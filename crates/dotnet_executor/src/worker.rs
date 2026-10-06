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
    io::AsyncReadExt,
    process::{
        Child,
        ChildStdin,
        ChildStdout,
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

pub struct Worker {
    child: Child,
    stdin: ChildStdin,
    stdout: ChildStdout,
    memory_limit_bytes: u64,
    pub invocations: u64,
}

impl Worker {
    /// Retained loader/JIT state is not an invocation's memory allocation.
    /// Retire between invocations before it consumes the next call's headroom;
    /// a request has not been written and no capability has been dispatched.
    pub async fn reusable(&mut self) -> bool {
        if !matches!(self.child.try_wait(), Ok(None)) {
            return false;
        }
        let Some(pid) = self.child.id() else {
            return false;
        };
        match resident_memory(pid).await {
            Ok(bytes) => bytes > 0 && bytes < self.memory_limit_bytes - self.memory_limit_bytes / 4,
            Err(_) => false,
        }
    }

    pub async fn spawn(config: &WorkerConfig, target: &NativeFunction) -> anyhow::Result<Self> {
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
            child,
            stdin,
            stdout,
            memory_limit_bytes: config.memory_mi_b * 1024 * 1024,
            invocations: 0,
        })
    }

    /// Reap an action process before publishing its terminal result. CLR tasks
    /// and network clients are not confined by unloading an AssemblyLoadContext.
    pub async fn retire(&mut self) -> anyhow::Result<()> {
        if self.child.try_wait()?.is_none() {
            self.child.start_kill().context("retiring native action worker")?;
        }
        tokio::time::timeout(Duration::from_secs(5), self.child.wait())
            .await
            .context("native action worker retirement deadline exceeded")?
            .context("reaping native action worker")?;
        Ok(())
    }

    pub async fn invoke(
        &mut self,
        request: &Invoke,
        budget: ExecutionBudget,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<InvocationResult> {
        self.execute(request, &request.invocation_id, budget, handler)
            .await
    }

    pub async fn describe(
        &mut self,
        request: &protocol::Describe,
        budget: ExecutionBudget,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<InvocationResult> {
        self.execute(request, &request.invocation_id, budget, handler)
            .await
    }

    pub async fn initialize_component(
        &mut self,
        request: &protocol::InitializeComponent,
        budget: ExecutionBudget,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<InvocationResult> {
        self.execute(request, &request.invocation_id, budget, handler)
            .await
    }

    async fn execute(
        &mut self,
        request: &impl serde::Serialize,
        expected_id: &str,
        budget: ExecutionBudget,
        handler: &mut impl SyscallHandler,
    ) -> anyhow::Result<InvocationResult> {
        protocol::write_frame(&mut self.stdin, request).await?;
        let mut last_request_id = None;
        let mut user_execution_time = Duration::ZERO;
        let mut system_execution_time = Duration::ZERO;
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
                            check_memory(self.child.id().context("worker exited")?,self.memory_limit_bytes).await?;
                        }
                    }
                }
            }).await.context("native user deadline exceeded")??;
            user_execution_time += user_started.elapsed();
            let message: WorkerMessage =
                serde_json::from_value(frame).context("invalid native worker message")?;
            match message {
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
                    let response=tokio::time::timeout(remaining_system,async {
                        loop {
                            tokio::select! {
                                value=&mut call=>break value,
                                _=tokio::time::sleep(Duration::from_millis(50))=>check_memory(self.child.id().context("worker exited")?,self.memory_limit_bytes).await?,
                            }
                        }
                    }).await.context("native syscall deadline exceeded")??;
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
                    check_memory(
                        self.child.id().context("worker exited")?,
                        self.memory_limit_bytes,
                    )
                    .await?;
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
                    check_memory(
                        self.child.id().context("worker exited")?,
                        self.memory_limit_bytes,
                    )
                    .await?;
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
        return Ok(total);
    }
    #[cfg(not(target_os = "linux"))]
    {
        let _ = pid;
        anyhow::bail!("native process memory supervision requires Linux");
    }
}

async fn check_memory(pid: u32, limit: u64) -> anyhow::Result<()> {
    anyhow::ensure!(
        resident_memory(pid).await? <= limit,
        "native worker exceeded resident memory budget"
    );
    Ok(())
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
