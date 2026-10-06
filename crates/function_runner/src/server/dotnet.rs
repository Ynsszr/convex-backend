//! Native definitions are evaluated in a bounded import-only worker. Their
//! results feed the same owning validators and atomic deployment metadata as
//! JavaScript analysis.
use std::{
    collections::BTreeMap,
    str::FromStr,
};

use anyhow::Context;
use async_trait::async_trait;
use common::{
    bootstrap_model::components::definition::{
        ComponentDefinitionMetadata,
        SerializedComponentDefinitionMetadata,
    },
    components::{
        ComponentDefinitionPath,
        ComponentName,
        Resource,
    },
    runtime::{
        Runtime,
        UnixTimestamp,
    },
    types::{
        EnvVarName,
        EnvVarValue,
        HttpActionRoute,
        UdfType,
    },
};
use dotnet_executor::{
    capsule::{
        Capsule,
        Catalog,
        ModuleKind,
    },
    protocol::WorkerError,
    DotNetExecutor,
    SyscallHandler,
};
use errors::ErrorMetadataAnyhowExt;
use futures::future::BoxFuture;
use isolate::environment::component_definitions::NativeDefinitionEvaluator;
use model::{
    config::types::ModuleConfig,
    modules::{
        function_validators::{
            ArgsValidator,
            ArgsValidatorJson,
            ReturnsValidator,
            ReturnsValidatorJson,
        },
        module_versions::{
            AnalyzedFunction,
            AnalyzedHttpRoute,
            AnalyzedHttpRoutes,
            AnalyzedModule,
            Visibility,
        },
    },
    udf_config::types::UdfConfig,
};
use rand::{
    Rng,
    SeedableRng,
};
use rand_chacha::ChaCha12Rng;
use serde_json::{
    json,
    Value,
};
use sync_types::CanonicalizedModulePath;
use value::{
    identifier::Identifier,
    ConvexObject,
    FieldName,
};

use super::{
    FunctionRunnerCore,
    StorageForDeployment,
};

struct ImportProvider {
    rng: ChaCha12Rng,
    time_ms: u64,
    environment: BTreeMap<EnvVarName, EnvVarValue>,
    kind: ModuleKind,
    issued_errors: BTreeMap<String, String>,
    component_environment_allowed: bool,
    component_definitions: BTreeMap<ComponentDefinitionPath, ComponentDefinitionMetadata>,
}
#[async_trait]
impl SyscallHandler for ImportProvider {
    async fn syscall(
        &mut self,
        name: &str,
        args: Value,
        is_async: bool,
    ) -> anyhow::Result<Result<Value, WorkerError>> {
        let result = (|| -> anyhow::Result<Value> {
            anyhow::ensure!(
                !is_async,
                "asynchronous operations are unavailable during native import"
            );
            match name {
                "dotnet/now" => {
                    anyhow::ensure!(
                        self.kind != ModuleKind::Component,
                        errors::ErrorMetadata::bad_request(
                            "NoDateDuringDefinitionEvaluation",
                            "Date unsupported when evaluating app definition"
                        )
                    );
                    anyhow::ensure!(
                        self.kind != ModuleKind::Auth,
                        errors::ErrorMetadata::bad_request(
                            "NoDateDuringAuthConfig",
                            "Date unsupported when evaluating auth config file"
                        )
                    );
                    Ok(json!(self.time_ms))
                },
                "dotnet/random" => {
                    anyhow::ensure!(
                        self.kind != ModuleKind::Component,
                        errors::ErrorMetadata::bad_request(
                            "NoRandomDuringDefinitionEvaluation",
                            "Math.random unsupported when evaluating app definition"
                        )
                    );
                    anyhow::ensure!(
                        self.kind != ModuleKind::Auth,
                        errors::ErrorMetadata::bad_request(
                            "NoRandomDuringAuthConfig",
                            "Math.random unsupported when evaluating auth config file"
                        )
                    );
                    Ok(json!(self.rng.random::<f64>()))
                },
                "dotnet/environmentVariable" => {
                    anyhow::ensure!(
                        self.kind != ModuleKind::Component || self.component_environment_allowed,
                        errors::ErrorMetadata::bad_request(
                            "EnvironmentVariablesUnsupported",
                            "Environment variables are only supported in the app's convex.config.ts. Learn more at https://docs.convex.dev/components/authoring#environment-variables"
                        )
                    );
                    anyhow::ensure!(
                        self.kind != ModuleKind::Schema,
                        errors::ErrorMetadata::bad_request(
                            "NoEnvironmentVariablesInSchema",
                            "Environment variables unsupported when evaluating schema"
                        )
                    );
                    let name: EnvVarName = args
                        .get("name")
                        .and_then(Value::as_str)
                        .context("environmentVariable requires a string name")?
                        .parse()?;
                    anyhow::ensure!(
                        self.kind != ModuleKind::Auth || self.environment.contains_key(&name),
                        errors::ErrorMetadata::bad_request(
                            "AuthConfigMissingEnvironmentVariable",
                            format!(
                                "Environment variable {name} is used in auth config file but its \
                                 value was not set"
                            )
                        )
                    );
                    Ok(self
                        .environment
                        .get(&name)
                        .map(|v| json!(v.to_string()))
                        .unwrap_or(Value::Null))
                },
                "dotnet/componentDefinition" => {
                    anyhow::ensure!(self.kind == ModuleKind::Component, "not a component import");
                    let path: ComponentDefinitionPath = args
                        .get("path")
                        .and_then(Value::as_str)
                        .context("componentDefinition requires a string path")?
                        .parse()?;
                    let definition = self.component_definitions.get(&path).context(
                        errors::ErrorMetadata::bad_request(
                            "UnknownComponentDefinition",
                            "Component dependency has not been evaluated by the owning graph",
                        ),
                    )?;
                    anyhow::ensure!(
                        !definition.is_app(),
                        errors::ErrorMetadata::bad_request(
                            "NoImportAppDuringDefinitionEvaluation",
                            "Cannot import an app definition as a component"
                        )
                    );
                    Ok(serde_json::to_value(
                        SerializedComponentDefinitionMetadata::try_from(definition.clone())?,
                    )?)
                },
                _ => anyhow::bail!("syscall {name} is unavailable during native import"),
            }
        })();
        Ok(result.map_err(|error| {
            let code: String = if error.downcast_ref::<errors::ErrorMetadata>().is_some() {
                error.short_msg().into()
            } else {
                "NoSyscallDuringImport".into()
            };
            let message = error.to_string();
            self.issued_errors.insert(code.clone(), message.clone());
            WorkerError {
                code,
                message,
                data: None,
            }
        }))
    }
}

