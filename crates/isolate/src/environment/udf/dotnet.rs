//! Native execution uses the same DatabaseUdfSyscallProvider and transaction
//! as V8. It neither accepts a worker-authored read set nor commits in the
//! worker.
use std::time::Duration;

use anyhow::Context;
use async_trait::async_trait;
use common::{
    errors::JsError,
    knobs::{
        DATABASE_UDF_SYSTEM_TIMEOUT,
        DATABASE_UDF_USER_TIMEOUT,
    },
    log_lines::LogLevel,
    runtime::Runtime,
    types::{
        EnvVarName,
        ModuleEnvironment,
        UdfType,
    },
};
use database::Transaction;
use dotnet_executor::{
    manifest::NativeFunction,
    protocol::{
        FunctionContract,
        FunctionKind,
        WorkerError,
    },
    DotNetExecutor,
    ExecutionBudget,
    SyscallHandler,
};
use errors::{
    ErrorMetadata,
    ErrorMetadataAnyhowExt,
};
use futures::FutureExt;
use model::{
    modules::{
        module_versions::Visibility,
        types::ModuleMetadata,
        ModuleModel,
    },
    source_packages::SourcePackageModel,
};
use rand::Rng;
use serde_json::{
    json,
    Value,
};
use udf::{
    FunctionOutcome,
    NestedUdfOutcome,
};
use value::{
    serialized_args_ext::SerializedArgsExt,
    PendingValue,
};

use super::{
    async_syscall::AsyncSyscallBatch,
    DatabaseUdfArgs,
    DatabaseUdfEnvironment,
    DatabaseUdfInnerProvider,
    DatabaseUdfSyscallProvider,
};
use crate::{
    client::{
        EnvironmentData,
        UdfCallback,
        UdfRequest,
    },
    environment::{
        OpProvider,
        SyscallProvider,
    },
    timeout::FunctionExecutionTime,
    IsolateClient,
};

/// Resolve native runtime from the same transaction's persisted metadata and
/// source package. An admitted capsule never trusts client filesystem paths.
pub async fn resolve_native_target<RT: Runtime>(
    executor: Option<&DotNetExecutor>,
    transaction: &mut Transaction<RT>,
    environment: &EnvironmentData<RT>,
    path: &common::components::ResolvedComponentFunctionPath,
    kind: FunctionKind,
) -> anyhow::Result<Option<NativeFunction>> {
    // System functions are embedded backend modules, not publisher-owned
    // entries in ModulesStorage. Preserve their original executor regardless
    // of whether this deployment admits a native worker.
    if path.udf_path.is_system() {
        return Ok(None);
    }
    let metadata = ModuleModel::new(transaction)
        .get_metadata_for_function_by_id(path)
        .await?
        .context("validated module metadata missing")?;
    if metadata.environment != ModuleEnvironment::DotNet {
        return Ok(executor.and_then(|e| {
            e.find(
                &environment.deployment.name,
                &path.component_path.to_string(),
                &path.udf_path.to_string(),
            )
        }));
    }
    let executor = executor.context("native runtime is not configured for this deployment")?;
    let package = SourcePackageModel::new(transaction, path.component.into())
        .get(metadata.source_package_id)
        .await?;
    let source = environment
        .module_loader
        .get_module_with_metadata(&metadata, &package)
        .await?;
    let capsule = dotnet_executor::capsule::Capsule::parse(&source.source().to_utf8())?;
    capsule.check_module_path(path.udf_path.module().as_str())?;
    anyhow::ensure!(
        capsule.module_kind == dotnet_executor::capsule::ModuleKind::Functions,
        "native function requires a function capsule"
    );
    let artifacts = executor.load_capsule(&capsule).await?;
    let module = path.udf_path.module().as_str();
    let function_path = format!(
        "{}:{}",
        module.strip_suffix(".js").unwrap_or(module),
        path.udf_path.function_name()
    );
    let entry_point = capsule.entry_point_for(&function_path).to_string();
    Ok(Some(NativeFunction {
        deployment: environment.deployment.name.clone(),
        component_path: path.component_path.to_string(),
        entry_point: (entry_point != function_path).then_some(entry_point),
        function_path,
        kind,
        assembly_path: artifacts.assembly_path,
        assembly_sha256: artifacts.assembly_sha256,
        assembly_dependencies: artifacts.assembly_dependencies,
        module_sha256: metadata.sha256.as_base64(),
        http_route: None,
    }))
}

pub(crate) fn deployed_contract(
    metadata: &ModuleMetadata,
    path: &sync_types::CanonicalizedUdfPath,
) -> anyhow::Result<FunctionContract> {
    let function = metadata
        .find_analyzed_function(path)?
        .context("native function analysis missing")?;
    let visibility = match function
        .visibility
        .context("native function visibility missing")?
    {
        Visibility::Public => "public",
        Visibility::Internal => "internal",
    }
    .to_owned();
    let arguments = match function.args_str {
        Some(json) => serde_json::from_str(&json)?,
        None => json!({"type":"any"}),
    };
    let returns = match function.returns_str {
        Some(json) => serde_json::from_str(&json)?,
        None => Value::Null,
    };
    Ok(FunctionContract {
        visibility,
        arguments,
        returns,
    })
}

