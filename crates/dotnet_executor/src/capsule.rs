//! Persisted native source-package contents. Uploaded data carries bytes and
//! hashes, never backend filesystem paths. Convex's module source hash and
//! owning deploy transaction bind the complete capsule.
use std::{
    collections::{
        BTreeMap,
        BTreeSet,
    },
    path::PathBuf,
    sync::Arc,
    time::Instant,
};

use anyhow::Context;
use serde::Deserialize;
use serde_json::Value;
use tokio::sync::Mutex;

use crate::{
    manifest::{
        sha256_hex,
        validate_digest,
    },
    protocol::{
        AssemblyDependency,
        FunctionKind,
    },
};

pub const MAX_CAPSULE_BYTES: usize = 16 * 1024 * 1024;
const MAX_ARTIFACT_BYTES: usize = 8 * 1024 * 1024;
const MAX_TOTAL_ARTIFACT_BYTES: usize = 10 * 1024 * 1024;
const MAX_CACHED_GRAPHS: usize = 64;
const MAX_CACHED_BYTES: usize = 512 * 1024 * 1024;

#[derive(Clone, Copy, Debug, Deserialize, serde::Serialize, Eq, PartialEq)]
#[serde(rename_all = "camelCase")]
pub enum ModuleKind {
    Functions,
    Schema,
    Auth,
    Http,
    Crons,
    Component,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Artifact {
    pub name: String,
    pub sha256: String,
    pub bytes: String,
}

#[derive(Clone, Debug, Deserialize, serde::Serialize, Eq, PartialEq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FunctionAlias {
    pub function_path: String,
    pub entry_point: String,
}

