use std::{
    collections::{
        BTreeMap,
        BTreeSet,
    },
    fmt::Debug,
    sync::Arc,
};

use anyhow::Context;
use async_trait::async_trait;
use common::{
    auth::AuthConfig,
    bootstrap_model::components::definition::ComponentDefinitionMetadata,
    components::{
        ComponentDefinitionPath,
        ComponentName,
        Resource,
    },
    document::udf_unix_timestamp,
    errors::JsError,
    execution_context::ExecutionContext,
    http::{
        fetch::FetchClient,
        RoutedHttpPath,
    },
    json::JsonForm,
    knobs::MAX_ISOLATE_WORKERS,
    log_lines::LogLine,
    persistence::RetentionValidator,
    query_journal::QueryJournal,
    runtime::{
        Runtime,
        UnixTimestamp,
    },
    schemas::DatabaseSchema,
    types::{
        ConvexOrigin,
        DeploymentMetadata,
        IndexId,
        ModuleEnvironment,
        RoutableMethod,
        UdfType,
    },
};
use database::{
    BootstrapMetadata,
    TableCountSnapshot,
    Transaction,
    TransactionTextSnapshot,
};
use dotnet_executor::{
    protocol::FunctionKind,
    DotNetExecutor,
};
use file_storage::TransactionalFileStorage;
use futures::FutureExt;
use indexing::index_reader::IndexReader;
use isolate::{
    client::{
        EnvironmentData,
        IsolateWorker,
    },
    IsolateClient,
};
use keybroker::{
    FunctionRunnerKeyBroker,
    Identity,
};
use model::{
    components::auth::propagate_component_auth,
    config::types::ModuleConfig,
    environment_variables::types::{
        EnvVarName,
        EnvVarValue,
    },
    modules::{
        module_versions::{
            AnalyzedModule,
            ModuleSource,
            SourceMap,
        },
        ModuleModel,
    },
    udf_config::types::UdfConfig,
};
use rand::Rng;

mod dotnet;
use storage::{
    Storage,
    StorageUseCase,
};
use sync_types::{
    CanonicalizedModulePath,
    Timestamp,
};
use tokio::sync::{
    mpsc,
    oneshot,
};
use udf::{
    validation::{
        ValidatedHttpPath,
        ValidatedPathAndArgs,
    },
    ActionCallbacks,
    EvaluateAppDefinitionsResult,
    FunctionOutcome,
    HttpActionRequest as HttpActionRequestInner,
    HttpActionResponseStreamer,
};
use usage_tracking::{
    FunctionUsageStats,
    FunctionUsageTracker,
};
use value::identifier::Identifier;

use super::in_memory_indexes::InMemoryIndexCache;
use crate::{
    module_cache::{
        CodeCache,
        FunctionRunnerModuleLoader,
        ModuleCache,
    },
    FunctionFinalTransaction,
    FunctionWrites,
};

pub struct RunRequestArgs {
    pub key_broker: FunctionRunnerKeyBroker,
    pub index_reader: Arc<dyn IndexReader>,
    pub convex_origin: ConvexOrigin,
    pub bootstrap_metadata: BootstrapMetadata,
    pub table_count_snapshot: Arc<dyn TableCountSnapshot>,
    pub text_index_snapshot: Arc<dyn TransactionTextSnapshot>,
    pub action_callbacks: Arc<dyn ActionCallbacks>,
    pub fetch_client: Arc<dyn FetchClient>,
    pub log_line_sender: Option<mpsc::UnboundedSender<LogLine>>,
    pub function_started_sender: Option<oneshot::Sender<()>>,
    pub udf_type: UdfType,
    pub identity: Identity,
    pub existing_writes: FunctionWrites,
    pub default_system_env_vars: BTreeMap<EnvVarName, EnvVarValue>,
    pub in_memory_index_last_modified: BTreeMap<IndexId, Timestamp>,
    pub context: ExecutionContext,
    pub subfunctions_in_same_isolate: bool,
    pub deployment: DeploymentMetadata,
}