async fn describe_with_provider(
    executor: &DotNetExecutor,
    capsule: &Capsule,
    time_ms: f64,
    provider: &mut ImportProvider,
) -> anyhow::Result<Catalog> {
    let artifacts = executor.load_capsule(capsule).await?;
    let catalog = executor
        .describe_selected(
            &artifacts,
            time_ms,
            Some(capsule.module_kind),
            capsule.export.clone(),
            provider,
        )
        .await
        .map_err(|error| {
            if let Some(definition) = error.downcast_ref::<dotnet_executor::DefinitionError>() {
                // Workers cannot forge backend authority/OCC/error classifications.
                let code = if provider.issued_errors.get(&definition.0.code)
                    == Some(&definition.0.message)
                {
                    definition.0.code.clone()
                } else {
                    "InvalidNativeDefinition".into()
                };
                let message = definition.0.message.clone();
                error.context(errors::ErrorMetadata::bad_request(code, message))
            } else {
                error
            }
        })?;
    capsule.validate_catalog(&catalog)?;
    Ok(catalog)
}

pub(super) struct NativeComponentsEvaluator {
    pub executor: DotNetExecutor,
}

impl NativeDefinitionEvaluator for NativeComponentsEvaluator {
    fn evaluate<'a>(
        &'a self,
        path: &'a ComponentDefinitionPath,
        definition: &'a ModuleConfig,
        evaluated_components: &'a BTreeMap<ComponentDefinitionPath, ComponentDefinitionMetadata>,
        environment_variables: Option<BTreeMap<EnvVarName, EnvVarValue>>,
    ) -> BoxFuture<'a, anyhow::Result<ComponentDefinitionMetadata>> {
        Box::pin(async move {
            anyhow::ensure!(
                definition.source_map.is_none(),
                "native definition has a JS map"
            );
            let capsule = Capsule::parse(&definition.source)?;
            anyhow::ensure!(
                capsule.module_kind == ModuleKind::Component,
                errors::ErrorMetadata::bad_request(
                    "InvalidNativeDefinition",
                    "Native app/component configuration requires a component capsule"
                )
            );
            capsule.check_module_path("convex.config.js")?;
            let mut provider = ImportProvider {
                rng: ChaCha12Rng::from_seed([0; 32]),
                time_ms: 0,
                component_environment_allowed: environment_variables.is_some(),
                environment: environment_variables.unwrap_or_default(),
                kind: ModuleKind::Component,
                component_definitions: evaluated_components.clone(),
                issued_errors: BTreeMap::new(),
            };
            let catalog =
                describe_with_provider(&self.executor, &capsule, 0.0, &mut provider).await?;
            let mut value = selected_definition(&capsule, &catalog)?;
            let object = value
                .as_object_mut()
                .context("native definition must be an object")?;
            // The owning deployment graph supplies this namespace. Deployment
            // code has no filesystem path or component-identity authority.
            object.insert("path".into(), json!(String::from(path.clone())));
            let serialized: SerializedComponentDefinitionMetadata = serde_json::from_value(value)
                .map_err(|error| {
                errors::ErrorMetadata::bad_request("InvalidDefinition", error.to_string())
            })?;
            ComponentDefinitionMetadata::try_from(serialized).map_err(|error| {
                errors::ErrorMetadata::bad_request("InvalidDefinition", error.to_string()).into()
            })
        })
    }
}