pub(crate) fn validate_function_identity(value: &str) -> anyhow::Result<()> {
    anyhow::ensure!(
        value.len() <= 512
            && value.matches(':').count() == 1
            && value.split(':').all(|part| !part.trim().is_empty())
            && !value.starts_with("_system/")
            && !value.chars().any(char::is_control),
        "invalid native function address/entry point"
    );
    Ok(())
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Capsule {
    pub format: String,
    pub version: u32,
    pub module_kind: ModuleKind,
    pub export: Option<String>,
    #[serde(default)]
    pub definition: Option<Value>,
    pub assembly: Artifact,
    pub assembly_dependencies: Vec<Artifact>,
    #[serde(default)]
    pub function_aliases: Vec<FunctionAlias>,
}

impl Capsule {
    pub fn parse(source: &str) -> anyhow::Result<Self> {
        anyhow::ensure!(
            source.len() <= MAX_CAPSULE_BYTES,
            "native capsule exceeds 16MiB"
        );
        let value: Value = serde_json::from_str(source)?;
        anyhow::ensure!(
            value.get("export").is_some(),
            "native capsule requires export (null for functions)"
        );
        let capsule: Self = serde_json::from_value(value)?;
        anyhow::ensure!(
            capsule.version == 1 && capsule.format == "convex-dotnet-capsule",
            "unsupported native capsule format/version"
        );
        anyhow::ensure!(
            capsule.assembly_dependencies.len() <= 64,
            "too many native dependencies"
        );
        anyhow::ensure!(
            capsule.function_aliases.len() <= 1000,
            "too many native aliases"
        );
        anyhow::ensure!(
            matches!(
                capsule.module_kind,
                ModuleKind::Functions | ModuleKind::Http
            ) || capsule.function_aliases.is_empty(),
            "native aliases apply only to function/HTTP capsules"
        );
        let mut addresses = BTreeSet::new();
        for alias in &capsule.function_aliases {
            for value in [&alias.function_path, &alias.entry_point] {
                validate_function_identity(value)?;
            }
            let module = alias.function_path.split_once(':').unwrap().0;
            anyhow::ensure!(
                !module.ends_with(".js"),
                "native alias requires extension-free durable module address"
            );
            anyhow::ensure!(
                addresses.insert(&alias.function_path),
                "duplicate native durable address"
            );
        }
        match capsule.module_kind {
            ModuleKind::Functions => anyhow::ensure!(
                capsule.export.is_none(),
                "function capsule export must be null"
            ),
            _ => anyhow::ensure!(
                capsule
                    .export
                    .as_ref()
                    .is_some_and(|v| v.contains(':') && !v.starts_with("_system/")),
                "native definition requires exact export"
            ),
        }
        anyhow::ensure!(
            capsule.module_kind != ModuleKind::Auth || capsule.definition.is_none(),
            "native auth definition must be null: owning variables determine providers"
        );
        capsule.verified_bytes()?;
        Ok(capsule)
    }

    pub fn entry_point_for<'a>(&'a self, address: &'a str) -> &'a str {
        self.function_aliases
            .iter()
            .find(|alias| alias.function_path == address)
            .map(|alias| alias.entry_point.as_str())
            .unwrap_or(address)
    }

    pub fn address_for_entry_point<'a>(&'a self, entry_point: &'a str) -> anyhow::Result<&'a str> {
        let mut aliases = self
            .function_aliases
            .iter()
            .filter(|alias| alias.entry_point == entry_point);
        let address = aliases
            .next()
            .map(|alias| alias.function_path.as_str())
            .unwrap_or(entry_point);
        anyhow::ensure!(
            aliases.next().is_none(),
            "ambiguous native HTTP handler alias"
        );
        Ok(address)
    }

    pub fn validate_catalog(&self, catalog: &Catalog) -> anyhow::Result<()> {
        for alias in &self.function_aliases {
            anyhow::ensure!(
                catalog
                    .functions
                    .iter()
                    .any(|function| function.function_path == alias.entry_point),
                "native alias entry point is not an exported registration"
            );
        }
        Ok(())
    }

    pub fn check_module_path(&self, path: &str) -> anyhow::Result<()> {
        let expected = match self.module_kind {
            ModuleKind::Functions => {
                anyhow::ensure!(
                    ![
                        "schema.js",
                        "auth.config.js",
                        "http.js",
                        "crons.js",
                        "convex.config.js"
                    ]
                    .contains(&path),
                    "reserved native definition module"
                );
                return Ok(());
            },
            ModuleKind::Schema => "schema.js",
            ModuleKind::Auth => "auth.config.js",
            ModuleKind::Http => "http.js",
            ModuleKind::Crons => "crons.js",
            ModuleKind::Component => "convex.config.js",
        };
        anyhow::ensure!(path == expected, "native definition kind/path mismatch");
        Ok(())
    }

    fn verified_bytes(&self) -> anyhow::Result<Vec<(&Artifact, Vec<u8>)>> {
        let mut names = BTreeSet::new();
        let mut total = 0usize;
        let mut result = Vec::new();
        for artifact in std::iter::once(&self.assembly).chain(&self.assembly_dependencies) {
            anyhow::ensure!(
                artifact.name.ends_with(".dll")
                    && artifact.name.len() <= 200
                    && !artifact.name.contains("..")
                    && artifact
                        .name
                        .bytes()
                        .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'_' | b'-' | b'.')),
                "native artifact must have a simple .dll filename"
            );
            anyhow::ensure!(
                names.insert(&artifact.name),
                "duplicate native artifact filename"
            );
            validate_digest(&artifact.sha256)?;
            anyhow::ensure!(
                artifact.bytes.len() <= ((MAX_ARTIFACT_BYTES + 2) / 3) * 4,
                "encoded native artifact exceeds limit"
            );
            let bytes =
                base64::decode(&artifact.bytes).context("invalid native artifact base64")?;
            anyhow::ensure!(
                bytes.len() <= MAX_ARTIFACT_BYTES,
                "native artifact exceeds 8MiB"
            );
            anyhow::ensure!(
                sha256_hex(&bytes) == artifact.sha256,
                "native capsule artifact digest mismatch"
            );
            total = total
                .checked_add(bytes.len())
                .context("native artifact size overflow")?;
            anyhow::ensure!(
                total <= MAX_TOTAL_ARTIFACT_BYTES,
                "native capsule artifacts exceed 10MiB"
            );
            result.push((artifact, bytes));
        }
        Ok(result)
    }
}