#[derive(Clone)]
pub struct FunctionMetadata {
    pub path_and_args: ValidatedPathAndArgs,
    pub journal: QueryJournal,
}

pub struct HttpActionMetadata {
    pub http_response_streamer: HttpActionResponseStreamer,
    pub http_module_path: ValidatedHttpPath,
    pub routed_path: RoutedHttpPath,
    pub http_request: HttpActionRequestInner,
}

#[async_trait]
pub trait StorageForDeployment<RT: Runtime>: Debug + Clone + Send + Sync + 'static {
    /// Gets a storage impl for a deployment. Agnostic to what kind of storage -
    /// local or s3, or how it was loaded (e.g. passed directly within backend,
    /// loaded from a transaction created in Funrun)
    async fn storage_for_deployment(
        &self,
        transaction: &mut Transaction<RT>,
        use_case: StorageUseCase,
    ) -> anyhow::Result<Arc<dyn Storage>>;
}

#[derive(Clone, Debug)]
pub struct DeploymentStorage {
    pub files_storage: Arc<dyn Storage>,
    pub modules_storage: Arc<dyn Storage>,
}

#[async_trait]
impl<RT: Runtime> StorageForDeployment<RT> for DeploymentStorage {
    async fn storage_for_deployment(
        &self,
        _transaction: &mut Transaction<RT>,
        use_case: StorageUseCase,
    ) -> anyhow::Result<Arc<dyn Storage>> {
        match use_case {
            StorageUseCase::Files => Ok(self.files_storage.clone()),
            StorageUseCase::Modules => Ok(self.modules_storage.clone()),
            _ => anyhow::bail!("function runner storage does not support {use_case}"),
        }
    }
}

pub struct FunctionRunnerCore<RT: Runtime, S: StorageForDeployment<RT>> {
    rt: RT,
    storage: S,
    index_cache: InMemoryIndexCache<RT>,
    module_cache: ModuleCache<RT>,
    code_cache: CodeCache,
    isolate_client: IsolateClient<RT>,
    dotnet_executor: Option<DotNetExecutor>,
}

impl<RT: Runtime, S: StorageForDeployment<RT>> Clone for FunctionRunnerCore<RT, S> {
    fn clone(&self) -> Self {
        Self {
            rt: self.rt.clone(),
            storage: self.storage.clone(),
            index_cache: self.index_cache.clone(),
            module_cache: self.module_cache.clone(),
            code_cache: self.code_cache.clone(),
            isolate_client: self.isolate_client.clone(),
            dotnet_executor: self.dotnet_executor.clone(),
        }
    }
}

#[fastrace::trace]
pub async fn validate_run_function_result(
    udf_type: UdfType,
    ts: Timestamp,
    retention_validator: Arc<dyn RetentionValidator>,
) -> anyhow::Result<()> {
    match udf_type {
        // Since queries and mutations have no side effects, we perform the
        // retention check here, when validating the result.
        UdfType::Query | UdfType::Mutation => retention_validator
            .validate_snapshot(ts)
            .await
            .context("Function runner retention check changed"),
        // Since Actions can have side effects, we have to validate their
        // retention while we run them. We can't perform an additional check
        // here since actions can run longer than the retention.
        UdfType::Action | UdfType::HttpAction => Ok(()),
    }
}

