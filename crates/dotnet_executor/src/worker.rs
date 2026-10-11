use std::{
    collections::BTreeSet,
    path::{
        Path,
        PathBuf,
    },
    process::Stdio,
    time::{
        Duration,
        Instant,
    },
};

use anyhow::Context;
use tokio::{
    io::{
        AsyncRead,
        AsyncReadExt,
        AsyncWrite,
    },
    process::{
        Child,
        Command,
    },
};

use crate::{
    manifest::{
        NativeFunction,
        WorkerConfig,
        WorkerProfile,
    },
    protocol::{
        self,
        Invoke,
        SyscallResult,
        WorkerMessage,
        PROTOCOL_VERSION,
    },
    ExecutionBudget,
    InvocationResult,
    SyscallHandler,
};

#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct WorkerKey {
    deployment: String,
    component_path: String,
    assembly_path: PathBuf,
    digest: String,
    dependencies: Vec<(PathBuf, String)>,
    action: bool,
}

impl From<&NativeFunction> for WorkerKey {
    fn from(value: &NativeFunction) -> Self {
        Self {
            deployment: value.deployment.clone(),
            component_path: value.component_path.clone(),
            assembly_path: value.assembly_path.clone(),
            digest: value.assembly_sha256.clone(),
            dependencies: value
                .assembly_dependencies
                .iter()
                .map(|d| (d.path.clone(), d.sha256.clone()))
                .collect(),
            action: matches!(
                value.kind,
                protocol::FunctionKind::Action | protocol::FunctionKind::HttpAction
            ),
        }
    }
}

enum WorkerOwner {
    Process(Child),
    #[cfg(target_os = "linux")]
    Broker(crate::maintenance_broker::Lease),
}

pub struct Worker {
    owner: WorkerOwner,
    stdin: Box<dyn AsyncWrite + Send + Unpin>,
    stdout: Box<dyn AsyncRead + Send + Unpin>,
    memory_limit_bytes: u64,
    pub invocations: u64,
}

impl Worker {
    /// Retained loader/JIT state is not an invocation's memory allocation.
    /// Retire between invocations before it consumes the next call's headroom;
    /// a request has not been written and no capability has been dispatched.
    pub async fn reusable(&mut self) -> bool {
        let child = match &mut self.owner {
            WorkerOwner::Process(child) => child,
            // Admission descriptions are one-shot and never pooled.
            #[cfg(target_os = "linux")]
            WorkerOwner::Broker(_) => return false,
        };
        if !matches!(child.try_wait(), Ok(None)) {
            return false;
        }
        let Some(pid) = child.id() else {
            return false;
        };
        match resident_memory(pid).await {
            Ok(bytes) => bytes > 0 && bytes < self.memory_limit_bytes - self.memory_limit_bytes / 4,
            Err(_) => false,
        }
    }