fn function_kind(kind: UdfType) -> anyhow::Result<FunctionKind> {
    match kind {
        UdfType::Query => Ok(FunctionKind::Query),
        UdfType::Mutation => Ok(FunctionKind::Mutation),
        UdfType::Action | UdfType::HttpAction => {
            anyhow::bail!("transaction executor requires query or mutation")
        },
    }
}

pub(crate) fn native_syscall_error(error: &anyhow::Error) -> WorkerError {
    if let Some(js_error) = error.downcast_ref::<JsError>() {
        if let Some(data) = &js_error.custom_data {
            return WorkerError {
                code: "ConvexError".into(),
                message: js_error.message.clone(),
                data: Some(data.clone().into()),
            };
        }
    }
    WorkerError {
        code: error.short_msg().into(),
        message: error.user_facing_message(),
        data: None,
    }
}

pub(crate) fn native_environment_name(args: &Value) -> anyhow::Result<EnvVarName> {
    let name = args.get("name").and_then(Value::as_str).ok_or_else(|| {
        ErrorMetadata::bad_request(
            "InvalidEnvironmentVariable",
            "environmentVariable requires a string name",
        )
    })?;
    // Preserve the owning Convex name syntax/length refusal, rather than
    // admitting platform-specific host environment names.
    name.parse()
}

#[derive(Clone)]
struct NativeCallback<RT: Runtime> {
    rt: RT,
    executor: DotNetExecutor,
    fallback: IsolateClient<RT>,
}

impl<RT: Runtime> UdfCallback<RT> for NativeCallback<RT> {
    async fn execute_nested_udf(
        self,
        client_id: String,
        mut request: UdfRequest<RT>,
        rng_seed: [u8; 32],
        depth: usize,
    ) -> anyhow::Result<(Transaction<RT>, NestedUdfOutcome)> {
        let target = resolve_native_target(
            Some(&self.executor),
            &mut request.transaction,
            &request.environment_data,
            request.path_and_args.path(),
            function_kind(request.udf_type)?,
        )
        .await?;
        if let Some(target) = target {
            return run_native_nested(
                self.rt.clone(),
                request,
                client_id,
                rng_seed,
                depth,
                target,
                self.executor.clone(),
                self.fallback,
            )
            .boxed()
            .await;
        }
        self.fallback
            .execute_nested_udf(client_id, request, rng_seed, depth)
            .await
    }
}

pub async fn run_native_nested<RT: Runtime>(
    rt: RT,
    request: UdfRequest<RT>,
    client_id: String,
    rng_seed: [u8; 32],
    depth: usize,
    target: NativeFunction,
    executor: DotNetExecutor,
    fallback: IsolateClient<RT>,
) -> anyhow::Result<(Transaction<RT>, NestedUdfOutcome)> {
    let (provider, args) = run_native_inner(
        rt.clone(),
        request,
        depth,
        client_id,
        rng_seed,
        target,
        NativeCallback {
            rt,
            executor,
            fallback,
        },
    )
    .boxed()
    .await?;
    let outcome = NestedUdfOutcome {
        observed_identity: provider.phase.observed_identity(),
        observed_rng: provider.phase.observed_rng(),
        observed_time: provider.phase.observed_time(),
        audit_log_lines: provider.audit_log_lines,
        log_lines: provider.log_lines,
        journal: provider.next_journal,
        result: args.1,
        syscall_trace: provider.syscall_trace,
    };
    Ok((provider.phase.into_transaction()?, outcome))
}

struct NativeSyscalls<RT: Runtime> {
    provider: DatabaseUdfSyscallProvider<RT>,
    callback: NativeCallback<RT>,
}

#[async_trait]
impl<RT: Runtime> SyscallHandler for NativeSyscalls<RT> {
    async fn syscall(
        &mut self,
        name: &str,
        args: Value,
        is_async: bool,
    ) -> anyhow::Result<Result<Value, WorkerError>> {
        let result: anyhow::Result<Value> = if is_async {
            let batch = AsyncSyscallBatch::new(name.to_owned(), args);
            let mut results = self
                .provider
                .run_async_syscall_batch(batch, self.callback.clone())
                .await;
            match results.pop().context("missing native syscall result")? {
                Ok(json) => {
                    serde_json::from_str(&json).context("invalid Convex syscall result JSON")
                },
                Err(error) => Err(error),
            }
        } else {
            match name {
                "dotnet/now" => self
                    .provider
                    .unix_timestamp()
                    .and_then(|ts| ts.as_ms_since_epoch())
                    .map(|ms| json!(ms)),
                "dotnet/random" => self.provider.rng().map(|rng| json!(rng.random::<f64>())),
                "dotnet/environmentVariable" => native_environment_name(&args)
                    .and_then(|name| self.provider.get_environment_variable(name))
                    .map(|value| json!(value.map(|value| value.to_string()))),
                "dotnet/log" => {
                    let messages: Vec<String> = serde_json::from_value(
                        args.get("messages")
                            .cloned()
                            .context("missing log messages")?,
                    )?;
                    anyhow::ensure!(
                        messages.iter().map(String::len).sum::<usize>() <= 4096,
                        "native log message too large"
                    );
                    let level = match args.get("level").and_then(Value::as_str) {
                        Some("debug") => LogLevel::Debug,
                        Some("info") => LogLevel::Info,
                        Some("warn") => LogLevel::Warn,
                        Some("error") => LogLevel::Error,
                        _ => anyhow::bail!("invalid native log level"),
                    };
                    self.provider.trace(level, messages).map(|_| Value::Null)
                },
                _ => self.provider.syscall(name, args),
            }
        };
        match result {
            Ok(value) => Ok(Ok(value)),
            Err(error) if error.is_deterministic_user_error() => {
                Ok(Err(native_syscall_error(&error)))
            },
            Err(error) => Err(error),
        }
    }
}