impl<RT: Runtime, S: StorageForDeployment<RT>> FunctionRunnerCore<RT, S> {
    pub fn new<W: IsolateWorker<RT>>(
        rt: RT,
        storage: S,
        max_percent_per_client: usize,
        isolate_worker: W,
    ) -> anyhow::Result<Self> {
        let max_isolate_workers = *MAX_ISOLATE_WORKERS;
        let dotnet_executor = DotNetExecutor::from_environment()?;
        let isolate_client = IsolateClient::new(
            rt.clone(),
            max_percent_per_client,
            max_isolate_workers,
            isolate_worker,
        )?
        .with_native_executor(dotnet_executor.clone());
        let index_cache = InMemoryIndexCache::new(rt.clone());
        let module_cache = ModuleCache::new(rt.clone());
        let code_cache = CodeCache::new();

        Ok(Self {
            rt,
            storage,
            index_cache,
            module_cache,
            code_cache,
            isolate_client,
            dotnet_executor,
        })
    }

    pub fn active_isolate_workers(&self) -> usize {
        self.isolate_client.active_workers()
    }

    pub fn max_isolate_workers(&self) -> usize {
        self.isolate_client.max_workers()
    }

    pub async fn shutdown(&self) -> anyhow::Result<()> {
        if let Some(executor) = &self.dotnet_executor {
            executor.shutdown().await;
        }
        self.isolate_client.shutdown().await
    }

    // Runs a function given the information for the backend as well as arguments
    // to the function itself.
    // NOTE: The caller of this is responsible of checking retention by calling
    // `validate_function_runner_result`. If the retention check fails, we should
    // ignore any results or errors returned by this method.
    #[fastrace::trace]
    pub async fn run_function_no_retention_check(
        &self,
        run_request_args: RunRequestArgs,
        function_metadata: Option<FunctionMetadata>,
        http_action_metadata: Option<HttpActionMetadata>,
    ) -> anyhow::Result<(
        Option<FunctionFinalTransaction>,
        FunctionOutcome,
        FunctionUsageStats,
    )> {
        self.run_function_no_retention_check_inner(
            run_request_args,
            function_metadata,
            http_action_metadata,
        )
        .boxed()
        .await
    }