/// Native targets and pooled processes retain this ephemeral directory owner.
/// A path alone is not an extraction lifetime. No lease is read from a capsule
/// or persisted deployment manifest.
#[derive(Clone, Debug)]
pub struct ArtifactLease {
    owner: Arc<ArtifactDirectory>,
}

#[derive(Debug)]
struct ArtifactDirectory {
    // Drop the child before the root, including when the cache owner is gone.
    _directory: tempfile::TempDir,
    _root: Arc<tempfile::TempDir>,
}

#[derive(Clone)]
pub struct LoadedArtifacts {
    pub assembly_path: PathBuf,
    pub assembly_sha256: String,
    pub assembly_dependencies: Vec<AssemblyDependency>,
    pub lease: ArtifactLease,
    byte_size: usize,
}

struct CachedArtifacts {
    value: LoadedArtifacts,
    last_used: Instant,
}

#[derive(Debug)]
pub(crate) struct ArtifactCapacityExceeded;

impl std::fmt::Display for ArtifactCapacityExceeded {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("native artifact cache admission limit exceeded")
    }
}

impl std::error::Error for ArtifactCapacityExceeded {}

pub struct ArtifactCache {
    root: Arc<tempfile::TempDir>,
    entries: Mutex<BTreeMap<String, CachedArtifacts>>,
    maximum_graphs: usize,
    maximum_bytes: usize,
}

impl ArtifactCache {
    #[cfg(test)]
    pub(crate) fn with_limits(maximum_graphs: usize, maximum_bytes: usize) -> anyhow::Result<Self> {
        let mut cache = Self::new()?;
        cache.maximum_graphs = maximum_graphs;
        cache.maximum_bytes = maximum_bytes;
        Ok(cache)
    }

    pub fn new() -> anyhow::Result<Self> {
        Ok(Self {
            root: Arc::new(
                tempfile::Builder::new()
                    .prefix("convex-dotnet-artifacts-")
                    .tempdir()?,
            ),
            entries: Mutex::new(BTreeMap::new()),
            maximum_graphs: MAX_CACHED_GRAPHS,
            maximum_bytes: MAX_CACHED_BYTES,
        })
    }

