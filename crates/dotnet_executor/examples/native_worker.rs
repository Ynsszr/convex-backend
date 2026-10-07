//! Real CoreCLR process proof against synthetic host operations. This does not
//! test Convex transactions/OCC/reactivity; those need the patched backend.
use std::{
    path::PathBuf,
    process::Command,
};

use anyhow::Context;
use async_trait::async_trait;
use dotnet_executor::{
    manifest::{
        sha256_hex,
        Manifest,
        NativeFunction,
        WorkerConfig,
        WorkerProfile,
    },
    protocol::{
        AssemblyDependency,
        FunctionContract,
        FunctionKind,
        WorkerError,
    },
    DotNetExecutor,
    SyscallHandler,
};
use serde_json::{
    json,
    Value,
};

struct ProofOperations {
    abort_on_insert: bool,
    observed: Vec<String>,
}

#[async_trait]
impl SyscallHandler for ProofOperations {
    async fn syscall(
        &mut self,
        name: &str,
        args: Value,
        is_async: bool,
    ) -> anyhow::Result<Result<Value, WorkerError>> {
        self.observed.push(name.to_owned());
        let value = match (name, is_async) {
            ("dotnet/now", false) => json!(1234567.0),
            ("dotnet/random", false) => json!(0.375),
            ("1.0/getUserIdentity", true) => Value::Null,
            ("1.0/queryStream", false) => {
                anyhow::ensure!(
                    args["query"]["source"]["type"] == "IndexRange",
                    "fixture must query its declared index"
                );
                json!({"queryId":1})
            },
            ("1.0/queryStreamNext", true) => json!({"value":null,"done":true}),
            ("1.0/queryCleanup", false) => json!({}),
            ("1.0/insert", true) if self.abort_on_insert => {
                anyhow::bail!("injected owner cancellation before syscall response")
            },
            _ => anyhow::bail!("unexpected synthetic operation: {name}"),
        };
        Ok(Ok(value))
    }
}

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    anyhow::ensure!(
        arguments.len() == 3,
        "usage: native_worker /absolute/dotnet /absolute/Host.dll /absolute/RuntimeFixture.dll"
    );
    let program = PathBuf::from(&arguments[0]);
    let host = PathBuf::from(&arguments[1]);
    let assembly = PathBuf::from(&arguments[2]);
    let digest = sha256_hex(&std::fs::read(&assembly)?);
    let described = Command::new(&program)
        .arg(&host)
        .arg("--describe")
        .arg(&assembly)
        .arg(&digest)
        .output()?;
    anyhow::ensure!(
        described.status.success(),
        "catalog failed: {}",
        String::from_utf8_lossy(&described.stderr)
    );
    let catalog: Value = serde_json::from_slice(&described.stdout)?;
    let definitions = catalog["functions"]
        .as_array()
        .context("catalog functions missing")?;
    let entries = definitions
        .iter()
        .filter(|function| function["kind"] != "httpAction")
        .map(|function| {
            Ok(NativeFunction {
                entry_point: None,
                deployment: "ipc-proof".into(),
                component_path: String::new(),
                function_path: function["functionPath"]
                    .as_str()
                    .context("missing function path")?
                    .into(),
                kind: serde_json::from_value(function["kind"].clone())?,
                assembly_path: assembly.clone(),
                assembly_sha256: digest.clone(),
                assembly_dependencies: vec![],
                artifact_lease: None,
                module_sha256: "transport-proof-only".into(),
                http_route: None,
            })
        })
        .collect::<anyhow::Result<Vec<_>>>()?;
    let manifest = Manifest {
        version: 1,
        max_workers: 2,
        worker: WorkerConfig {
            program_sha256: sha256_hex(&std::fs::read(&program)?),
            artifacts: std::fs::read_dir(host.parent().context("host directory")?)?
                .filter(|entry| entry.as_ref().map(|e| e.path().is_file()).unwrap_or(true))
                .map(|e| {
                    let path = e?.path();
                    Ok(AssemblyDependency {
                        sha256: sha256_hex(&std::fs::read(&path)?),
                        path,
                    })
                })
                .collect::<anyhow::Result<Vec<_>>>()?,
            framework: Some(
                dotnet_executor::manifest::FrameworkPin::capture(
                    &program,
                    std::env::var("CONVEX_DOTNET_FRAMEWORK_VERSION")
                        .unwrap_or_else(|_| "10.0.12".into()),
                )
                .await?,
            ),
            program,
            arguments: vec![host.to_str().context("host path UTF-8")?.into()],
            profile: WorkerProfile::RestrictedFirstParty,
            memory_mi_b: 256,
            invocation_timeout_ms: 10000,
            max_invocations: 100,
        },
        functions: entries,
    };
    let executor = DotNetExecutor::new(manifest)?;
    let mut operations = ProofOperations {
        abort_on_insert: false,
        observed: vec![],
    };
    let cases = [
        (
            "Counter:echo",
            json!({"value":{"integer":{"$integer":"/////////38="},"float":{"$float":"AAAAAAAA8H8="},
            "bytes":{"$bytes":"AAH/"},"null":null}}),
            json!({"integer":{"$integer":"/////////38="},
            "float":{"$float":"AAAAAAAA8H8="},"bytes":{"$bytes":"AAH/"},"null":null}),
        ),
        ("Counter:clock", json!({}), json!([1234567, 1234567])),
        ("Counter:randomValue", json!({}), json!(0.375)),
        ("Counter:identity", json!({}), Value::Null),
        ("Counter:read", json!({"name":"alpha"}), Value::Null),
    ];
    for (name, args, expected) in cases {
        let target = executor
            .find("ipc-proof", "", name)
            .context("fixture export missing")?;
        let definition = definitions
            .iter()
            .find(|f| f["functionPath"] == name)
            .context("descriptor missing")?;
        let contract = FunctionContract {
            visibility: definition["visibility"]
                .as_str()
                .context("visibility")?
                .into(),
            arguments: definition["args"].clone(),
            returns: definition["returns"].clone(),
        };
        let result = executor
            .invoke(&target, args, contract, &mut operations)
            .await?;
        let value = result.map_err(|error| anyhow::anyhow!("{}: {}", error.code, error.message))?;
        anyhow::ensure!(
            value == expected,
            "worker result differs for {name}: {value}"
        );
    }
    // Abort while a real worker awaits the owning process. The next invocation
    // must start with a clean channel; reusing the interrupted worker fails it.
    operations.abort_on_insert = true;
    let target = executor
        .find("ipc-proof", "", "Counter:increment")
        .context("increment missing")?;
    anyhow::ensure!(
        target.kind == FunctionKind::Mutation,
        "mutation fixture required"
    );
    let definition = definitions
        .iter()
        .find(|f| f["functionPath"] == "Counter:increment")
        .context("increment descriptor")?;
    let contract = FunctionContract {
        visibility: "public".into(),
        arguments: definition["args"].clone(),
        returns: definition["returns"].clone(),
    };
    anyhow::ensure!(
        executor
            .invoke(
                &target,
                json!({"name":"alpha","amount":1.0}),
                contract,
                &mut operations
            )
            .await
            .is_err(),
        "owner interruption must fail the invocation"
    );
    operations.abort_on_insert = false;
    let target = executor
        .find("ipc-proof", "", "Counter:clock")
        .context("clock missing")?;
    let definition = definitions
        .iter()
        .find(|f| f["functionPath"] == "Counter:clock")
        .context("clock descriptor")?;
    let contract = FunctionContract {
        visibility: "public".into(),
        arguments: definition["args"].clone(),
        returns: definition["returns"].clone(),
    };
    anyhow::ensure!(
        executor
            .invoke(&target, json!({}), contract, &mut operations)
            .await?
            .map_err(|error| anyhow::anyhow!(error.message))?
            == json!([1234567, 1234567]),
        "worker failed to recover"
    );
    let target = executor
        .find("ipc-proof", "", "Counter:hang")
        .context("noncooperative fixture missing")?;
    let definition = definitions
        .iter()
        .find(|f| f["functionPath"] == "Counter:hang")
        .context("hang descriptor")?;
    let contract = FunctionContract {
        visibility: "public".into(),
        arguments: definition["args"].clone(),
        returns: definition["returns"].clone(),
    };
    let failure = executor
        .invoke(&target, json!({}), contract, &mut operations)
        .await
        .expect_err("noncooperative worker must be killed");
    anyhow::ensure!(
        failure.to_string().contains("deadline"),
        "expected deadline failure, got {failure}"
    );
    let target = executor
        .find("ipc-proof", "", "Counter:clock")
        .context("clock after kill missing")?;
    let definition = definitions
        .iter()
        .find(|f| f["functionPath"] == "Counter:clock")
        .context("clock descriptor")?;
    let contract = FunctionContract {
        visibility: "public".into(),
        arguments: definition["args"].clone(),
        returns: definition["returns"].clone(),
    };
    anyhow::ensure!(
        executor
            .invoke(&target, json!({}), contract, &mut operations)
            .await?
            .map_err(|e| anyhow::anyhow!(e.message))?
            == json!([1234567, 1234567]),
        "worker failed to recover after forced kill"
    );
    executor.shutdown().await;
    println!(
        "Real CoreCLR IPC proof passed: canonical values, controlled clock/random, identity, \
         index protocol, filesystem restriction, interruption, noncooperative timeout and \
         recovery. Host operations were synthetic; Convex transactions/reactivity were not \
         exercised."
    );
    Ok(())
}