impl NativeComponentsEvaluator {
    /// Invoked only by the original deployment InitializerEvaluator. Namespace,
    /// parent arguments and the selected child belong to its typechecker.
    pub(super) async fn initialize(
        &self,
        evaluated: &BTreeMap<ComponentDefinitionPath, ComponentDefinitionMetadata>,
        path: &ComponentDefinitionPath,
        definition: &ModuleConfig,
        args: BTreeMap<Identifier, Resource>,
        name: ComponentName,
    ) -> anyhow::Result<BTreeMap<Identifier, Resource>> {
        anyhow::ensure!(
            definition.source_map.is_none(),
            "native definition has a JS map"
        );
        let capsule = Capsule::parse(&definition.source)?;
        capsule.check_module_path("convex.config.js")?;
        anyhow::ensure!(
            capsule.module_kind == ModuleKind::Component,
            "native initializer requires a component capsule"
        );
        let admitted = evaluated
            .get(path)
            .context(errors::ErrorMetadata::bad_request(
                "InvalidDefinition",
                "Owning component definition not found",
            ))?;
        anyhow::ensure!(
            admitted.path == *path && !admitted.is_app(),
            errors::ErrorMetadata::bad_request(
                "InvalidDefinition",
                "Initializer requires its owning child component definition"
            )
        );
        let mut children = admitted
            .child_components
            .iter()
            .filter(|child| child.name == name);
        let child = children.next().context(errors::ErrorMetadata::bad_request(
            "InvalidDefinition",
            "Owning initializer child not found",
        ))?;
        anyhow::ensure!(
            children.next().is_none()
                && child.args.is_none()
                && evaluated.contains_key(&child.path),
            errors::ErrorMetadata::bad_request(
                "InvalidDefinition",
                "Initializer child does not match owning definition"
            )
        );
        let mut input = BTreeMap::new();
        for (arg_name, value) in args {
            let Resource::Value(value) = value else {
                anyhow::bail!(errors::ErrorMetadata::bad_request(
                    "InvalidDefinition",
                    format!("Argument {arg_name} is not a value")
                ));
            };
            input.insert(FieldName::from_str(&arg_name)?, value);
        }
        let args = ConvexObject::try_from(input)?;
        let mut provider = ImportProvider {
            rng: ChaCha12Rng::from_seed([0; 32]),
            time_ms: 0,
            environment: BTreeMap::new(),
            kind: ModuleKind::Component,
            issued_errors: BTreeMap::new(),
            component_environment_allowed: false,
            component_definitions: evaluated.clone(),
        };
        // Reimport under the original initializer's no-environment profile.
        // Keep exact raw export bytes for the host equality fence; the owner
        // parses them into its canonical metadata before allowing the callback.
        let catalog = describe_with_provider(&self.executor, &capsule, 0.0, &mut provider).await?;
        let value = selected_definition(&capsule, &catalog)?;
        let mut fenced = value.clone();
        fenced
            .as_object_mut()
            .context("native definition must be an object")?
            .insert("path".into(), json!(String::from(path.clone())));
        let reimported = ComponentDefinitionMetadata::try_from(serde_json::from_value::<
            SerializedComponentDefinitionMetadata,
        >(fenced)?)?;
        anyhow::ensure!(
            reimported == *admitted,
            errors::ErrorMetadata::bad_request(
                "InvalidDefinition",
                "Initializer definition changed after owning evaluation"
            )
        );
        let artifacts = self.executor.load_capsule(&capsule).await?;
        let result = self
            .executor
            .initialize_component(
                &artifacts,
                capsule
                    .export
                    .clone()
                    .context("native definition export missing")?,
                String::from(path.clone()),
                String::from(name),
                value,
                serde_json::from_str(&args.json_serialize()?)?,
                &mut provider,
            )
            .await
            .map_err(|error| {
                if let Some(definition) = error.downcast_ref::<dotnet_executor::DefinitionError>() {
                    let code = if provider.issued_errors.get(&definition.0.code)
                        == Some(&definition.0.message)
                    {
                        definition.0.code.clone()
                    } else {
                        "InvalidDefinition".into()
                    };
                    let message = definition.0.message.clone();
                    error.context(errors::ErrorMetadata::bad_request(code, message))
                } else {
                    error
                }
            })?;
        // Existing Convex value/identifier validation and the child's original
        // check_args own admissible arguments. CLR supplies no references or IDs.
        let result = ConvexObject::try_from(result)?;
        let mut resources = BTreeMap::new();
        for (arg_name, value) in BTreeMap::from(result) {
            resources.insert(arg_name.parse()?, Resource::Value(value));
        }
        Ok(resources)
    }
}