async fn run_native_inner<RT: Runtime>(
    rt: RT,
    request: UdfRequest<RT>,
    depth: usize,
    client_id: String,
    rng_seed: [u8; 32],
    target: NativeFunction,
    callback: NativeCallback<RT>,
) -> anyhow::Result<(
    DatabaseUdfSyscallProvider<RT>,
    (DatabaseUdfArgs, Result<PendingValue, JsError>, Duration),
)> {
    anyhow::ensure!(
        target.kind == function_kind(request.udf_type)?,
        "native function kind mismatch"
    );
    let nested = depth > 0 || !request.context.is_root();
    let (provider, args) = {
        let (environment, args) =
            DatabaseUdfEnvironment::new(rt, request, depth, client_id, rng_seed);
        (environment.syscall_provider, args)
    };
    let mut host = NativeSyscalls { provider, callback };
    host.provider.phase.initialize_without_isolate().await?;
    let path = host.provider.path.clone();
    let metadata = ModuleModel::new(host.provider.phase.tx_mut()?)
        .get_metadata_for_function_by_id(&path)
        .await?
        .context("native function deployed metadata missing")?;
    anyhow::ensure!(
        metadata.sha256.as_base64() == target.module_sha256,
        "native assembly binding is stale for deployed module"
    );
    let contract = deployed_contract(&metadata, &path.udf_path)?;
    host.provider
        .begin_execution(args.rng_seed, args.unix_timestamp)?;
    let wire_args = args.udf_args.clone().into_args()?;
    anyhow::ensure!(
        wire_args.len() == 1,
        "native function requires one Convex args object"
    );
    let executor = host.callback.executor.clone();
    // Clock reads belong to dotnet/now. An invocation that never asks for
    // the clock must not acquire a time dependency merely by entering .NET.
    let response = executor
        .invoke_with_budget(
            &target,
            wire_args.into_iter().next().context("missing args")?,
            contract,
            ExecutionBudget {
                user: *DATABASE_UDF_USER_TIMEOUT,
                system: *DATABASE_UDF_SYSTEM_TIMEOUT,
            },
            nested,
            &mut host,
        )
        .await?;
    let user_execution_time = response.user_execution_time;
    let result = match response.result {
        Ok(value) => Ok(PendingValue::from_uncommitted_json(value)?),
        Err(error) => Err(match error.data {
            Some(data) => JsError::convex_error(error.message, data.try_into()?),
            None => JsError::from_message(error.message),
        }),
    };
    let result = match result {
        Ok(value)
            if host.provider.udf_type == UdfType::Query && depth == 0 && value.is_pending() =>
        {
            Err(JsError::from_message(format!(
                "Function {} return value invalid: queries cannot return an unresolved commit \
                 timestamp",
                path.for_logging().debug_str()
            )))
        },
        result => result,
    };
    Ok((host.provider, (args, result, user_execution_time)))
}

pub async fn run_native_udf<RT: Runtime>(
    rt: RT,
    request: UdfRequest<RT>,
    client_id: String,
    rng_seed: [u8; 32],
    target: NativeFunction,
    executor: DotNetExecutor,
    fallback: IsolateClient<RT>,
    function_started: Option<tokio::sync::oneshot::Sender<()>>,
) -> anyhow::Result<(Transaction<RT>, FunctionOutcome)> {
    let memory_limit_mib = executor.memory_limit_mib();
    if let Some(sender) = function_started {
        let _ = sender.send(());
    }
    let (provider, (args, result, user_execution_time)) = run_native_inner(
        rt.clone(),
        request,
        0,
        client_id,
        rng_seed,
        target,
        NativeCallback {
            rt,
            executor,
            fallback,
        },
    )
    .await?;
    let (tx, mut outcome) = provider.into_outcome(
        args,
        result,
        FunctionExecutionTime {
            elapsed: user_execution_time,
            limit: *DATABASE_UDF_USER_TIMEOUT,
        },
    )?;
    match &mut outcome {
        FunctionOutcome::Query(outcome) | FunctionOutcome::Mutation(outcome) => {
            outcome.memory_in_mb = memory_limit_mib.try_into()?;
            outcome.native_execution = true;
        },
        _ => anyhow::bail!("unexpected native transaction outcome"),
    }
    Ok((tx, outcome))
}