    pub async fn run_function_no_retention_check_inner(
        &self,
        RunRequestArgs {
            key_broker,
            index_reader,
            convex_origin,
            bootstrap_metadata,
            table_count_snapshot,
            text_index_snapshot,
            action_callbacks,
            fetch_client,
            log_line_sender,
            function_started_sender,
            udf_type,
            identity,
            existing_writes,
            default_system_env_vars,
            in_memory_index_last_modified,
            context,
            subfunctions_in_same_isolate,
            deployment,
        }: RunRequestArgs,
        function_metadata: Option<FunctionMetadata>,
        http_action_metadata: Option<HttpActionMetadata>,
    ) -> anyhow::Result<(
        Option<FunctionFinalTransaction>,
        FunctionOutcome,
        FunctionUsageStats,
    )> {
        let deployment_name = deployment.name.clone();
        let usage_tracker = FunctionUsageTracker::new();
        let mut transaction = self
            .index_cache
            .begin_tx(
                identity.clone(),
                existing_writes,
                index_reader,
                deployment_name.clone(),
                in_memory_index_last_modified,
                bootstrap_metadata,
                table_count_snapshot,
                text_index_snapshot,
                usage_tracker.clone(),
            )
            .await?;
        let storage = self
            .storage
            .storage_for_deployment(&mut transaction, StorageUseCase::Files)
            .await?;
        let file_storage = TransactionalFileStorage::new(self.rt.clone(), storage, convex_origin);
        let modules_storage = self
            .storage
            .storage_for_deployment(&mut transaction, StorageUseCase::Modules)
            .await?;

        let environment_data = EnvironmentData {
            key_broker,
            default_system_env_vars,
            file_storage,
            module_loader: Arc::new(FunctionRunnerModuleLoader {
                deployment_name: deployment_name.clone(),
                cache: self.module_cache.clone(),
                code_cache: self.code_cache.clone(),
                modules_storage,
            }),
            deployment,
        };

        match udf_type {
            UdfType::Query | UdfType::Mutation => {
                let FunctionMetadata {
                    path_and_args,
                    journal,
                } = function_metadata.context("Missing function metadata for query or mutation")?;
                // Initialize the UDF's RNG from some high-quality entropy. As with
                // `unix_timestamp` below, the UDF is only deterministic modulo this
                // system-generated input.
                let rng_seed = self.rt.rng().random();
                let unix_timestamp = udf_unix_timestamp(transaction.next_creation_time());
                let expected_kind = if udf_type == UdfType::Query {
                    FunctionKind::Query
                } else {
                    FunctionKind::Mutation
                };
                let native_target = isolate::environment::udf::dotnet::resolve_native_target(
                    self.dotnet_executor.as_ref(),
                    &mut transaction,
                    &environment_data,
                    path_and_args.path(),
                    expected_kind,
                )
                .await?;
                if let Some(target) = native_target {
                    let expected_kind = match udf_type {
                        UdfType::Query => FunctionKind::Query,
                        UdfType::Mutation => FunctionKind::Mutation,
                        UdfType::Action | UdfType::HttpAction => {
                            unreachable!("query/mutation branch")
                        },
                    };
                    anyhow::ensure!(
                        target.kind == expected_kind,
                        "native manifest function kind mismatch"
                    );
                    let request = isolate::client::UdfRequest {
                        udf_type,
                        path_and_args,
                        transaction,
                        journal,
                        context,
                        environment_data,
                        unix_timestamp,
                    };
                    let (tx, outcome) = isolate::environment::udf::dotnet::run_native_udf(
                        self.rt.clone(),
                        request,
                        deployment_name,
                        rng_seed,
                        target,
                        self.dotnet_executor
                            .clone()
                            .context("missing native executor")?,
                        self.isolate_client.clone(),
                        function_started_sender,
                    )
                    .await
                    .map_err(dotnet_executor::mark_native_execution)?;
                    return Ok((
                        Some(
                            tx.try_into()
                                .map_err(dotnet_executor::mark_native_execution)?,
                        ),
                        outcome,
                        usage_tracker.gather_user_stats(),
                    ));
                }
                let (tx, outcome) = self
                    .isolate_client
                    .execute_udf(
                        udf_type,
                        path_and_args,
                        transaction,
                        journal,
                        context,
                        environment_data,
                        rng_seed,
                        unix_timestamp,
                        0,
                        deployment_name,
                        function_started_sender,
                        subfunctions_in_same_isolate,
                    )
                    .await?;
                Ok((
                    Some(tx.try_into()?),
                    outcome,
                    usage_tracker.gather_user_stats(),
                ))
            },
            UdfType::Action => {
                let FunctionMetadata { path_and_args, .. } =
                    function_metadata.context("Missing function metadata for action")?;
                let log_line_sender =
                    log_line_sender.context("Missing log line sender for action")?;
                let native_target = isolate::environment::udf::dotnet::resolve_native_target(
                    self.dotnet_executor.as_ref(),
                    &mut transaction,
                    &environment_data,
                    path_and_args.path(),
                    FunctionKind::Action,
                )
                .await?;
                if let Some(target) = native_target {
                    let outcome = isolate::environment::action::dotnet::run_native_action(
                        self.rt.clone(),
                        path_and_args,
                        transaction,
                        identity,
                        environment_data,
                        action_callbacks,
                        fetch_client,
                        log_line_sender,
                        context,
                        target,
                        self.dotnet_executor
                            .clone()
                            .context("missing native executor")?,
                        function_started_sender,
                    )
                    .await
                    .map_err(dotnet_executor::mark_native_execution)?;
                    return Ok((
                        None,
                        FunctionOutcome::Action(outcome),
                        usage_tracker.gather_user_stats(),
                    ));
                }
                let outcome = self
                    .isolate_client
                    .execute_action(
                        path_and_args,
                        transaction,
                        action_callbacks,
                        fetch_client,
                        log_line_sender,
                        context,
                        environment_data,
                        deployment_name,
                        function_started_sender,
                    )
                    .await?;
                Ok((
                    None,
                    FunctionOutcome::Action(outcome),
                    usage_tracker.gather_user_stats(),
                ))
            },
            UdfType::HttpAction => {
                let HttpActionMetadata {
                    mut http_response_streamer,
                    http_module_path,
                    routed_path,
                    http_request,
                } = http_action_metadata.context("Missing http action metadata")?;
                let log_line_sender =
                    log_line_sender.context("Missing log line sender for http action")?;
                // Set the proper identity for component HTTP actions. Note that for HTTP,
                // the component is both the caller and the callee.
                let component_id = http_module_path.path().component;
                let identity =
                    propagate_component_auth(&identity, component_id, component_id.is_root());
                let native_route = if self.dotnet_executor.is_some() {
                    let metadata = ModuleModel::new(&mut transaction)
                        .get_metadata_for_function_by_id(http_module_path.path())
                        .await?
                        .context("native HTTP owning router metadata missing")?;
                    let routes = metadata
                        .analyze_result
                        .as_ref()
                        .and_then(|module| module.http_routes.as_ref())
                        .context("native HTTP owning route analysis missing")?;
                    let method: RoutableMethod = http_request.head.method.clone().try_into()?;
                    // Preserve SDK exact-before-longest-prefix routing and
                    // normalization of incoming HEAD to the registered GET.
                    let selected =
                        routes
                            .iter()
                            .find(|analyzed| {
                                analyzed.route.method == method
                                    && analyzed.route.path == routed_path.0.as_str()
                            })
                            .or_else(|| {
                                routes
                                    .iter()
                                    .filter(|analyzed| {
                                        analyzed.route.method == method
                                            && analyzed.route.path.strip_suffix('*').is_some_and(
                                                |prefix| routed_path.starts_with(prefix),
                                            )
                                    })
                                    .max_by_key(|analyzed| analyzed.route.path.len())
                            });
                    if let Some(analyzed) = selected {
                        self.resolve_native_http_target(
                            &mut transaction,
                            &environment_data,
                            http_module_path.path(),
                            &analyzed.route,
                        )
                        .await?
                        .map(|target| (target, analyzed.route.clone()))
                    } else if metadata.environment == ModuleEnvironment::DotNet {
                        // Native source is an opaque capsule. An unmatched
                        // owning route must return the original 404 response,
                        // never fall through to JavaScript evaluation.
                        if let Some(started) = function_started_sender {
                            let _ = started.send(());
                        }
                        for part in udf::HttpActionResponsePart::from_text(
                            http::StatusCode::NOT_FOUND,
                            "No matching routes found".into(),
                        ) {
                            http_response_streamer.send_part(part)??;
                        }
                        let outcome = udf::HttpActionOutcome::new(
                            None,
                            http_request.head,
                            identity.into(),
                            self.rt.unix_timestamp(),
                            udf::HttpActionResult::Streamed,
                            None,
                            http_module_path.npm_version().clone(),
                            std::time::Duration::ZERO,
                        )
                        .with_memory_in_mb(0)
                        .with_native_execution(true);
                        return Ok((
                            None,
                            FunctionOutcome::HttpAction(outcome),
                            usage_tracker.gather_user_stats(),
                        ));
                    } else {
                        None
                    }
                } else {
                    None
                };
                if let Some((target, route)) = native_route {
                    let outcome = isolate::environment::action::dotnet::run_native_http_action(
                        self.rt.clone(),
                        http_module_path,
                        route,
                        http_request,
                        transaction,
                        identity,
                        environment_data,
                        action_callbacks,
                        fetch_client,
                        log_line_sender,
                        http_response_streamer,
                        context,
                        target,
                        self.dotnet_executor
                            .clone()
                            .context("missing native HTTP executor")?,
                        function_started_sender,
                    )
                    .await
                    .map_err(dotnet_executor::mark_native_execution)?;
                    return Ok((
                        None,
                        FunctionOutcome::HttpAction(outcome),
                        usage_tracker.gather_user_stats(),
                    ));
                }
                let outcome = self
                    .isolate_client
                    .execute_http_action(
                        http_module_path,
                        routed_path,
                        http_request,
                        identity,
                        action_callbacks,
                        fetch_client,
                        log_line_sender,
                        http_response_streamer,
                        transaction,
                        context,
                        environment_data,
                        deployment_name,
                        function_started_sender,
                    )
                    .await?;
                Ok((
                    None,
                    FunctionOutcome::HttpAction(outcome),
                    usage_tracker.gather_user_stats(),
                ))
            },
        }
    }

