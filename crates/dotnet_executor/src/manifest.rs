use std::{
    collections::BTreeSet,
    path::{
        Path,
        PathBuf,
    },
};

use anyhow::Context;
use serde::Deserialize;
use sha2::{
    Digest,
    Sha256,
};

use crate::protocol::{
    AssemblyDependency,
    FunctionKind,
    PROTOCOL_VERSION,
};

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NativeFunction {
    pub deployment: String,
    pub component_path: String,
    pub function_path: String,
    #[serde(default)]
    pub entry_point: Option<String>,
    pub kind: FunctionKind,
    pub assembly_path: PathBuf,
    pub assembly_sha256: String,
    pub assembly_dependencies: Vec<AssemblyDependency>,
    /// Process-local extraction ownership. It is never supplied by a persisted
    /// manifest or publisher, and survives target selection until dispatch
    /// ends.
    #[serde(skip)]
    pub artifact_lease: Option<crate::capsule::ArtifactLease>,
    /// Convex ModuleMetadata.sha256 (base64; source plus source map).
    pub module_sha256: String,
    #[serde(default)]
    pub http_route: Option<NativeHttpRoute>,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NativeHttpRoute {
    pub module_path: String,
    pub module_sha256: String,
    /// Normalized SDK method, including GET for incoming HEAD requests.
    pub method: String,
    /// Exact path or SDK analyzed prefix pattern, such as /prefix/*.
    pub path: String,
}

/// RestrictedFirstParty adds an OS process boundary. It is not an admission
/// policy for arbitrary customer IL: ambient clocks, mutable statics and
/// foreign code still need a separately enforced deterministic-code admission
/// policy.
#[derive(Clone, Copy, Debug, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum WorkerProfile {
    RestrictedFirstParty,
    TrustedDevelopment,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct WorkerConfig {
    pub program: PathBuf,
    pub program_sha256: String,
    pub artifacts: Vec<AssemblyDependency>,
    #[serde(default)]
    pub framework: Option<FrameworkPin>,
    pub arguments: Vec<String>,
    pub profile: WorkerProfile,
    #[serde(rename = "memoryMiB")]
    pub memory_mi_b: u64,
    pub invocation_timeout_ms: u64,
    pub max_invocations: u64,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FrameworkPin {
    pub version: String,
    /// Exact selected CoreCLR framework and complete host/fxr membership. The
    /// launcher chooses the highest installed hostfxr, so every loader is
    /// pinned.
    pub artifacts: Vec<AssemblyDependency>,
}

impl FrameworkPin {
    fn files(&self, program: &Path) -> anyhow::Result<BTreeSet<PathBuf>> {
        let parts: Vec<_> = self.version.split('.').collect();
        anyhow::ensure!(
            parts.len() == 3
                && parts.iter().all(|part| !part.is_empty()
                    && part.len() <= 5
                    && part.bytes().all(|b| b.is_ascii_digit())),
            "an exact released CLR framework version is required"
        );
        let root = program
            .canonicalize()?
            .parent()
            .context("native runtime root missing")?
            .to_owned();
        let mut result = BTreeSet::new();
        fn walk(path: &Path, files: &mut BTreeSet<PathBuf>) -> anyhow::Result<()> {
            let metadata = std::fs::symlink_metadata(path)?;
            anyhow::ensure!(
                !metadata.file_type().is_symlink(),
                "native runtime closure cannot contain symbolic links"
            );
            if metadata.is_dir() {
                for entry in std::fs::read_dir(path)? {
                    walk(&entry?.path(), files)?;
                }
            } else {
                anyhow::ensure!(
                    metadata.is_file() && metadata.len() > 0 && metadata.len() <= 64 * 1024 * 1024,
                    "invalid native runtime artifact"
                );
                files.insert(path.to_owned());
                anyhow::ensure!(
                    files.len() <= 256,
                    "native runtime closure exceeds artifact bound"
                );
            }
            Ok(())
        }
        walk(
            &root
                .join("shared/Microsoft.NETCore.App")
                .join(&self.version),
            &mut result,
        )?;
        walk(&root.join("host/fxr"), &mut result)?;
        anyhow::ensure!(
            result.contains(
                &root
                    .join("shared/Microsoft.NETCore.App")
                    .join(&self.version)
                    .join("System.Private.CoreLib.dll")
            ),
            "native CoreCLR framework is incomplete"
        );
        Ok(result)
    }

    fn validate(&self, program: &Path) -> anyhow::Result<()> {
        validate_artifacts(&self.artifacts)?;
        let expected = self.files(program)?;
        let actual = self
            .artifacts
            .iter()
            .map(|artifact| artifact.path.clone())
            .collect();
        anyhow::ensure!(
            expected == actual,
            "native runtime closure has added, missing or unpinned files"
        );
        Ok(())
    }

    pub async fn capture(program: &Path, version: String) -> anyhow::Result<Self> {
        let mut pin = Self {
            version,
            artifacts: vec![],
        };
        for path in pin.files(program)? {
            let sha256 = sha256_hex(&tokio::fs::read(&path).await?);
            pin.artifacts.push(AssemblyDependency { path, sha256 });
        }
        Ok(pin)
    }
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Manifest {
    pub version: u32,
    pub max_workers: usize,
    pub worker: WorkerConfig,
    pub functions: Vec<NativeFunction>,
}

pub fn sha256_hex(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

impl Manifest {
    pub fn load(path: &Path) -> anyhow::Result<Self> {
        use std::io::Read;
        const LIMIT: u64 = 1024 * 1024;
        let file = std::fs::File::open(path)
            .with_context(|| format!("reading native manifest {path:?}"))?;
        let mut bytes = Vec::new();
        file.take(LIMIT + 1).read_to_end(&mut bytes)?;
        anyhow::ensure!(bytes.len() as u64 <= LIMIT, "native manifest is too large");
        let manifest: Self = serde_json::from_slice(&bytes)?;
        manifest.validate()?;
        Ok(manifest)
    }

    pub fn validate(&self) -> anyhow::Result<()> {
        anyhow::ensure!(
            self.version == PROTOCOL_VERSION,
            "unsupported native manifest version"
        );
        anyhow::ensure!(
            (1..=64).contains(&self.max_workers),
            "invalid native maxWorkers"
        );
        anyhow::ensure!(
            self.worker.program.is_absolute(),
            "worker program must be absolute"
        );
        validate_digest(&self.worker.program_sha256)?;
        validate_artifacts(&self.worker.artifacts)?;
        match &self.worker.framework {
            Some(framework) => framework.validate(&self.worker.program)?,
            None => anyhow::ensure!(
                matches!(self.worker.profile, WorkerProfile::TrustedDevelopment),
                "restricted native workers require a pinned CoreCLR framework closure"
            ),
        }
        for argument in &self.worker.arguments {
            let path = Path::new(argument);
            if path.is_absolute() {
                anyhow::ensure!(
                    self.worker.artifacts.iter().any(|a| a.path == path),
                    "worker argument artifact is not digest bound"
                );
            }
        }
        anyhow::ensure!(
            self.worker.invocation_timeout_ms > 0 && self.worker.invocation_timeout_ms <= 600_000,
            "native invocation timeout must be 1..600000 ms"
        );
        anyhow::ensure!(
            (64..=16384).contains(&self.worker.memory_mi_b),
            "invalid native memoryMiB"
        );
        anyhow::ensure!(
            self.worker.max_invocations > 0,
            "invalid native maxInvocations"
        );
        let mut keys = BTreeSet::new();
        let mut route_keys = BTreeSet::new();
        for function in &self.functions {
            anyhow::ensure!(
                !function.deployment.is_empty() && !function.function_path.starts_with("_system/"),
                "native deployment and user function path required"
            );
            anyhow::ensure!(
                function.function_path.contains(':'),
                "native function path must contain ':'"
            );
            crate::capsule::validate_function_identity(&function.function_path)?;
            if let Some(entry_point) = &function.entry_point {
                crate::capsule::validate_function_identity(entry_point)?;
            }
            anyhow::ensure!(
                function.assembly_path.is_absolute(),
                "assembly path must be absolute"
            );
            validate_digest(&function.assembly_sha256)?;
            validate_artifacts(&function.assembly_dependencies)?;
            anyhow::ensure!(
                !function.module_sha256.is_empty(),
                "deployed module digest is required"
            );
            if function.kind == FunctionKind::HttpAction {
                let route = function
                    .http_route
                    .as_ref()
                    .context("native HTTP route binding missing")?;
                anyhow::ensure!(
                    route.module_path == "http.js" && !route.module_sha256.is_empty(),
                    "native HTTP route requires owning http.js digest"
                );
                anyhow::ensure!(
                    matches!(
                        route.method.as_str(),
                        "DELETE" | "GET" | "OPTIONS" | "PATCH" | "POST" | "PUT"
                    ),
                    "invalid normalized native HTTP method"
                );
                anyhow::ensure!(
                    route.path.starts_with('/')
                        && (!route.path.contains('*')
                            || (route.path.ends_with("/*")
                                && route.path.matches('*').count() == 1)),
                    "invalid native HTTP route path"
                );
                anyhow::ensure!(
                    route_keys.insert((
                        &function.deployment,
                        &function.component_path,
                        &route.method,
                        &route.path
                    )),
                    "duplicate native HTTP route mapping"
                );
            } else {
                anyhow::ensure!(
                    function.http_route.is_none(),
                    "HTTP route binding requires httpAction"
                );
                anyhow::ensure!(
                    keys.insert((
                        &function.deployment,
                        &function.component_path,
                        normalized_function_path(&function.function_path)
                    )),
                    "duplicate native function mapping"
                );
            }
        }
        Ok(())
    }

    pub fn find(
        &self,
        deployment: &str,
        component_path: &str,
        function_path: &str,
    ) -> Option<&NativeFunction> {
        self.functions.iter().find(|f| {
            f.kind != FunctionKind::HttpAction
                && f.deployment == deployment
                && f.component_path == component_path
                && normalized_function_path(&f.function_path)
                    == normalized_function_path(function_path)
        })
    }

    pub fn has_http_functions(&self, deployment: &str, component_path: &str) -> bool {
        self.functions.iter().any(|f| {
            f.kind == FunctionKind::HttpAction
                && f.deployment == deployment
                && f.component_path == component_path
        })
    }

    pub fn find_http(
        &self,
        deployment: &str,
        component_path: &str,
        method: &str,
        route_path: &str,
    ) -> Option<&NativeFunction> {
        self.functions.iter().find(|f| {
            f.kind == FunctionKind::HttpAction
                && f.deployment == deployment
                && f.component_path == component_path
                && f.http_route
                    .as_ref()
                    .is_some_and(|route| route.method == method && route.path == route_path)
        })
    }
}

fn normalized_function_path(path: &str) -> String {
    match path.rsplit_once(':') {
        Some((module, member)) => {
            format!("{}:{member}", module.strip_suffix(".js").unwrap_or(module))
        },
        None => path.into(),
    }
}

impl NativeFunction {
    pub async fn verify_assembly(&self) -> anyhow::Result<()> {
        verify_artifact(&self.assembly_path, &self.assembly_sha256).await?;
        for dependency in &self.assembly_dependencies {
            verify_artifact(&dependency.path, &dependency.sha256).await?;
        }
        Ok(())
    }
}

pub(crate) fn validate_digest(digest: &str) -> anyhow::Result<()> {
    anyhow::ensure!(
        digest.len() == 64
            && digest
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)),
        "invalid SHA-256 digest"
    );
    Ok(())
}

fn validate_artifacts(artifacts: &[AssemblyDependency]) -> anyhow::Result<()> {
    anyhow::ensure!(artifacts.len() <= 256, "too many native artifacts");
    let mut paths = BTreeSet::new();
    for artifact in artifacts {
        anyhow::ensure!(
            artifact.path.is_absolute(),
            "native artifact must be absolute"
        );
        anyhow::ensure!(
            paths.insert(&artifact.path),
            "duplicate native artifact path"
        );
        validate_digest(&artifact.sha256)?;
    }
    Ok(())
}

async fn verify_artifact(path: &Path, digest: &str) -> anyhow::Result<()> {
    use tokio::io::AsyncReadExt;
    const LIMIT: u64 = 64 * 1024 * 1024;
    let file = tokio::fs::File::open(path)
        .await
        .with_context(|| format!("reading native artifact {path:?}"))?;
    let mut bytes = Vec::new();
    file.take(LIMIT + 1).read_to_end(&mut bytes).await?;
    anyhow::ensure!(bytes.len() as u64 <= LIMIT, "native artifact exceeds 64MiB");
    anyhow::ensure!(
        sha256_hex(&bytes) == digest,
        "native artifact SHA-256 mismatch: {path:?}"
    );
    Ok(())
}

impl WorkerConfig {
    pub async fn verify_artifacts(&self) -> anyhow::Result<()> {
        verify_artifact(&self.program, &self.program_sha256).await?;
        for artifact in &self.artifacts {
            verify_artifact(&artifact.path, &artifact.sha256).await?;
        }
        if let Some(framework) = &self.framework {
            framework.validate(&self.program)?;
            for artifact in &framework.artifacts {
                verify_artifact(&artifact.path, &artifact.sha256).await?;
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use serde_json::{
        json,
        Value,
    };

    use super::*;

    fn fixture() -> Value {
        json!({"version":1,"maxWorkers":2,"worker":{"program":"/usr/bin/true","programSha256":"00".repeat(32),
        "artifacts":[],"arguments":[],"profile":"trusted-development","memoryMiB":256,
        "invocationTimeoutMs":1000,"maxInvocations":100},"functions":[{"deployment":"proof","componentPath":"",
        "functionPath":"Counter:read","kind":"query","assemblyPath":"/tmp/proof.dll","assemblySha256":"00".repeat(32),
        "assemblyDependencies":[],"moduleSha256":"deployment-proof"}]})
    }

    #[test]
    fn explicit_entry_points_require_exact_user_function_identities() {
        for invalid in [
            "Counter",
            "Counter:",
            "Counter:read:extra",
            "_system/probe:read",
            "Counter:\nread",
        ] {
            let mut value = fixture();
            value["functions"][0]["entryPoint"] = json!(invalid);
            assert!(serde_json::from_value::<Manifest>(value)
                .unwrap()
                .validate()
                .is_err());
        }
        let mut value = fixture();
        value["functions"][0]["entryPoint"] = json!("Dbm.SharedTemplates:save");
        assert!(serde_json::from_value::<Manifest>(value)
            .unwrap()
            .validate()
            .is_ok());
    }

    #[test]
    fn canonical_module_aliases_cannot_be_ambiguous() {
        let mut value = fixture();
        let mut duplicate = value["functions"][0].clone();
        duplicate["functionPath"] = json!("Counter.js:read");
        value["functions"].as_array_mut().unwrap().push(duplicate);
        let manifest: Manifest = serde_json::from_value(value).unwrap();
        assert!(manifest.validate().is_err());
    }

    #[test]
    fn unpinned_worker_artifacts_are_refused() {
        let mut value = fixture();
        value["worker"]["arguments"] = json!(["/tmp/unsignedHost.dll"]);
        let manifest: Manifest = serde_json::from_value(value).unwrap();
        assert!(manifest.validate().is_err());
        assert!(serde_json::from_value::<Manifest>(fixture())
            .unwrap()
            .validate()
            .is_ok());
    }

    #[test]
    fn persisted_targets_cannot_supply_ephemeral_directory_leases() {
        let mut value = fixture();
        value["functions"][0]["artifactLease"] = json!({"path":"/tmp/foreign"});
        assert!(serde_json::from_value::<Manifest>(value).is_err());
        let admitted: Manifest = serde_json::from_value(fixture()).unwrap();
        assert!(admitted
            .functions
            .iter()
            .all(|function| function.artifact_lease.is_none()));
    }

    #[test]
    fn http_routes_require_explicit_unique_binding() {
        let mut value = fixture();
        value["functions"][0]["kind"] = json!("httpAction");
        let unbound: Manifest = serde_json::from_value(value.clone()).unwrap();
        assert!(unbound.validate().is_err());
        value["functions"][0]["httpRoute"] = json!({"modulePath":"http.js", "moduleSha256":"router-proof",
            "method":"POST", "path":"/echo"});
        assert!(serde_json::from_value::<Manifest>(value.clone())
            .unwrap()
            .validate()
            .is_ok());
        let mut second = value["functions"][0].clone();
        second["httpRoute"]["path"] = json!("/prefix/*");
        value["functions"]
            .as_array_mut()
            .unwrap()
            .push(second.clone());
        // One exported handler may serve more than one distinct route.
        assert!(serde_json::from_value::<Manifest>(value.clone())
            .unwrap()
            .validate()
            .is_ok());
        value["functions"].as_array_mut().unwrap().push(second);
        assert!(serde_json::from_value::<Manifest>(value)
            .unwrap()
            .validate()
            .is_err());
    }

    #[tokio::test]
    async fn modified_artifact_cannot_be_reused() {
        let path =
            std::env::temp_dir().join(format!("convex-native-digest-proof-{}", std::process::id()));
        std::fs::write(&path, b"admitted").unwrap();
        let digest = sha256_hex(b"admitted");
        assert!(verify_artifact(&path, &digest).await.is_ok());
        std::fs::write(&path, b"modified").unwrap();
        assert!(verify_artifact(&path, &digest).await.is_err());
        std::fs::remove_file(path).unwrap();
    }
}