pub(super) fn is_capsule(source: &str) -> bool {
    serde_json::from_str::<Value>(source)
        .ok()
        .and_then(|v| {
            v.get("format")
                .and_then(Value::as_str)
                .map(|s| s == "convex-dotnet-capsule")
        })
        .unwrap_or(false)
}

pub(super) fn selected_definition(capsule: &Capsule, catalog: &Catalog) -> anyhow::Result<Value> {
    let export = capsule
        .export
        .as_deref()
        .context("native definition export missing")?;
    let value = match capsule.module_kind {
        ModuleKind::Schema => &catalog.schemas,
        ModuleKind::Auth => &catalog.auth_configs,
        ModuleKind::Crons => &catalog.crons,
        ModuleKind::Component => &catalog.component_definitions,
        ModuleKind::Http => {
            let router = catalog
                .http_routers
                .iter()
                .find(|r| r.export == export)
                .context("native HTTP router export missing")?;
            let routes = router
                .routes
                .iter()
                .map(|route| {
                    Ok(dotnet_executor::capsule::HttpRoute {
                        method: route.method.clone(),
                        path: route.path.clone(),
                        function_path: capsule
                            .address_for_entry_point(&route.function_path)?
                            .to_string(),
                    })
                })
                .collect::<anyhow::Result<Vec<_>>>()?;
            let value = json!({"routes":routes});
            anyhow::ensure!(
                capsule.definition.as_ref() == Some(&value),
                "native HTTP route catalogue does not match uploaded definition"
            );
            return Ok(value);
        },
        ModuleKind::Functions => anyhow::bail!("function capsule is not a definition"),
    }
    .iter()
    .find(|definition| definition.export == export)
    .context("native definition export missing")?
    .value
    .clone();
    // Authentication imports are reevaluated when owning deployment variables
    // change. Their frozen code and exact export bind the definition, rather
    // than a snapshot of providers materialized at packaging time.
    if let Some(expected) = &capsule.definition {
        anyhow::ensure!(
            *expected == value,
            "native definition catalogue does not match uploaded definition"
        );
    }
    Ok(value)
}