    pub async fn spawn(config: &WorkerConfig, target: &NativeFunction) -> anyhow::Result<Self> {
        #[cfg(target_os = "linux")]
        if crate::maintenance_broker::selected() {
            let started = crate::maintenance_broker::Lease::spawn(config, target).await?;
            return Ok(Self {
                owner: WorkerOwner::Broker(started.lease),
                stdin: Box::new(started.write),
                stdout: Box::new(started.read),
                memory_limit_bytes: config.memory_mi_b * 1024 * 1024,
                invocations: 0,
            });
        }
        let mut command = match config.profile {
            WorkerProfile::TrustedDevelopment => Command::new(&config.program),
            WorkerProfile::RestrictedFirstParty => sandbox_command(config, target)?,
        };
        if matches!(config.profile, WorkerProfile::TrustedDevelopment) {
            if let Some(framework) = &config.framework {
                command.args(["--fx-version", &framework.version]);
            }
            command.args(&config.arguments);
        }
        command
            .env_clear()
            .env("DOTNET_EnableDiagnostics", "0")
            .env(
                "DOTNET_GCHeapHardLimit",
                format!("{:x}", config.memory_mi_b * 1024 * 1024),
            )
            .env("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
            .env("DOTNET_NOLOGO", "1")
            .env("LANG", "C.UTF-8")
            .env("PATH", "/usr/bin:/bin")
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true);
        if config.framework.is_some() {
            command
                .env(
                    "DOTNET_ROOT",
                    config
                        .program
                        .canonicalize()?
                        .parent()
                        .context("native runtime root missing")?,
                )
                .env("DOTNET_ROLL_FORWARD", "Disable");
        }
        let mut child = command.spawn().context("starting native CoreCLR worker")?;
        let stdin = child.stdin.take().context("missing worker stdin")?;
        let stdout = child.stdout.take().context("missing worker stdout")?;
        let mut stderr = child.stderr.take().context("missing worker stderr")?;
        // Drain without accumulating unbounded application output. Stderr isn't
        // a trusted protocol and must not be interpreted as backend instructions.
        tokio::spawn(async move {
            let mut buffer = [0u8; 4096];
            while matches!(stderr.read(&mut buffer).await, Ok(n) if n > 0) {}
        });
        Ok(Self {
            owner: WorkerOwner::Process(child),
            stdin: Box::new(stdin),
            stdout: Box::new(stdout),
            memory_limit_bytes: config.memory_mi_b * 1024 * 1024,
            invocations: 0,
        })
    }

    /// Reap an action process before publishing its terminal result. CLR tasks
    /// and network clients are not confined by unloading an
    /// AssemblyLoadContext.
    pub async fn retire(&mut self) -> anyhow::Result<()> {
        match &mut self.owner {
            WorkerOwner::Process(child) => {
                if child.try_wait()?.is_none() {
                    child.start_kill().context("retiring native worker")?;
                }
                tokio::time::timeout(Duration::from_secs(5), child.wait())
                    .await
                    .context("native worker retirement deadline exceeded")?
                    .context("reaping native worker")?;
                Ok(())
            },
            #[cfg(target_os = "linux")]
            WorkerOwner::Broker(lease) => lease.retire().await,
        }
    }