    pub async fn load(&self, capsule: &Capsule) -> anyhow::Result<LoadedArtifacts> {
        // Validate *all* bytes before writing any artifact. Cache names use only
        // backend hashes; the private root is never supplied by the publisher.
        let bytes = capsule.verified_bytes()?;
        let descriptor: Vec<_> = bytes
            .iter()
            .map(|(artifact, _)| (&artifact.name, &artifact.sha256))
            .collect();
        let key = sha256_hex(&serde_json::to_vec(&descriptor)?);
        let mut entries = self.entries.lock().await;
        if let Some(cached) = entries.get_mut(&key) {
            cached.last_used = Instant::now();
            return Ok(cached.value.clone());
        }
        let byte_size = bytes.iter().map(|(_, data)| data.len()).sum::<usize>();
        // Plan retirement completely before mutating the cache. A live target,
        // catalog callback or pooled process makes a graph ineligible. No later
        // dispatch is authorized by reconstruction from the original capsule.
        let mut retained_graphs = entries.len();
        let mut retained_bytes = entries
            .values()
            .map(|entry| entry.value.byte_size)
            .sum::<usize>();
        let mut idle: Vec<_> = entries
            .iter()
            .filter(|(_, entry)| Arc::strong_count(&entry.value.lease.owner) == 1)
            .map(|(key, entry)| (entry.last_used, key.clone(), entry.value.byte_size))
            .collect();
        idle.sort();
        let mut retire = Vec::new();
        for (_, key, bytes) in idle {
            if retained_graphs < self.maximum_graphs
                && retained_bytes + byte_size <= self.maximum_bytes
            {
                break;
            }
            retained_graphs -= 1;
            retained_bytes -= bytes;
            retire.push(key);
        }
        if retained_graphs >= self.maximum_graphs || retained_bytes + byte_size > self.maximum_bytes
        {
            return Err(ArtifactCapacityExceeded.into());
        }
        for key in retire {
            entries.remove(&key);
        }
        // TempDir cleans a partial extraction if its future is cancelled. The
        // complete private directory is published only in the cache map, with
        // no await between acquiring its lease and recording that ownership.
        let pending = tempfile::Builder::new()
            .prefix("graph-")
            .tempdir_in(self.root.path())?;
        let directory = pending.path().to_owned();
        #[cfg(test)]
        tokio::task::yield_now().await;
        let mut paths = Vec::new();
        for (artifact, data) in bytes {
            let path = pending.path().join(&artifact.name);
            let mut options = tokio::fs::OpenOptions::new();
            options.write(true).create_new(true);
            #[cfg(unix)]
            options.mode(0o600);
            let mut file = options.open(&path).await?;
            use tokio::io::AsyncWriteExt;
            file.write_all(&data).await?;
            file.flush().await?;
            paths.push(AssemblyDependency {
                path: directory.join(&artifact.name),
                sha256: artifact.sha256.clone(),
            });
        }
        let primary = paths.remove(0);
        let loaded = LoadedArtifacts {
            assembly_path: primary.path,
            assembly_sha256: primary.sha256,
            assembly_dependencies: paths,
            lease: ArtifactLease {
                owner: Arc::new(ArtifactDirectory {
                    _directory: pending,
                    _root: self.root.clone(),
                }),
            },
            byte_size,
        };
        entries.insert(
            key,
            CachedArtifacts {
                value: loaded.clone(),
                last_used: Instant::now(),
            },
        );
        Ok(loaded)
    }
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CatalogFunction {
    pub function_path: String,
    pub kind: FunctionKind,
    pub visibility: String,
    pub arguments: Value,
    pub returns: Value,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Definition {
    pub export: String,
    pub value: Value,
}
#[derive(Clone, Debug, Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct HttpRoute {
    pub method: String,
    pub path: String,
    pub function_path: String,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct HttpRouter {
    pub export: String,
    pub routes: Vec<HttpRoute>,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Catalog {
    pub version: u32,
    pub functions: Vec<CatalogFunction>,
    pub schemas: Vec<Definition>,
    pub auth_configs: Vec<Definition>,
    pub http_routers: Vec<HttpRouter>,
    pub crons: Vec<Definition>,
    pub component_definitions: Vec<Definition>,
}

impl Catalog {
    pub fn validate(&self) -> anyhow::Result<()> {
        anyhow::ensure!(self.version == 1, "unsupported native catalog version");
        anyhow::ensure!(
            self.functions.len() <= 1000,
            "native catalog has too many exports"
        );
        let mut functions = BTreeSet::new();
        for function in &self.functions {
            anyhow::ensure!(
                function.function_path.contains(':')
                    && !function.function_path.starts_with("_system/")
                    && functions.insert(&function.function_path),
                "invalid/duplicate native catalog function"
            );
            anyhow::ensure!(
                matches!(function.visibility.as_str(), "public" | "internal"),
                "invalid native visibility"
            );
        }
        for definitions in [
            &self.schemas,
            &self.auth_configs,
            &self.crons,
            &self.component_definitions,
        ] {
            let mut exports = BTreeSet::new();
            for definition in definitions {
                anyhow::ensure!(
                    exports.insert(&definition.export),
                    "duplicate native definition export"
                );
            }
        }
        let mut routers = BTreeSet::new();
        for router in &self.http_routers {
            anyhow::ensure!(
                routers.insert(&router.export),
                "duplicate native router export"
            );
            let mut routes = BTreeSet::new();
            for route in &router.routes {
                anyhow::ensure!(
                    matches!(
                        route.method.as_str(),
                        "DELETE" | "GET" | "OPTIONS" | "PATCH" | "POST" | "PUT"
                    ) && route.path.starts_with('/')
                        && (!route.path.contains('*')
                            || (route.path.ends_with("/*")
                                && route.path.matches('*').count() == 1))
                        && routes.insert((&route.method, &route.path)),
                    "invalid/duplicate native HTTP route"
                );
                anyhow::ensure!(
                    self.functions
                        .iter()
                        .any(|f| f.function_path == route.function_path
                            && f.kind == FunctionKind::HttpAction),
                    "native route must refer to a registered HTTP handler"
                );
            }
        }
        Ok(())
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    fn capsule() -> Value {
        serde_json::json!({"format":"convex-dotnet-capsule","version":1,"moduleKind":"functions","export":null,"assembly":{"name":"Functions.dll","sha256":sha256_hex(b"artifact"),"bytes":base64::encode(b"artifact")},"assemblyDependencies":[]})
    }
    pub(crate) fn graph(bytes: &[u8]) -> Capsule {
        let mut value = capsule();
        value["assembly"]["bytes"] = Value::String(base64::encode(bytes));
        value["assembly"]["sha256"] = Value::String(sha256_hex(bytes));
        Capsule::parse(&value.to_string()).unwrap()
    }
    #[test]
    fn capsule_refuses_paths_modified_artifacts_and_missing_selection() {
        let valid = capsule();
        assert!(Capsule::parse(&valid.to_string()).is_ok());
        let mut path = valid.clone();
        path["assembly"]["name"] = Value::String("../secret.dll".into());
        assert!(Capsule::parse(&path.to_string()).is_err());
        let mut bytes = valid.clone();
        bytes["assembly"]["bytes"] = Value::String(base64::encode(b"modified"));
        assert!(Capsule::parse(&bytes.to_string()).is_err());
        let mut selection = valid;
        selection.as_object_mut().unwrap().remove("export");
        assert!(Capsule::parse(&selection.to_string()).is_err());
    }
    #[test]
    fn aliases_preserve_durable_case_sensitive_addresses_and_refuse_ambiguity() {
        let mut value = capsule();
        value["functionAliases"] = serde_json::json!([
            {"functionPath":"sharedConfiguration:save","entryPoint":"Dbm.SharedTemplates:save"}
        ]);
        let parsed = Capsule::parse(&value.to_string()).unwrap();
        assert_eq!(
            parsed.entry_point_for("sharedConfiguration:save"),
            "Dbm.SharedTemplates:save"
        );
        assert_eq!(
            parsed.entry_point_for("sharedConfiguration:Save"),
            "sharedConfiguration:Save"
        );
        assert_eq!(
            parsed
                .address_for_entry_point("Dbm.SharedTemplates:save")
                .unwrap(),
            "sharedConfiguration:save"
        );
        let duplicate = value["functionAliases"][0].clone();
        value["functionAliases"]
            .as_array_mut()
            .unwrap()
            .push(duplicate);
        assert!(Capsule::parse(&value.to_string()).is_err());
        value["functionAliases"][1]["functionPath"] =
            Value::String("sharedConfiguration:other".into());
        let ambiguous = Capsule::parse(&value.to_string()).unwrap();
        assert!(ambiguous
            .address_for_entry_point("Dbm.SharedTemplates:save")
            .is_err());
        value["functionAliases"][0]["functionPath"] =
            Value::String("sharedConfiguration.js:save".into());
        assert!(Capsule::parse(&value.to_string()).is_err());
    }
    #[tokio::test]
    async fn capsule_cache_is_owned_hash_bound_and_reuses_verified_artifacts() {
        let cache = ArtifactCache::new().unwrap();
        let capsule = Capsule::parse(&capsule().to_string()).unwrap();
        let first = cache.load(&capsule).await.unwrap();
        let second = cache.load(&capsule).await.unwrap();
        assert_eq!(first.assembly_path, second.assembly_path);
        assert_eq!(
            tokio::fs::read(first.assembly_path).await.unwrap(),
            b"artifact"
        );
    }

    #[tokio::test]
    async fn cache_retires_idle_graphs_beyond_lifetime_capacity_and_reconstructs_original() {
        let cache = ArtifactCache::new().unwrap();
        let original = graph(b"original");
        let first = cache.load(&original).await.unwrap();
        let old_path = first.assembly_path.clone();
        drop(first);
        for revision in 0..80 {
            drop(
                cache
                    .load(&graph(format!("revision-{revision}").as_bytes()))
                    .await
                    .unwrap(),
            );
        }
        assert_eq!(cache.entries.lock().await.len(), MAX_CACHED_GRAPHS);
        assert!(!old_path.exists());
        let reconstructed = cache.load(&original).await.unwrap();
        assert_ne!(reconstructed.assembly_path, old_path);
        assert_eq!(
            tokio::fs::read(&reconstructed.assembly_path).await.unwrap(),
            b"original"
        );
        let path = reconstructed.assembly_path.clone();
        let root = cache.root.path().to_owned();
        drop(cache);
        assert!(path.exists(), "a loaded graph outlives its cache owner");
        drop(reconstructed);
        assert!(!root.exists(), "the final directory lease owns cleanup");
    }

    #[tokio::test]
    async fn active_graphs_refuse_capacity_without_retiring_any_existing_owner() {
        let cache = ArtifactCache::new().unwrap();
        let mut held = Vec::new();
        for revision in 0..MAX_CACHED_GRAPHS {
            held.push(
                cache
                    .load(&graph(format!("held-{revision}").as_bytes()))
                    .await
                    .unwrap(),
            );
        }
        let before = cache
            .entries
            .lock()
            .await
            .keys()
            .cloned()
            .collect::<Vec<_>>();
        let failure = cache.load(&graph(b"one-too-many")).await.err().unwrap();
        assert!(failure.is::<ArtifactCapacityExceeded>());
        assert_eq!(
            cache
                .entries
                .lock()
                .await
                .keys()
                .cloned()
                .collect::<Vec<_>>(),
            before
        );
        assert!(held
            .iter()
            .all(|artifacts| artifacts.assembly_path.exists()));
        drop(held.remove(0));
        let accepted = cache.load(&graph(b"one-too-many")).await.unwrap();
        assert!(accepted.assembly_path.exists());
        assert!(held
            .iter()
            .all(|artifacts| artifacts.assembly_path.exists()));
    }

    #[tokio::test]
    async fn byte_capacity_retirement_and_contradiction_are_checked_before_mutation() {
        let cache = ArtifactCache::with_limits(2, 4).unwrap();
        let first = cache.load(&graph(b"aaa")).await.unwrap();
        let before = first.assembly_path.clone();
        assert!(cache
            .load(&graph(b"bbb"))
            .await
            .err()
            .unwrap()
            .is::<ArtifactCapacityExceeded>());
        let mut contradiction = graph(b"bbb");
        contradiction.assembly.bytes = base64::encode(b"ccc");
        assert!(cache
            .load(&contradiction)
            .await
            .err()
            .unwrap()
            .to_string()
            .contains("digest mismatch"));
        assert_eq!(cache.entries.lock().await.len(), 1);
        assert_eq!(tokio::fs::read(&before).await.unwrap(), b"aaa");
        drop(first);
        let second = cache.load(&graph(b"bbb")).await.unwrap();
        assert!(!before.exists());
        assert_eq!(
            tokio::fs::read(&second.assembly_path).await.unwrap(),
            b"bbb"
        );
    }

    #[tokio::test]
    async fn cancelled_extraction_cleans_unpublished_directory() {
        use std::future::Future;
        let cache = ArtifactCache::new().unwrap();
        let capsule = graph(&vec![42; 4 * 1024 * 1024]);
        {
            let pending = cache.load(&capsule);
            tokio::pin!(pending);
            // A test-only yield fixes the cancellation checkpoint after private
            // directory creation, before publication. Every extraction await
            // retains this same TempDir cleanup owner.
            std::future::poll_fn(|context| {
                assert!(pending.as_mut().poll(context).is_pending());
                std::task::Poll::Ready(())
            })
            .await;
            assert_eq!(std::fs::read_dir(cache.root.path()).unwrap().count(), 1);
        }
        assert_eq!(std::fs::read_dir(cache.root.path()).unwrap().count(), 0);
        assert!(cache.entries.lock().await.is_empty());
    }
}