impl<RT: Runtime, S: StorageForDeployment<RT>> FunctionRunnerCore<RT, S> {
    pub(super) async fn resolve_native_http_target(
        &self,
        transaction: &mut database::Transaction<RT>,
        environment: &isolate::client::EnvironmentData<RT>,
        router_path: &common::components::ResolvedComponentFunctionPath,
        route: &HttpActionRoute,
    ) -> anyhow::Result<Option<dotnet_executor::manifest::NativeFunction>> {
        let metadata = model::modules::ModuleModel::new(transaction)
            .get_metadata_for_function_by_id(router_path)
            .await?
            .context("native HTTP router metadata missing")?;
        if metadata.environment != common::types::ModuleEnvironment::DotNet {
            return Ok(self.dotnet_executor.as_ref().and_then(|executor| {
                executor.find_http(
                    &environment.deployment.name,
                    &router_path.component_path.to_string(),
                    &route.method.to_string(),
                    &route.path,
                )
            }));
        }
        let executor = self
            .dotnet_executor
            .as_ref()
            .context("native runtime is not configured")?;
        let package = model::source_packages::SourcePackageModel::new(
            transaction,
            router_path.component.into(),
        )
        .get(metadata.source_package_id)
        .await?;
        let source = environment
            .module_loader
            .get_module_with_metadata(&metadata, &package)
            .await?;
        let capsule = Capsule::parse(&source.source().to_utf8())?;
        capsule.check_module_path("http.js")?;
        anyhow::ensure!(
            capsule.module_kind == ModuleKind::Http,
            "native HTTP router capsule kind mismatch"
        );
        let routes: Vec<dotnet_executor::capsule::HttpRoute> = serde_json::from_value(
            capsule
                .definition
                .as_ref()
                .and_then(|value| value.get("routes"))
                .context("admitted native HTTP bindings missing")?
                .clone(),
        )?;
        let binding = routes
            .iter()
            .find(|binding| {
                binding.method == route.method.to_string() && binding.path == route.path
            })
            .context("admitted native HTTP route handler missing")?;
        let handler_path = common::components::ResolvedComponentFunctionPath {
            component: router_path.component,
            component_path: router_path.component_path.clone(),
            udf_path: binding
                .function_path
                .parse::<sync_types::UdfPath>()?
                .canonicalize(),
        };
        let mut target = isolate::environment::udf::dotnet::resolve_native_target(
            Some(executor),
            transaction,
            environment,
            &handler_path,
            dotnet_executor::protocol::FunctionKind::HttpAction,
        )
        .await?
        .context("native HTTP handler runtime missing")?;
        anyhow::ensure!(
            target
                .entry_point
                .as_deref()
                .unwrap_or(&target.function_path)
                == capsule.entry_point_for(&binding.function_path),
            "native HTTP router and handler alias binding disagree"
        );
        anyhow::ensure!(
            target.assembly_sha256 == capsule.assembly.sha256,
            "native HTTP router and handler artifact disagree"
        );
        anyhow::ensure!(
            target.assembly_dependencies.len() == capsule.assembly_dependencies.len()
                && target
                    .assembly_dependencies
                    .iter()
                    .zip(&capsule.assembly_dependencies)
                    .all(|(admitted, uploaded)| admitted.sha256 == uploaded.sha256
                        && admitted.path.file_name().and_then(|name| name.to_str())
                            == Some(uploaded.name.as_str())),
            "native HTTP router and handler dependencies disagree"
        );
        target.http_route = Some(dotnet_executor::manifest::NativeHttpRoute {
            module_path: "http.js".into(),
            module_sha256: metadata.sha256.as_base64(),
            method: route.method.to_string(),
            path: route.path.clone(),
        });
        Ok(Some(target))
    }

    pub(super) async fn describe_native(
        &self,
        source: &str,
        rng_seed: [u8; 32],
        time: UnixTimestamp,
        environment: BTreeMap<EnvVarName, EnvVarValue>,
    ) -> anyhow::Result<(Capsule, Catalog)> {
        let capsule = Capsule::parse(source)?;
        let executor = self
            .dotnet_executor
            .as_ref()
            .context("native runtime admission is not configured")?;
        let time_ms = time.as_ms_since_epoch()?;
        let mut provider = ImportProvider {
            rng: ChaCha12Rng::from_seed(rng_seed),
            time_ms,
            environment,
            kind: capsule.module_kind,
            issued_errors: BTreeMap::new(),
            component_environment_allowed: false,
            component_definitions: BTreeMap::new(),
        };
        let catalog =
            describe_with_provider(executor, &capsule, time_ms as f64, &mut provider).await?;
        Ok((capsule, catalog))
    }