    pub async fn analyze(
        &self,
        udf_config: UdfConfig,
        modules: BTreeMap<CanonicalizedModulePath, ModuleConfig>,
        environment_variables: BTreeMap<EnvVarName, EnvVarValue>,
        deployment_name: String,
    ) -> anyhow::Result<Result<BTreeMap<CanonicalizedModulePath, AnalyzedModule>, JsError>> {
        let mut native = BTreeMap::new();
        let mut isolate = BTreeMap::new();
        for (path, module) in modules {
            match module.environment {
                ModuleEnvironment::DotNet => {
                    native.insert(path, module);
                },
                ModuleEnvironment::Isolate => {
                    isolate.insert(path, module);
                },
                _ => anyhow::bail!("only Isolate/DotNet modules can use the core analyzer"),
            }
        }
        let mut result = if isolate.is_empty() {
            BTreeMap::new()
        } else {
            match self
                .isolate_client
                .analyze(
                    udf_config.clone(),
                    isolate,
                    environment_variables.clone(),
                    deployment_name,
                )
                .await?
            {
                Ok(value) => value,
                Err(error) => return Ok(Err(error)),
            }
        };
        for (path, module) in native {
            let analyzed = self
                .analyze_native_module(&path, &module, &udf_config, environment_variables.clone())
                .await
                .map_err(|error| {
                    error.context(errors::ErrorMetadata::bad_request(
                        "InvalidNativeCapsule",
                        "Native module admission failed",
                    ))
                })?;
            anyhow::ensure!(
                result.insert(path, analyzed).is_none(),
                "duplicate native module"
            );
        }
        Ok(Ok(result))
    }

