//! Invocation-scoped local IPC. This crate has no database, identity,
//! credential, or HTTP client: the owning Convex host serves each syscall in
//! its transaction.
pub mod capsule;
pub mod manifest;
pub mod protocol;
mod worker;

#[cfg(not(feature = "transport-proof"))]
metrics::register_convex_counter!(
    NATIVE_IDLE_WORKER_RESOURCE_RETIRE_TOTAL,
    "Native idle workers retired before dispatch for resident memory headroom or exit"
);

use std::{
    collections::BTreeMap,
    sync::{
        atomic::{
            AtomicU64,
            Ordering,
        },
        Arc,
    },
    time::Duration,
};

use anyhow::Context;
use async_trait::async_trait;
use manifest::{
    Manifest,
    NativeFunction,
};
use protocol::{
    FunctionContract,
    HttpRequestHead,
    Invoke,
    WorkerError,
    PROTOCOL_VERSION,
};
use serde_json::Value;
use tokio::sync::{
    Mutex,
    Semaphore,
};
use worker::{
    Worker,
    WorkerKey,
};

#[async_trait]
pub trait SyscallHandler: Send {
    /// Outer errors abort the invocation (OCC/system failure); inner errors can
    /// be caught by the application. Never turn an OCC into an application
    /// error.
    async fn syscall(
        &mut self,
        name: &str,
        args: Value,
        is_async: bool,
    ) -> anyhow::Result<Result<Value, WorkerError>>;
}

struct Inner {
    manifest: Manifest,
    idle: Mutex<BTreeMap<WorkerKey, Vec<Worker>>>,
    capacity: Arc<Semaphore>,
    next_invocation: AtomicU64,
    artifacts: capsule::ArtifactCache,
}

// A deployment/capsule key lives only while it owns an idle process. Keeping
// empty buckets would grow this map across every code revision even though the
// worker semaphore bounds the actual process count.
fn take_idle_worker(
    idle: &mut BTreeMap<WorkerKey, Vec<Worker>>,
    key: &WorkerKey,
) -> Option<Worker> {
    let workers = idle.get_mut(key)?;
    let worker = workers.pop();
    if workers.is_empty() {
        idle.remove(key);
    }
    worker
}

#[derive(Clone)]
pub struct DotNetExecutor(Arc<Inner>);

#[derive(Clone, Copy)]
pub struct ExecutionBudget {
    pub user: Duration,
    pub system: Duration,
}

pub struct InvocationResult {
    pub result: Result<Value, WorkerError>,
    pub user_execution_time: Duration,
}

/// A structured definition failure remains distinct from transport, resource,
/// cancellation and protocol failures owned by the executor itself.
#[derive(Debug)]
pub struct DefinitionError(pub WorkerError);
impl std::fmt::Display for DefinitionError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "native definition failed: {}: {}",
            self.0.code, self.0.message
        )
    }
}
impl std::error::Error for DefinitionError {}

/// Provenance supplied by the owning top-level dispatch. Worker messages and
/// nested calls cannot choose the execution runtime of their caller's log.
#[derive(Debug)]
struct NativeExecution;

impl std::fmt::Display for NativeExecution {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("native execution failed")
    }
}

impl std::error::Error for NativeExecution {}

pub fn mark_native_execution(error: anyhow::Error) -> anyhow::Error {
    error.context(NativeExecution)
}

pub fn was_native_execution(error: &anyhow::Error) -> bool {
    error.downcast_ref::<NativeExecution>().is_some()
}

impl DotNetExecutor {
    pub fn new(manifest: Manifest) -> anyhow::Result<Self> {
        manifest.validate()?;
        let capacity = Arc::new(Semaphore::new(manifest.max_workers));
        Ok(Self(Arc::new(Inner {
            manifest,
            idle: Mutex::new(BTreeMap::new()),
            capacity,
            next_invocation: AtomicU64::new(1),
            artifacts: capsule::ArtifactCache::new()?,
        })))
    }