    pub(super) async fn analyze_native_module(
        &self,
        path: &CanonicalizedModulePath,
        module: &ModuleConfig,
        config: &UdfConfig,
        environment: BTreeMap<EnvVarName, EnvVarValue>,
    ) -> anyhow::Result<AnalyzedModule> {
        anyhow::ensure!(
            module.source_map.is_none(),
            "native capsules do not admit JavaScript source maps"
        );
        let (capsule, catalog) = self
            .describe_native(
                &module.source,
                config.import_phase_rng_seed,
                config.import_phase_unix_timestamp,
                environment,
            )
            .await?;
        capsule.check_module_path(path.as_str())?;
        let mut result = AnalyzedModule::default();
        match capsule.module_kind {
            ModuleKind::Functions => {
                let logical = path.as_str().strip_suffix(".js").unwrap_or(path.as_str());
                let mut functions = Vec::new();
                let mut bindings = BTreeMap::new();
                for function in &catalog.functions {
                    let (module, _) = function
                        .function_path
                        .rsplit_once(':')
                        .context("invalid native function path")?;
                    if module == logical
                        && !capsule
                            .function_aliases
                            .iter()
                            .any(|alias| alias.function_path == function.function_path)
                    {
                        bindings.insert(function.function_path.clone(), function);
                    }
                }
                for alias in &capsule.function_aliases {
                    let (module, _) = alias
                        .function_path
                        .rsplit_once(':')
                        .context("invalid durable address")?;
                    if module == logical {
                        let function = catalog
                            .functions
                            .iter()
                            .find(|function| function.function_path == alias.entry_point)
                            .context("native alias target missing from catalog")?;
                        bindings.insert(alias.function_path.clone(), function);
                    }
                }
                let export_count = bindings.len();
                for (address, function) in bindings {
                    let (_, name) = address
                        .rsplit_once(':')
                        .context("invalid durable address")?;
                    // HTTP handlers are admitted through their owning router,
                    // not exposed as ordinary client-callable functions.
                    if function.kind == dotnet_executor::protocol::FunctionKind::HttpAction {
                        continue;
                    }
                    let kind = match function.kind {
                        dotnet_executor::protocol::FunctionKind::Query => UdfType::Query,
                        dotnet_executor::protocol::FunctionKind::Mutation => UdfType::Mutation,
                        dotnet_executor::protocol::FunctionKind::Action => UdfType::Action,
                        dotnet_executor::protocol::FunctionKind::HttpAction => UdfType::HttpAction,
                    };
                    let args: ArgsValidator =
                        serde_json::from_value::<ArgsValidatorJson>(function.arguments.clone())?
                            .try_into()?;
                    let returns: ReturnsValidator =
                        serde_json::from_value::<ReturnsValidatorJson>(function.returns.clone())?
                            .try_into()?;
                    let visibility = if function.visibility == "public" {
                        Visibility::Public
                    } else {
                        Visibility::Internal
                    };
                    functions.push(AnalyzedFunction::new(
                        name.parse()?,
                        None,
                        kind,
                        Some(visibility),
                        args,
                        returns,
                    )?);
                }
                anyhow::ensure!(
                    export_count > 0,
                    "native module has no durable exports for {logical}"
                );
                result.functions = functions.into();
            },
            ModuleKind::Http => {
                selected_definition(&capsule, &catalog)?;
                let export = capsule
                    .export
                    .as_deref()
                    .context("native HTTP export missing")?;
                let router = catalog
                    .http_routers
                    .iter()
                    .find(|router| router.export == export)
                    .context("native HTTP router missing")?;
                result.http_routes = Some(AnalyzedHttpRoutes::new(
                    router
                        .routes
                        .iter()
                        .map(|route| {
                            Ok(AnalyzedHttpRoute {
                                route: HttpActionRoute {
                                    method: route.method.parse()?,
                                    path: route.path.clone(),
                                    matched: true,
                                },
                                pos: None,
                            })
                        })
                        .collect::<anyhow::Result<Vec<_>>>()?,
                ));
            },
            ModuleKind::Crons => {
                let definition = selected_definition(&capsule, &catalog)?;
                let crons = definition
                    .as_object()
                    .context("native cron export must be an object")?
                    .iter()
                    .map(|(name, spec)| {
                        Ok((
                            name.parse()?,
                            model::cron_jobs::types::CronSpec::from_exported_json(spec.clone())?,
                        ))
                    })
                    .collect::<anyhow::Result<BTreeMap<_, _>>>()?;
                result.cron_specs = Some(crons.into());
            },
            ModuleKind::Schema | ModuleKind::Auth => {
                selected_definition(&capsule, &catalog)?;
            },
            ModuleKind::Component => {
                anyhow::bail!("native component definitions are not yet admitted")
            },
        }
        Ok(result)
    }
}