    pub async fn invoke(
        &mut self,
        request: &Invoke,
        budget: ExecutionBudget,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<InvocationResult> {
        let preparation_protocol_version = request.preparation_protocol_version.unwrap_or(0);
        anyhow::ensure!(
            preparation_protocol_version <= 1,
            "unsupported native preparation protocol version"
        );
        self.execute(
            request,
            &request.invocation_id,
            preparation_protocol_version,
            budget,
            handler,
        )
        .await
    }

    pub async fn describe(
        &mut self,
        request: &protocol::Describe,
        budget: ExecutionBudget,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<InvocationResult> {
        self.execute(request, &request.invocation_id, 0, budget, handler)
            .await
    }

    pub async fn initialize_component(
        &mut self,
        request: &protocol::InitializeComponent,
        budget: ExecutionBudget,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<InvocationResult> {
        self.execute(request, &request.invocation_id, 0, budget, handler)
            .await
    }

    async fn execute(
        &mut self,
        request: &impl serde::Serialize,
        expected_id: &str,
        preparation_protocol_version: u32,
        budget: ExecutionBudget,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<InvocationResult> {
        protocol::write_frame(&mut self.stdin, request).await?;
        let mut last_request_id = None;
        let mut user_execution_time = Duration::ZERO;
        let mut system_execution_time = Duration::ZERO;
        if preparation_protocol_version == 1 {
            // Only trusted loading/admission precedes this barrier. A worker
            // cannot dispatch a capability or return a value before admission.
            let preparation_started = Instant::now();
            let remaining_system = budget
                .system
                .checked_sub(system_execution_time)
                .context("native preparation deadline exceeded")?;
            let frame = tokio::time::timeout(remaining_system, async {
                let read = protocol::read_frame(&mut self.stdout);
                tokio::pin!(read);
                loop {
                    tokio::select! {
                        value = &mut read => break value,
                        _ = tokio::time::sleep(Duration::from_millis(50)) => {
                            self.owner.check_memory(self.memory_limit_bytes).await?;
                        },
                    }
                }
            })
            .await
            .context("native preparation deadline exceeded")??;
            let message: WorkerMessage =
                serde_json::from_value(frame).context("invalid native preparation message")?;
            match message {
                WorkerMessage::Prepared {
                    version,
                    invocation_id,
                } => {
                    validate_scope(version, &invocation_id, expected_id)?;
                    self.owner.check_memory(self.memory_limit_bytes).await?;
                    system_execution_time += preparation_started.elapsed();
                },
                WorkerMessage::Error {
                    version,
                    invocation_id,
                    error,
                } => {
                    validate_scope(version, &invocation_id, expected_id)?;
                    self.owner.check_memory(self.memory_limit_bytes).await?;
                    self.invocations += 1;
                    return Ok(InvocationResult {
                        result: Err(error),
                        user_execution_time,
                    });
                },
                WorkerMessage::Syscall { .. } | WorkerMessage::Result { .. } => {
                    anyhow::bail!("native worker dispatched before preparation barrier");
                },
            }
        }
        loop {
            let user_started = Instant::now();
            let remaining_user = budget
                .user
                .checked_sub(user_execution_time)
                .context("native user deadline exceeded")?;
            let frame = tokio::time::timeout(remaining_user, async {
                let read = protocol::read_frame(&mut self.stdout);
                tokio::pin!(read);
                loop {
                    tokio::select! {
                        value=&mut read=>break value,
                        _=tokio::time::sleep(Duration::from_millis(50))=> {
                            self.owner.check_memory(self.memory_limit_bytes).await?;
                        }
                    }
                }
            })
            .await
            .context("native user deadline exceeded")??;
            user_execution_time += user_started.elapsed();
            let message: WorkerMessage =
                serde_json::from_value(frame).context("invalid native worker message")?;
            match message {
                WorkerMessage::Prepared { .. } => anyhow::bail!("unexpected or repeated native preparation barrier"),
                WorkerMessage::Syscall {
                    version,
                    invocation_id,
                    request_id,
                    name,
                    args,
                    is_async,
                } => {
                    validate_scope(version, &invocation_id, expected_id)?;
                    anyhow::ensure!(
                        last_request_id.is_none_or(|last| request_id > last),
                        "duplicate/out-of-order worker syscall"
                    );
                    last_request_id = Some(request_id);
                    let remaining_system = budget
                        .system
                        .checked_sub(system_execution_time)
                        .context("native syscall deadline exceeded")?;
                    let system_started = Instant::now();
                    let call = handler.syscall(&name, args, is_async);
                    tokio::pin!(call);
                    let response = tokio::time::timeout(remaining_system, async {
                        loop {
                            tokio::select! {
                                value = &mut call => break value,
                                _ = tokio::time::sleep(Duration::from_millis(50)) => {
                                    self.owner.check_memory(self.memory_limit_bytes).await?;
                                },
                            }
                        }
                    })
                    .await
                    .context("native syscall deadline exceeded")??;
                    system_execution_time += system_started.elapsed();
                    let (value, error) = match response {
                        Ok(value) => (Some(value), None),
                        Err(error) => (None, Some(error)),
                    };
                    protocol::write_frame(
                        &mut self.stdin,
                        &SyscallResult {
                            message_type: "syscallResult",
                            version: PROTOCOL_VERSION,
                            invocation_id: expected_id.to_owned(),
                            request_id,
                            value,
                            error,
                        },
                    )
                    .await?;
                },
                WorkerMessage::Result {
                    version,
                    invocation_id,
                    value,
                } => {
                    validate_scope(version, &invocation_id, expected_id)?;
                    self.owner.check_memory(self.memory_limit_bytes).await?;
                    self.invocations += 1;
                    return Ok(InvocationResult {
                        result: Ok(value),
                        user_execution_time,
                    });
                },
                WorkerMessage::Error {
                    version,
                    invocation_id,
                    error,
                } => {
                    validate_scope(version, &invocation_id, expected_id)?;
                    self.owner.check_memory(self.memory_limit_bytes).await?;
                    self.invocations += 1;
                    return Ok(InvocationResult {
                        result: Err(error),
                        user_execution_time,
                    });
                },
            }
        }
    }
}

fn validate_scope(version: u32, actual: &str, expected: &str) -> anyhow::Result<()> {
    anyhow::ensure!(
        version == PROTOCOL_VERSION,
        "unsupported native protocol version"
    );
    anyhow::ensure!(actual == expected, "native invocation scope mismatch");
    Ok(())
}

async fn resident_memory(pid: u32) -> anyhow::Result<u64> {
    #[cfg(target_os = "linux")]
    {
        // Bubblewrap has a supervisor outside the PID namespace. Account for
        // its descendants, not just the small supervisor process itself.
        let mut remaining = vec![pid];
        let mut visited = BTreeSet::new();
        let mut total = 0u64;
        while let Some(pid) = remaining.pop() {
            if !visited.insert(pid) {
                continue;
            }
            anyhow::ensure!(visited.len() <= 64, "native worker process budget exceeded");
            let status = match tokio::fs::read_to_string(format!("/proc/{pid}/status")).await {
                Ok(status) => status,
                Err(error) if error.kind() == std::io::ErrorKind::NotFound && visited.len() > 1 => {
                    continue
                },
                Err(error) => {
                    return Err(error).context("native memory supervision cannot inspect worker")
                },
            };
            if let Some(rss) = status.lines().find_map(|line| {
                line.strip_prefix("VmRSS:")
                    .and_then(|v| v.split_whitespace().next())
                    .and_then(|v| v.parse::<u64>().ok())
            }) {
                total = total.saturating_add(rss.saturating_mul(1024));
            }
            // A child launched from a CLR pool thread is listed under that
            // task's children, not necessarily under the process leader.
            if let Ok(mut tasks) = tokio::fs::read_dir(format!("/proc/{pid}/task")).await {
                let mut task_count = 0usize;
                while let Some(task) = tasks.next_entry().await? {
                    task_count += 1;
                    anyhow::ensure!(task_count <= 256, "native worker thread budget exceeded");
                    if let Ok(children) =
                        tokio::fs::read_to_string(task.path().join("children")).await
                    {
                        remaining.extend(
                            children
                                .split_whitespace()
                                .filter_map(|v| v.parse::<u32>().ok()),
                        );
                    }
                }
            }
        }
        Ok(total)
    }
    #[cfg(not(target_os = "linux"))]
    {
        let _ = pid;
        anyhow::bail!("native process memory supervision requires Linux");
    }
}

impl WorkerOwner {
    async fn check_memory(&self, limit: u64) -> anyhow::Result<()> {
        let bytes = match self {
            Self::Process(child) => resident_memory(child.id().context("worker exited")?).await?,
            #[cfg(target_os = "linux")]
            Self::Broker(lease) => lease.resident_memory().await?,
        };
        anyhow::ensure!(
            bytes <= limit,
            "native worker exceeded resident memory budget"
        );
        Ok(())
    }
}

fn sandbox_command(config: &WorkerConfig, target: &NativeFunction) -> anyhow::Result<Command> {
    anyhow::ensure!(
        cfg!(target_os = "linux"),
        "restricted native workers require Linux bubblewrap"
    );
    let mut command = Command::new("/usr/bin/bwrap");
    command.args([
        "--unshare-all",
        "--die-with-parent",
        "--new-session",
        "--chdir",
        "/",
        "--clearenv",
        "--proc",
        "/proc",
        "--dev",
        "/dev",
        "--tmpfs",
        "/tmp",
    ]);
    if matches!(
        target.kind,
        protocol::FunctionKind::Action | protocol::FunctionKind::HttpAction
    ) {
        command.arg("--share-net");
        for file in [
            "/etc/ssl",
            "/etc/resolv.conf",
            "/etc/hosts",
            "/etc/nsswitch.conf",
        ] {
            if Path::new(file).exists() {
                command.args(["--ro-bind", file, file]);
            }
        }
    }
    let mut roots = BTreeSet::from([
        PathBuf::from("/usr"),
        PathBuf::from("/lib"),
        PathBuf::from("/lib64"),
    ]);
    roots.insert(
        config
            .program
            .canonicalize()?
            .parent()
            .context("worker program parent")?
            .to_owned(),
    );
    for dependency in &target.assembly_dependencies {
        roots.insert(
            dependency
                .path
                .parent()
                .context("dependency parent")?
                .to_owned(),
        );
    }
    for artifact in &config.artifacts {
        roots.insert(
            artifact
                .path
                .parent()
                .context("worker artifact parent")?
                .to_owned(),
        );
    }
    roots.insert(
        target
            .assembly_path
            .parent()
            .context("assembly parent")?
            .to_owned(),
    );
    for argument in &config.arguments {
        let path = Path::new(argument);
        if path.is_absolute() && path.exists() {
            roots.insert(path.parent().context("worker artifact parent")?.to_owned());
        }
    }
    for root in roots {
        if root.exists() {
            command.arg("--ro-bind").arg(&root).arg(&root);
        }
    }
    command.args([
        "--setenv",
        "DOTNET_EnableDiagnostics",
        "0",
        "--setenv",
        "DOTNET_CLI_TELEMETRY_OPTOUT",
        "1",
        "--setenv",
        "LANG",
        "C.UTF-8",
        "--setenv",
        "PATH",
        "/usr/bin:/bin",
        "--setenv",
        "HOME",
        "/tmp",
    ]);
    command
        .arg("--setenv")
        .arg("DOTNET_GCHeapHardLimit")
        .arg(format!("{:x}", config.memory_mi_b * 1024 * 1024));
    if config.framework.is_some() {
        command
            .arg("--setenv")
            .arg("DOTNET_ROOT")
            .arg(
                config
                    .program
                    .canonicalize()?
                    .parent()
                    .context("native runtime root missing")?,
            )
            .args(["--setenv", "DOTNET_ROLL_FORWARD", "Disable"]);
    }
    command.arg("--").arg(&config.program);
    if let Some(framework) = &config.framework {
        command.args(["--fx-version", &framework.version]);
    }
    command.args(&config.arguments);
    Ok(command)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn response_scope_cannot_cross_invocations() {
        assert!(validate_scope(1, "2", "1").is_err());
        assert!(validate_scope(2, "1", "1").is_err());
        assert!(validate_scope(1, "1", "1").is_ok());
    }
}

#[cfg(test)]
mod preparation_barrier_tests {
    use std::sync::{
        atomic::{
            AtomicUsize,
            Ordering,
        },
        Arc,
    };

    use async_trait::async_trait;
    use serde_json::{
        json,
        Value,
    };

    use super::*;
    use crate::protocol::{
        FunctionContract,
        FunctionKind,
        WorkerError,
    };

    const PROTOTYPE: &str = r#"import json,struct,sys,time
q=json.loads(sys.stdin.buffer.read(struct.unpack('<I',sys.stdin.buffer.read(4))[0]))
a=q['args']; mode=a['case']; identity=q['invocationId']
def send(kind, **fields):
    value={'type':kind,'version':1,'invocationId':identity,**fields}
    data=json.dumps(value).encode();sys.stdout.buffer.write(struct.pack('<I',len(data))+data);sys.stdout.buffer.flush()
if mode=='legacy':
    assert 'preparationProtocolVersion' not in q
    time.sleep(a.get('preparationDelay',0)); send('result',value=7);sys.exit(0)
assert q['preparationProtocolVersion']==1
if mode=='missing':time.sleep(20);sys.exit(0)
if mode=='pre-error':send('error',error={'code':'SyntheticAdmissionRefusal','message':'bounded refusal'});sys.exit(0)
if mode=='pre-syscall':send('syscall',requestId=1,name='synthetic',args={},**{'async':True});sys.exit(0)
if mode=='pre-result':send('result',value=7);sys.exit(0)
time.sleep(a.get('preparationDelay',0))
if mode=='wrong-scope':identity='other-original'
if mode=='wrong-version':send('prepared',version=2);sys.exit(0)
send('prepared')
if mode=='repeated':send('prepared');sys.exit(0)
if mode=='syscall':
    send('syscall',requestId=1,name='synthetic',args={},**{'async':True})
    h=sys.stdin.buffer.read(4)
    if h:sys.stdin.buffer.read(struct.unpack('<I',h)[0])
time.sleep(a.get('handlerDelay',0));send('result',value=7)
"#;

    struct Handler {
        calls: Arc<AtomicUsize>,
        delay: Duration,
    }
    #[async_trait]
    impl SyscallHandler for Handler {
        async fn syscall(
            &mut self,
            name: &str,
            _: Value,
            _: bool,
        ) -> anyhow::Result<Result<Value, WorkerError>> {
            assert_eq!(name, "synthetic");
            self.calls.fetch_add(1, Ordering::SeqCst);
            tokio::time::sleep(self.delay).await;
            Ok(Ok(Value::Null))
        }
    }
    async fn attempt(
        case: &str,
        protocol: Option<u32>,
        preparation: f64,
        handler_delay: f64,
        system: Duration,
        syscall: Duration,
    ) -> (anyhow::Result<InvocationResult>, usize, Duration) {
        let folder = tempfile::tempdir().unwrap();
        let script = folder.path().join("finite-worker.py");
        std::fs::write(&script, PROTOTYPE).unwrap();
        let config = WorkerConfig {
            program: std::fs::canonicalize("/usr/bin/python3").unwrap(),
            program_sha256: "00".repeat(32),
            artifacts: vec![],
            framework: None,
            arguments: vec![script.to_str().unwrap().into()],
            profile: WorkerProfile::TrustedDevelopment,
            memory_mi_b: 64,
            invocation_timeout_ms: 16_000,
            max_invocations: 1,
            preparation_protocol_version: protocol.unwrap_or(0),
        };
        let target = NativeFunction {
            deployment: "isolated-preparation-proof".into(),
            component_path: String::new(),
            function_path: "synthetic:proof".into(),
            entry_point: None,
            kind: FunctionKind::Query,
            assembly_path: script,
            assembly_sha256: "00".repeat(32),
            assembly_dependencies: vec![],
            artifact_lease: None,
            module_sha256: "synthetic".into(),
            http_route: None,
        };
        let mut worker = Worker::spawn(&config, &target).await.unwrap();
        let calls = Arc::new(AtomicUsize::new(0));
        let mut handler = Handler {
            calls: calls.clone(),
            delay: syscall,
        };
        let request = Invoke {
            message_type: "invoke",
            version: 1,
            invocation_id: "exact-original".into(),
            assembly_path: target.assembly_path.to_str().unwrap().into(),
            assembly_sha256: target.assembly_sha256.clone(),
            assembly_dependencies: vec![],
            function_path: target.function_path.clone(),
            entry_point: None,
            kind: FunctionKind::Query,
            function_contract: FunctionContract {
                visibility: "public".into(),
                arguments: json!({}),
                returns: json!({}),
            },
            args: json!({"case":case,"preparationDelay":preparation,"handlerDelay":handler_delay}),
            http_request: None,
            preparation_protocol_version: protocol,
        };
        let started = Instant::now();
        // Exactly the selected worker outer bound remains independent.
        let outcome = tokio::time::timeout(
            Duration::from_secs(16),
            worker.invoke(
                &request,
                ExecutionBudget {
                    user: Duration::from_secs(1),
                    system,
                },
                &mut handler,
            ),
        )
        .await
        .context("synthetic outer worker deadline exceeded")
        .and_then(|value| value);
        let elapsed = started.elapsed();
        worker.retire().await.unwrap();
        (outcome, calls.load(Ordering::SeqCst), elapsed)
    }
    #[tokio::test]
    async fn delayed_preparation_preserves_the_original_handler_budget() {
        let (result, calls, elapsed) = attempt(
            "normal",
            Some(1),
            1.2,
            0.05,
            Duration::from_secs(15),
            Duration::ZERO,
        )
        .await;
        let result = result.unwrap();
        assert_eq!(result.result.unwrap(), json!(7));
        assert_eq!(calls, 0);
        assert!(elapsed >= Duration::from_millis(1200));
        assert!(result.user_execution_time < Duration::from_millis(500));
    }
    #[tokio::test]
    async fn pre_syscall_handler_loop_still_has_one_second() {
        let (result, calls, elapsed) = attempt(
            "normal",
            Some(1),
            0.01,
            1.2,
            Duration::from_secs(15),
            Duration::ZERO,
        )
        .await;
        assert!(result
            .err()
            .unwrap()
            .to_string()
            .contains("native user deadline"));
        assert_eq!(calls, 0);
        assert!(elapsed < Duration::from_secs(3));
    }
    #[tokio::test]
    async fn missing_preparation_has_fifteen_seconds_and_no_dispatch() {
        let (result, calls, elapsed) = attempt(
            "missing",
            Some(1),
            0.,
            0.,
            Duration::from_secs(15),
            Duration::ZERO,
        )
        .await;
        assert!(result
            .err()
            .unwrap()
            .to_string()
            .contains("native preparation deadline"));
        assert_eq!(calls, 0);
        assert!(elapsed >= Duration::from_millis(14_500));
        assert!(elapsed < Duration::from_secs(16));
    }
    #[tokio::test]
    async fn wrong_repeated_and_prebarrier_dispatch_are_refused_without_owner_calls() {
        for (case, message) in [
            ("wrong-scope", "scope mismatch"),
            ("wrong-version", "protocol version"),
            ("repeated", "repeated"),
            ("pre-syscall", "before preparation"),
            ("pre-result", "before preparation"),
        ] {
            let (result, calls, _) = attempt(
                case,
                Some(1),
                0.,
                0.,
                Duration::from_secs(15),
                Duration::ZERO,
            )
            .await;
            assert!(
                result.err().unwrap().to_string().contains(message),
                "case {case}"
            );
            assert_eq!(calls, 0);
        }
    }
    #[tokio::test]
    async fn terminal_preparation_error_is_an_original_refusal() {
        let (result, calls, _) = attempt(
            "pre-error",
            Some(1),
            0.,
            0.,
            Duration::from_secs(15),
            Duration::ZERO,
        )
        .await;
        assert_eq!(
            result.unwrap().result.unwrap_err().code,
            "SyntheticAdmissionRefusal"
        );
        assert_eq!(calls, 0);
    }
    #[tokio::test]
    async fn preparation_consumes_system_budget_before_an_actual_syscall() {
        let (result, calls, _) = attempt(
            "syscall",
            Some(1),
            0.7,
            0.,
            Duration::from_secs(1),
            Duration::from_millis(600),
        )
        .await;
        assert!(result
            .err()
            .unwrap()
            .to_string()
            .contains("native syscall deadline"));
        assert_eq!(calls, 1);
    }
    #[tokio::test]
    async fn legacy_request_wire_and_timer_are_unchanged() {
        let (result, calls, _) = attempt(
            "legacy",
            None,
            0.01,
            0.,
            Duration::from_secs(15),
            Duration::ZERO,
        )
        .await;
        assert_eq!(result.unwrap().result.unwrap(), json!(7));
        assert_eq!(calls, 0);
        let (result, calls, _) = attempt(
            "legacy",
            None,
            1.2,
            0.,
            Duration::from_secs(15),
            Duration::ZERO,
        )
        .await;
        assert!(result
            .err()
            .unwrap()
            .to_string()
            .contains("native user deadline"));
        assert_eq!(calls, 0);
    }
}