    pub fn from_environment() -> anyhow::Result<Option<Self>> {
        let Some(path) = std::env::var_os("CONVEX_DOTNET_MANIFEST") else {
            return Ok(None);
        };
        Self::new(Manifest::load(std::path::Path::new(&path))?).map(Some)
    }

    pub fn find(
        &self,
        deployment: &str,
        component_path: &str,
        function_path: &str,
    ) -> Option<NativeFunction> {
        self.0
            .manifest
            .find(deployment, component_path, function_path)
            .cloned()
    }

    pub fn memory_limit_mib(&self) -> u64 {
        self.0.manifest.worker.memory_mi_b
    }

    pub async fn load_capsule(
        &self,
        capsule: &capsule::Capsule,
    ) -> anyhow::Result<capsule::LoadedArtifacts> {
        self.0.artifacts.load(capsule).await
    }

    pub async fn describe(
        &self,
        artifacts: &capsule::LoadedArtifacts,
        time_ms: f64,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<capsule::Catalog> {
        self.describe_selected(artifacts, time_ms, None, None, handler)
            .await
    }

    pub async fn describe_selected(
        &self,
        artifacts: &capsule::LoadedArtifacts,
        time_ms: f64,
        module_kind: Option<capsule::ModuleKind>,
        export: Option<String>,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<capsule::Catalog> {
        let target = NativeFunction {
            deployment: "native-admission".into(),
            component_path: String::new(),
            function_path: "admission:describe".into(),
            entry_point: None,
            kind: protocol::FunctionKind::Query,
            assembly_path: artifacts.assembly_path.clone(),
            assembly_sha256: artifacts.assembly_sha256.clone(),
            assembly_dependencies: artifacts.assembly_dependencies.clone(),
            module_sha256: "admission".into(),
            http_route: None,
        };
        target.verify_assembly().await?;
        self.0.manifest.worker.verify_artifacts().await?;
        let limit = Duration::from_millis(self.0.manifest.worker.invocation_timeout_ms.min(16_000));
        let permit = tokio::time::timeout(limit, self.0.capacity.clone().acquire_owned())
            .await
            .context("native admission capacity timeout")??;
        // Definition code has no action profile, network, or database capability.
        // Never pool this process: import failure/cancellation drops and kills it.
        let value = tokio::time::timeout(limit, async {
            let mut worker = Worker::spawn(&self.0.manifest.worker, &target).await?;
            let request = protocol::Describe {
                message_type: "describe",
                version: PROTOCOL_VERSION,
                invocation_id: self
                    .0
                    .next_invocation
                    .fetch_add(1, Ordering::Relaxed)
                    .to_string(),
                assembly_path: artifacts
                    .assembly_path
                    .to_str()
                    .context("non-UTF8 native artifact path")?
                    .into(),
                assembly_sha256: artifacts.assembly_sha256.clone(),
                assembly_dependencies: artifacts.assembly_dependencies.clone(),
                time_ms,
                module_kind,
                export,
            };
            let result = worker
                .describe(
                    &request,
                    ExecutionBudget {
                        user: Duration::from_secs(1),
                        system: Duration::from_secs(15),
                    },
                    handler,
                )
                .await?;
            match result.result {
                Ok(value) => Ok::<Value, anyhow::Error>(value),
                Err(error) => Err(DefinitionError(error).into()),
            }
        })
        .await
        .context("native admission wall deadline exceeded")??;
        drop(permit);
        let catalog: capsule::Catalog = serde_json::from_value(value)?;
        catalog.validate()?;
        Ok(catalog)
    }

    /// Pure definition initializer. It never receives a transaction, action
    /// profile or activation capability and is never returned to the worker
    /// pool.
    pub async fn initialize_component(
        &self,
        artifacts: &capsule::LoadedArtifacts,
        export: String,
        definition_path: String,
        component_name: String,
        component_definition: Value,
        args: Value,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<Value> {
        let target = NativeFunction {
            deployment: "native-admission".into(),
            component_path: definition_path.clone(),
            function_path: "admission:initializeComponent".into(),
            entry_point: None,
            kind: protocol::FunctionKind::Query,
            assembly_path: artifacts.assembly_path.clone(),
            assembly_sha256: artifacts.assembly_sha256.clone(),
            assembly_dependencies: artifacts.assembly_dependencies.clone(),
            module_sha256: "admission".into(),
            http_route: None,
        };
        anyhow::ensure!(
            args.is_object() && component_definition.is_object(),
            "native initializer requires objects"
        );
        target.verify_assembly().await?;
        self.0.manifest.worker.verify_artifacts().await?;
        let limit = Duration::from_millis(self.0.manifest.worker.invocation_timeout_ms.min(16_000));
        let permit = tokio::time::timeout(limit, self.0.capacity.clone().acquire_owned())
            .await
            .context("native initializer capacity timeout")??;
        let result = tokio::time::timeout(limit, async {
            let mut worker = Worker::spawn(&self.0.manifest.worker, &target).await?;
            let request = protocol::InitializeComponent {
                message_type: "initializeComponent",
                version: PROTOCOL_VERSION,
                invocation_id: self
                    .0
                    .next_invocation
                    .fetch_add(1, Ordering::Relaxed)
                    .to_string(),
                assembly_path: artifacts
                    .assembly_path
                    .to_str()
                    .context("non-UTF8 native artifact path")?
                    .into(),
                assembly_sha256: artifacts.assembly_sha256.clone(),
                assembly_dependencies: artifacts.assembly_dependencies.clone(),
                export,
                definition_path,
                component_name,
                component_definition,
                args,
            };
            let result = worker
                .initialize_component(
                    &request,
                    ExecutionBudget {
                        user: Duration::from_secs(1),
                        system: Duration::from_secs(15),
                    },
                    handler,
                )
                .await?;
            match result.result {
                Ok(value) => Ok::<Value, anyhow::Error>(value),
                Err(error) => Err(DefinitionError(error).into()),
            }
        })
        .await
        .context("native initializer wall deadline exceeded")??;
        drop(permit);
        Ok(result)
    }

    pub fn has_http_functions(&self, deployment: &str, component_path: &str) -> bool {
        self.0
            .manifest
            .has_http_functions(deployment, component_path)
    }

    pub fn find_http(
        &self,
        deployment: &str,
        component_path: &str,
        method: &str,
        route_path: &str,
    ) -> Option<NativeFunction> {
        self.0
            .manifest
            .find_http(deployment, component_path, method, route_path)
            .cloned()
    }

    pub async fn invoke(
        &self,
        target: &NativeFunction,
        args: Value,
        time_ms: f64,
        function_contract: FunctionContract,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<Result<Value, WorkerError>> {
        let limit = Duration::from_millis(self.0.manifest.worker.invocation_timeout_ms);
        Ok(self
            .invoke_with_budget(
                target,
                args,
                time_ms,
                function_contract,
                ExecutionBudget {
                    user: limit,
                    system: limit,
                },
                false,
                handler,
            )
            .await?
            .result)
    }

    pub async fn invoke_with_budget(
        &self,
        target: &NativeFunction,
        args: Value,
        time_ms: f64,
        function_contract: FunctionContract,
        budget: ExecutionBudget,
        nested: bool,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<InvocationResult> {
        self.invoke_with_request(
            target,
            args,
            time_ms,
            function_contract,
            budget,
            nested,
            None,
            handler,
        )
        .await
    }

    pub async fn invoke_http(
        &self,
        target: &NativeFunction,
        http_request: HttpRequestHead,
        time_ms: f64,
        budget: ExecutionBudget,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<InvocationResult> {
        anyhow::ensure!(
            target.kind == protocol::FunctionKind::HttpAction,
            "native HTTP kind mismatch"
        );
        self.invoke_with_request(
            target,
            serde_json::json!({}),
            time_ms,
            FunctionContract {
                visibility: "public".into(),
                arguments: serde_json::json!({"type":"any"}),
                returns: Value::Null,
            },
            budget,
            false,
            Some(http_request),
            handler,
        )
        .await
    }

    async fn invoke_with_request(
        &self,
        target: &NativeFunction,
        args: Value,
        time_ms: f64,
        function_contract: FunctionContract,
        budget: ExecutionBudget,
        nested: bool,
        http_request: Option<HttpRequestHead>,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<InvocationResult> {
        target.verify_assembly().await?;
        self.0.manifest.worker.verify_artifacts().await?;
        let timeout = Duration::from_millis(self.0.manifest.worker.invocation_timeout_ms);
        let permit = if nested {
            // A paused parent still owns its worker. Never wait on a full pool
            // from a nested call: that would deadlock every saturated caller.
            self.0
                .capacity
                .clone()
                .try_acquire_owned()
                .context("native worker budget exhausted by nested invocation")?
        } else {
            tokio::time::timeout(timeout, self.0.capacity.clone().acquire_owned())
                .await
                .context("native worker capacity timeout")??
        };
        let key = WorkerKey::from(target);
        // A checked-out worker is owned by this future. Cancellation drops and
        // kills it rather than returning a half-finished protocol to the pool.
        let cached = {
            let mut idle = self.0.idle.lock().await;
            let cached = take_idle_worker(&mut idle, &key);
            if cached.is_none() {
                let busy = self.0.manifest.max_workers - self.0.capacity.available_permits();
                while idle.values().map(Vec::len).sum::<usize>() + busy
                    > self.0.manifest.max_workers
                {
                    let retire = idle
                        .iter()
                        .find(|(_, workers)| !workers.is_empty())
                        .map(|(key, _)| key.clone());
                    if let Some(retire) = retire {
                        drop(take_idle_worker(&mut idle, &retire));
                    } else {
                        break;
                    }
                }
            }
            cached
        };
        let reusable = match cached {
            Some(mut worker) => {
                if worker.reusable().await {
                    Some(worker)
                } else {
                    // Dropping kills the old process before any new request is
                    // sent. This never retries an invocation or a syscall.
                    drop(worker);
                    #[cfg(not(feature = "transport-proof"))]
                    metrics::log_counter(&NATIVE_IDLE_WORKER_RESOURCE_RETIRE_TOTAL, 1);
                    None
                }
            },
            None => None,
        };
        let mut worker = match reusable {
            Some(worker) => worker,
            None => Worker::spawn(&self.0.manifest.worker, target).await?,
        };
        let id = self
            .0
            .next_invocation
            .fetch_add(1, Ordering::Relaxed)
            .to_string();
        let request = Invoke {
            message_type: "invoke",
            version: PROTOCOL_VERSION,
            invocation_id: id,
            assembly_path: target
                .assembly_path
                .to_str()
                .context("assembly path is not UTF-8")?
                .into(),
            assembly_sha256: target.assembly_sha256.clone(),
            assembly_dependencies: target.assembly_dependencies.clone(),
            function_path: target.function_path.clone(),
            entry_point: target.entry_point.clone(),
            kind: target.kind,
            function_contract,
            args,
            time_ms,
            http_request,
        };
        let result = tokio::time::timeout(timeout, worker.invoke(&request, budget, handler))
            .await
            .context("native invocation deadline exceeded")??;
        let managed_action = matches!(
            target.kind,
            protocol::FunctionKind::Action | protocol::FunctionKind::HttpAction
        );
        if managed_action {
            // Even a successful action can leave managed background work. Do
            // not pool that process or let it survive the returned outcome.
            // A retirement error stays uncertain; no invocation is replayed.
            worker.retire().await?;
        } else if worker.invocations < self.0.manifest.worker.max_invocations {
            self.0
                .idle
                .lock()
                .await
                .entry(key)
                .or_default()
                .push(worker);
        }
        drop(permit);
        Ok(result)
    }

    pub async fn shutdown(&self) {
        self.0.idle.lock().await.clear();
    }
}
