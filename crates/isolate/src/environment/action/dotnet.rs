//! Actions use upstream TaskExecutor callbacks, which create separate query or
//! mutation transactions. They never obtain the transactional DB syscall class.
use std::{
    collections::BTreeMap,
    sync::{
        Arc,
        OnceLock,
    },
};

use anyhow::Context;
use async_trait::async_trait;
use common::{
    components::ResolvedComponentFunctionPath,
    errors::JsError,
    execution_context::ExecutionContext,
    http::fetch::FetchClient,
    knobs::{
        V8_ACTION_SYSTEM_TIMEOUT,
        V8_ACTION_USER_TIMEOUT,
    },
    log_lines::{
        LogLevel,
        LogLine,
    },
    runtime::Runtime,
    sync::spsc,
    types::HttpActionRoute,
};
use database::Transaction;
use dotnet_executor::{
    manifest::NativeFunction,
    protocol::{
        FunctionKind,
        HttpHeader,
        HttpRequestHead,
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
use futures::{
    stream::BoxStream,
    StreamExt,
};
use keybroker::Identity;
use model::modules::ModuleModel;
use parking_lot::Mutex;
use rand::Rng;
use serde::Deserialize;
use serde_json::{
    json,
    Value,
};
use tokio::sync::mpsc;
use udf::{
    validation::{
        ValidatedHttpPath,
        ValidatedPathAndArgs,
    },
    ActionCallbacks,
    ActionOutcome,
    HttpActionOutcome,
    HttpActionRequest,
    HttpActionResponseHead,
    HttpActionResponsePart,
    HttpActionResponseStreamer,
    HttpActionResult,
    SyscallTrace,
    HTTP_ACTION_BODY_LIMIT,
};
use value::{
    serialized_args_ext::SerializedArgsExt,
    ConvexValue,
    JsonPackedValue,
};

use super::{
    dotnet_streams::{
        validate_http_head,
        NativeHttpResponseStream,
        NativeStorageStreams,
    },
    phase::ActionPhase,
    service_token::ServiceTokenCache,
    task_executor::TaskExecutor,
};
use crate::client::EnvironmentData;

struct NativeActionSyscalls<RT: Runtime> {
    phase: ActionPhase<RT>,
    tasks: TaskExecutor<RT>,
    logs: mpsc::UnboundedSender<LogLine>,
    log_count: usize,
    http_body: Option<NativeHttpBody>,
    http_response: Option<NativeHttpResponseStream>,
    storage_streams: NativeStorageStreams,
}

struct NativeHttpBody {
    stream: Option<BoxStream<'static, anyhow::Result<bytes::Bytes>>>,
    pending: bytes::Bytes,
    total_bytes: usize,
}

impl NativeHttpBody {
    async fn read(&mut self) -> anyhow::Result<Value> {
        while self.pending.is_empty() {
            let chunk = match &mut self.stream {
                Some(stream) => stream.next().await.transpose()?,
                None => None,
            };
            let Some(chunk) = chunk else {
                self.stream = None;
                return Ok(json!({"bytes": {"$bytes":""}, "done":true}));
            };
            self.total_bytes = self.total_bytes.saturating_add(chunk.len());
            anyhow::ensure!(
                self.total_bytes <= HTTP_ACTION_BODY_LIMIT,
                ErrorMetadata::bad_request(
                    "HttpRequestBodyTooLarge",
                    "HTTP request body exceeds owning 20MiB limit"
                )
            );
            self.pending = chunk;
        }
        let chunk = self.pending.split_to(self.pending.len().min(64 * 1024));
        let bytes: Value = ConvexValue::Bytes(chunk.to_vec().try_into()?).into();
        Ok(json!({"bytes":bytes,"done":false}))
    }
}

#[async_trait]
impl<RT: Runtime> SyscallHandler for NativeActionSyscalls<RT> {
    async fn syscall(
        &mut self,
        name: &str,
        args: Value,
        is_async: bool,
    ) -> anyhow::Result<Result<Value, WorkerError>> {
        let result = if is_async {
            match name {
                "dotnet/httpReadBody" => match &mut self.http_body {
                    Some(body) if args.as_object().is_some_and(|object| object.is_empty()) => {
                        body.read().await
                    },
                    _ => Err(ErrorMetadata::bad_request(
                        "InvalidHttpBodyOperation",
                        "httpReadBody requires an HTTP invocation and empty args",
                    )
                    .into()),
                },
                "dotnet/storageStore" => self.storage_store(args).await,
                "dotnet/storageGet" => self.storage_get(args).await,
                "dotnet/storageOpenRead"
                | "dotnet/storageRead"
                | "dotnet/storageClose"
                | "dotnet/storageOpenWrite"
                | "dotnet/storageWrite"
                | "dotnet/storageCommit"
                | "dotnet/storageAbort" => {
                    self.storage_streams.syscall(&self.tasks, name, args).await
                },
                "dotnet/httpResponseHead" | "dotnet/httpResponseChunk" => {
                    match &mut self.http_response {
                        Some(response) => response.syscall(name, args).await,
                        None => Err(ErrorMetadata::bad_request(
                            "InvalidNativeStreamOperation",
                            "HTTP response operations require an HTTP invocation",
                        )
                        .into()),
                    }
                },
                _ => self
                    .tasks
                    .run_async_syscall(name.to_owned(), args)
                    .await
                    .and_then(|result| {
                        serde_json::from_str(&result).context("invalid action syscall JSON")
                    }),
            }
        } else {
            match name {
                "dotnet/now" => self
                    .phase
                    .unix_timestamp()
                    .and_then(|t| t.as_ms_since_epoch())
                    .map(|ms| json!(ms)),
                "dotnet/random" => self.phase.rng().map(|rng| json!(rng.random::<f64>())),
                "dotnet/environmentVariable" => {
                    crate::environment::udf::dotnet::native_environment_name(&args)
                        .and_then(|name| self.phase.get_environment_variable(name))
                        .map(|value| json!(value.map(|value| value.to_string())))
                },
                "1.0/componentArgument" => {
                    let name = args
                        .get("name")
                        .and_then(Value::as_str)
                        .context("missing component argument name")?;
                    let identifier = name.parse().context("invalid component argument name")?;
                    Ok(match self.phase.component_arguments()?.get(&identifier) {
                        Some(value) => json!({"value":value}),
                        None => json!({}),
                    })
                },
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
                    if self.log_count < 256 {
                        let _ = self.logs.send(LogLine::new_developer_log_line(
                            level,
                            messages,
                            self.tasks.rt.unix_timestamp(),
                        ));
                        self.log_count += 1;
                    }
                    Ok(Value::Null)
                },
                _ => Err(ErrorMetadata::bad_request(
                    "UnknownOperation",
                    format!("Unknown action syscall {name}"),
                )
                .into()),
            }
        };
        match result {
            Ok(value) => Ok(Ok(value)),
            Err(error) if error.is_deterministic_user_error() => Ok(Err(
                crate::environment::udf::dotnet::native_syscall_error(&error),
            )),
            Err(error) => Err(error),
        }
    }
}

impl<RT: Runtime> NativeActionSyscalls<RT> {
    async fn storage_store(&mut self, args: Value) -> anyhow::Result<Value> {
        let bytes = ConvexValue::try_from(
            args.get("bytes")
                .cloned()
                .context("missing storage bytes")?,
        )?;
        let ConvexValue::Bytes(bytes) = bytes else {
            return Err(ErrorMetadata::bad_request(
                "InvalidStorageBytes",
                "storageStore requires Convex bytes",
            )
            .into());
        };
        let content_type: Option<String> =
            serde_json::from_value(args.get("contentType").cloned().unwrap_or(Value::Null))?;
        let sha256: Option<String> =
            serde_json::from_value(args.get("sha256").cloned().unwrap_or(Value::Null))?;
        let content_length = Some(bytes.len().to_string());
        let (mut sender, receiver) = spsc::unbounded_channel();
        sender.send(Ok(bytes::Bytes::copy_from_slice(bytes.as_ref())))?;
        sender.close();
        let id = self
            .tasks
            .run_storage_store(
                receiver,
                content_type,
                content_length,
                sha256.map(|digest| format!("sha-256={digest}")),
            )
            .await?;
        Ok(json!(id.to_string()))
    }

    async fn storage_get(&mut self, args: Value) -> anyhow::Result<Value> {
        let id = args
            .get("storageId")
            .and_then(Value::as_str)
            .context("missing storageId")?;
        let Some((mut stream, metadata)) = self
            .tasks
            .run_storage_get_inner(id.to_owned(), uuid::Uuid::nil())
            .await?
        else {
            return Ok(Value::Null);
        };
        // IPC v1 is bounded, so buffered native blobs have an explicit 8MiB
        // ceiling. The upstream file stream still owns auth and usage tracking.
        const LIMIT: usize = 8 * 1024 * 1024;
        if metadata.content_length > LIMIT as u64 {
            return Err(ErrorMetadata::bad_request(
                "NativeBlobTooLarge",
                "native storageGet exceeds 8MiB; streaming IPC is required",
            )
            .into());
        }
        let mut bytes = Vec::new();
        while let Some(chunk) = stream.next().await {
            let chunk = chunk?;
            if bytes.len().saturating_add(chunk.len()) > LIMIT {
                return Err(ErrorMetadata::bad_request(
                    "NativeBlobTooLarge",
                    "native storageGet exceeds 8MiB",
                )
                .into());
            }
            bytes.extend_from_slice(&chunk);
        }
        let bytes: Value = ConvexValue::Bytes(bytes.try_into()?).into();
        Ok(json!({"bytes":bytes,"contentType":metadata.content_type}))
    }
}

async fn prepare_action_syscalls<RT: Runtime>(
    rt: RT,
    path: &ResolvedComponentFunctionPath,
    transaction: Transaction<RT>,
    identity: Identity,
    environment_data: EnvironmentData<RT>,
    action_callbacks: Arc<dyn ActionCallbacks>,
    fetch_client: Arc<dyn FetchClient>,
    log_line_sender: mpsc::UnboundedSender<LogLine>,
    context: ExecutionContext,
    http_route: Option<HttpActionRoute>,
) -> anyhow::Result<NativeActionSyscalls<RT>> {
    let trace = Arc::new(Mutex::new(SyscallTrace::new()));
    let resources = Arc::new(Mutex::new(BTreeMap::new()));
    let origin = Arc::new(Mutex::new(None));
    let (retval_sender, _retval_receiver) = mpsc::unbounded_channel();
    let route = Arc::new(OnceLock::new());
    if let Some(http_route) = http_route {
        let _ = route.set(http_route);
    }
    let tasks = TaskExecutor {
        rt: rt.clone(),
        identity: identity.clone(),
        file_storage: environment_data.file_storage,
        syscall_trace: trace.clone(),
        action_callbacks,
        fetch_client,
        _module_loader: environment_data.module_loader.clone(),
        key_broker: environment_data.key_broker,
        task_order: Default::default(),
        task_retval_sender: retval_sender,
        usage_tracker: transaction.usage_tracker.clone(),
        context,
        resources: resources.clone(),
        component_id: path.component,
        udf_path: path.udf_path.clone(),
        component_path: path.component_path.clone(),
        convex_origin_override: origin.clone(),
        http_action_route: route,
        deployment: environment_data.deployment,
        service_token: Arc::new(ServiceTokenCache::default()),
    };
    let mut phase = ActionPhase::new(
        rt.clone(),
        path.component,
        transaction,
        environment_data.module_loader,
        environment_data.default_system_env_vars,
        resources,
        origin,
    );
    phase.initialize_without_isolate().await?;
    phase.begin_execution()?;
    Ok(NativeActionSyscalls {
        phase,
        tasks,
        logs: log_line_sender,
        log_count: 0,
        http_body: None,
        http_response: None,
        storage_streams: NativeStorageStreams::default(),
    })
}

pub async fn run_native_action<RT: Runtime>(
    rt: RT,
    path_and_args: ValidatedPathAndArgs,
    mut transaction: Transaction<RT>,
    identity: Identity,
    environment_data: EnvironmentData<RT>,
    action_callbacks: Arc<dyn ActionCallbacks>,
    fetch_client: Arc<dyn FetchClient>,
    log_line_sender: mpsc::UnboundedSender<LogLine>,
    context: ExecutionContext,
    target: NativeFunction,
    executor: DotNetExecutor,
    function_started: Option<tokio::sync::oneshot::Sender<()>>,
) -> anyhow::Result<ActionOutcome> {
    anyhow::ensure!(
        target.kind == FunctionKind::Action,
        "native action kind mismatch"
    );
    let unix_timestamp = rt.unix_timestamp();
    let path = path_and_args.path().clone();
    let nested = !context.is_root();
    let metadata = ModuleModel::new(&mut transaction)
        .get_metadata_for_function_by_id(&path)
        .await?
        .context("native action deployed metadata missing")?;
    anyhow::ensure!(
        metadata.sha256.as_base64() == target.module_sha256,
        "native action assembly binding is stale"
    );
    let contract = crate::environment::udf::dotnet::deployed_contract(&metadata, &path.udf_path)?;
    let mut host = prepare_action_syscalls(
        rt,
        &path,
        transaction,
        identity.clone(),
        environment_data,
        action_callbacks,
        fetch_client,
        log_line_sender,
        context,
        None,
    )
    .await?;
    let trace = host.tasks.syscall_trace.clone();
    let (_, arguments, version) = path_and_args.consume();
    let args = arguments.clone().into_args()?;
    anyhow::ensure!(
        args.len() == 1,
        "native action requires one Convex argument object"
    );
    if let Some(sender) = function_started {
        let _ = sender.send(());
    }
    let invocation = executor
        .invoke_with_budget(
            &target,
            args.into_iter().next().context("missing args")?,
            contract,
            ExecutionBudget {
                user: *V8_ACTION_USER_TIMEOUT,
                system: *V8_ACTION_SYSTEM_TIMEOUT,
            },
            nested,
            &mut host,
        )
        .await?;
    let result = match invocation.result {
        Ok(value) => Ok(JsonPackedValue::pack(ConvexValue::try_from(value)?)),
        Err(error) => Err(match error.data {
            Some(data) => JsError::convex_error(error.message, data.try_into()?),
            None => JsError::from_message(error.message),
        }),
    };
    Ok(ActionOutcome {
        path: path.for_logging(),
        arguments,
        identity: identity.into(),
        unix_timestamp,
        result,
        syscall_trace: trace.lock().clone(),
        udf_server_version: version,
        user_execution_time: Some(invocation.user_execution_time),
        native_memory_in_mb: Some(executor.memory_limit_mib()),
        native_execution: true,
    })
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct NativeHttpResponse {
    status: u16,
    headers: Vec<HttpHeader>,
    body: Value,
}

fn validate_http_response(value: Value) -> anyhow::Result<(HttpActionResponseHead, bytes::Bytes)> {
    let response: NativeHttpResponse = serde_json::from_value(value)?;
    let head = validate_http_head(response.status, response.headers)?;
    let ConvexValue::Bytes(body) = ConvexValue::try_from(response.body)? else {
        anyhow::bail!("native HTTP response body must be canonical bytes");
    };
    anyhow::ensure!(
        body.len() <= 8 * 1024 * 1024,
        "native buffered HTTP response exceeds 8MiB"
    );
    anyhow::ensure!(
        !matches!(response.status, 204 | 205 | 304) || body.is_empty(),
        "native HTTP response status cannot carry a body"
    );
    Ok((head, bytes::Bytes::from(Vec::<u8>::from(body))))
}

pub async fn run_native_http_action<RT: Runtime>(
    rt: RT,
    http_module_path: ValidatedHttpPath,
    route: HttpActionRoute,
    request: HttpActionRequest,
    mut transaction: Transaction<RT>,
    identity: Identity,
    environment_data: EnvironmentData<RT>,
    action_callbacks: Arc<dyn ActionCallbacks>,
    fetch_client: Arc<dyn FetchClient>,
    log_line_sender: mpsc::UnboundedSender<LogLine>,
    response_streamer: HttpActionResponseStreamer,
    context: ExecutionContext,
    target: NativeFunction,
    executor: DotNetExecutor,
    function_started: Option<tokio::sync::oneshot::Sender<()>>,
) -> anyhow::Result<HttpActionOutcome> {
    anyhow::ensure!(
        target.kind == FunctionKind::HttpAction,
        "native HTTP kind mismatch"
    );
    let binding = target
        .http_route
        .as_ref()
        .context("native HTTP route binding missing")?;
    let router_path = http_module_path.path().clone();
    anyhow::ensure!(
        binding.module_path == router_path.udf_path.module().as_str()
            && binding.method == route.method.to_string()
            && binding.path == route.path,
        "native HTTP route binding mismatch"
    );
    let router_metadata = ModuleModel::new(&mut transaction)
        .get_metadata_for_function_by_id(&router_path)
        .await?
        .context("native HTTP router metadata missing")?;
    anyhow::ensure!(
        router_metadata.sha256.as_base64() == binding.module_sha256,
        "native HTTP router binding is stale"
    );
    let routes = router_metadata
        .analyze_result
        .as_ref()
        .and_then(|module| module.http_routes.as_ref())
        .context("native HTTP analyzed routes missing")?;
    anyhow::ensure!(
        routes
            .iter()
            .any(|analyzed| analyzed.route.path == route.path
                && analyzed.route.method == route.method
                && analyzed.route.matched),
        "native HTTP route was not admitted by owning analysis"
    );
    let path = ResolvedComponentFunctionPath {
        component: router_path.component,
        component_path: router_path.component_path.clone(),
        udf_path: target
            .function_path
            .parse::<sync_types::UdfPath>()?
            .canonicalize(),
    };
    let handler_metadata = ModuleModel::new(&mut transaction)
        .get_metadata_for_function_by_id(&path)
        .await?
        .context("native HTTP handler metadata missing")?;
    anyhow::ensure!(
        handler_metadata.sha256.as_base64() == target.module_sha256,
        "native HTTP handler assembly binding is stale"
    );
    let unix_timestamp = rt.unix_timestamp();
    let request_head = request.head;
    let wire_head = HttpRequestHead {
        method: request_head.method.to_string(),
        url: request_head.url.to_string(),
        headers: request_head
            .headers
            .iter()
            .map(|(name, value)| HttpHeader {
                name: name.as_str().into(),
                value: crate::http::header_to_byte_string(value),
            })
            .collect(),
    };
    let mut host = prepare_action_syscalls(
        rt,
        &path,
        transaction,
        identity.clone(),
        environment_data,
        action_callbacks,
        fetch_client,
        log_line_sender,
        context,
        Some(route.clone()),
    )
    .await?;
    host.http_body = Some(NativeHttpBody {
        stream: request.body,
        pending: bytes::Bytes::new(),
        total_bytes: 0,
    });
    host.http_response = Some(NativeHttpResponseStream::new(
        response_streamer,
        request_head.method == http::Method::HEAD,
    ));
    if let Some(sender) = function_started {
        let _ = sender.send(());
    }
    let invocation = executor
        .invoke_http(
            &target,
            wire_head,
            ExecutionBudget {
                user: *V8_ACTION_USER_TIMEOUT,
                system: *V8_ACTION_SYSTEM_TIMEOUT,
            },
            &mut host,
        )
        .await?;
    let result = match invocation.result {
        Ok(value) => {
            let response = host
                .http_response
                .as_mut()
                .context("native HTTP response owner missing")?;
            if value == json!({"streamed":true}) {
                anyhow::ensure!(
                    response.streamer.has_started(),
                    "native streamed HTTP result requires a sent head"
                );
            } else {
                anyhow::ensure!(
                    !response.streamer.has_started(),
                    "native HTTP result cannot mix buffered and streamed output"
                );
                // Buffered compatibility responses validate completely before
                // any head. Streamed responses validate each bounded part.
                let (head, body) = validate_http_response(value)?;
                response
                    .streamer
                    .send_part(HttpActionResponsePart::Head(head))??;
                if request_head.method != http::Method::HEAD && !body.is_empty() {
                    response
                        .streamer
                        .send_part(HttpActionResponsePart::BodyChunk(body))??;
                }
            }
            HttpActionResult::Streamed
        },
        Err(error) => HttpActionResult::Error(match error.data {
            Some(data) => JsError::convex_error(error.message, data.try_into()?),
            None => JsError::from_message(error.message),
        }),
    };
    let trace = host.tasks.syscall_trace.lock().clone();
    Ok(HttpActionOutcome::new(
        Some(route),
        request_head,
        identity.into(),
        unix_timestamp,
        result,
        Some(trace),
        http_module_path.npm_version().clone(),
        invocation.user_execution_time,
    )
    .with_memory_in_mb(executor.memory_limit_mib())
    .with_native_execution(true))
}