    #[fastrace::trace]
    pub async fn evaluate_app_definitions(
        &self,
        app_definition: ModuleConfig,
        component_definitions: BTreeMap<ComponentDefinitionPath, ModuleConfig>,
        dependency_graph: BTreeSet<(ComponentDefinitionPath, ComponentDefinitionPath)>,
        user_environment_variables: BTreeMap<EnvVarName, EnvVarValue>,
        system_env_vars: BTreeMap<EnvVarName, EnvVarValue>,
        deployment_name: String,
    ) -> anyhow::Result<EvaluateAppDefinitionsResult> {
        for definition in std::iter::once(&app_definition).chain(component_definitions.values()) {
            anyhow::ensure!(
                definition.environment != ModuleEnvironment::DotNet
                    || dotnet::is_capsule(&definition.source),
                errors::ErrorMetadata::bad_request(
                    "NativeComponentUnsupported",
                    "A native app/component definition requires a frozen native capsule"
                )
            );
        }
        anyhow::ensure!(
            matches!(
                app_definition.environment,
                ModuleEnvironment::Isolate | ModuleEnvironment::DotNet
            ),
            "Unsupported definition runtime"
        );
        anyhow::ensure!(
            component_definitions.values().all(|m| matches!(
                m.environment,
                ModuleEnvironment::Isolate | ModuleEnvironment::DotNet
            )),
            "Unsupported definition runtime"
        );

        let native_evaluator = self.dotnet_executor.as_ref().map(|executor| {
            Arc::new(dotnet::NativeComponentsEvaluator {
                executor: executor.clone(),
            })
                as Arc<dyn isolate::environment::component_definitions::NativeDefinitionEvaluator>
        });

        self.isolate_client
            .evaluate_app_definitions_with_native(
                app_definition,
                component_definitions,
                dependency_graph,
                user_environment_variables,
                system_env_vars,
                deployment_name,
                native_evaluator,
            )
            .await
    }

    #[fastrace::trace]
    pub async fn evaluate_component_initializer(
        &self,
        evaluated_definitions: BTreeMap<ComponentDefinitionPath, ComponentDefinitionMetadata>,
        path: ComponentDefinitionPath,
        definition: ModuleConfig,
        args: BTreeMap<Identifier, Resource>,
        name: ComponentName,
        deployment_name: String,
    ) -> anyhow::Result<BTreeMap<Identifier, Resource>> {
        if definition.environment == ModuleEnvironment::DotNet {
            let evaluator = dotnet::NativeComponentsEvaluator {
                executor: self
                    .dotnet_executor
                    .as_ref()
                    .context("native component runtime is not configured")?
                    .clone(),
            };
            return evaluator
                .initialize(&evaluated_definitions, &path, &definition, args, name)
                .await;
        }
        self.isolate_client
            .evaluate_component_initializer(
                evaluated_definitions,
                path,
                definition,
                args,
                name,
                deployment_name,
            )
            .await
    }

    #[fastrace::trace]
    pub async fn evaluate_schema(
        &self,
        schema_bundle: ModuleSource,
        source_map: Option<SourceMap>,
        rng_seed: [u8; 32],
        unix_timestamp: UnixTimestamp,
        deployment_name: String,
    ) -> anyhow::Result<DatabaseSchema> {
        if dotnet::is_capsule(&schema_bundle) {
            anyhow::ensure!(
                source_map.is_none(),
                "native schema source map is unsupported"
            );
            let (capsule, catalog) = self
                .describe_native(&schema_bundle, rng_seed, unix_timestamp, BTreeMap::new())
                .await?;
            anyhow::ensure!(
                capsule.module_kind == dotnet_executor::capsule::ModuleKind::Schema,
                "native schema capsule kind mismatch"
            );
            capsule.check_module_path("schema.js")?;
            return DatabaseSchema::json_deserialize(
                &dotnet::selected_definition(&capsule, &catalog)?.to_string(),
            );
        }
        self.isolate_client
            .evaluate_schema(
                schema_bundle,
                source_map,
                rng_seed,
                unix_timestamp,
                deployment_name,
            )
            .await
    }

    #[fastrace::trace]
    pub async fn evaluate_auth_config(
        &self,
        auth_config_bundle: ModuleSource,
        source_map: Option<SourceMap>,
        environment_variables: BTreeMap<EnvVarName, EnvVarValue>,
        explanation: &str,
        deployment_name: String,
    ) -> anyhow::Result<AuthConfig> {
        if dotnet::is_capsule(&auth_config_bundle) {
            anyhow::ensure!(
                source_map.is_none(),
                "native auth source map is unsupported"
            );
            let (capsule, catalog) = self
                .describe_native(
                    &auth_config_bundle,
                    [0; 32],
                    self.rt.unix_timestamp(),
                    environment_variables,
                )
                .await?;
            anyhow::ensure!(
                capsule.module_kind == dotnet_executor::capsule::ModuleKind::Auth,
                "native auth capsule kind mismatch"
            );
            capsule.check_module_path("auth.config.js")?;
            let auth: common::auth::SerializedAuthConfig =
                serde_json::from_value(dotnet::selected_definition(&capsule, &catalog)?)?;
            return auth.try_into();
        }
        self.isolate_client
            .evaluate_auth_config(
                auth_config_bundle,
                source_map,
                environment_variables,
                explanation,
                deployment_name,
            )
            .await
    }
}
