//! Persisted native source-package contents. Uploaded data carries bytes and
//! hashes, never backend filesystem paths. Convex's module source hash and
//! owning deploy transaction bind the complete capsule.
use std::{
    collections::{
        BTreeMap,
        BTreeSet,
    },
    path::PathBuf,
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

#[derive(Clone)]
pub struct LoadedArtifacts {
    pub assembly_path: PathBuf,
    pub assembly_sha256: String,
    pub assembly_dependencies: Vec<AssemblyDependency>,
    byte_size: usize,
}

pub struct ArtifactCache {
    root: tempfile::TempDir,
    entries: Mutex<BTreeMap<String, LoadedArtifacts>>,
}

impl ArtifactCache {
    pub fn new() -> anyhow::Result<Self> {
        Ok(Self {
            root: tempfile::Builder::new()
                .prefix("convex-dotnet-artifacts-")
                .tempdir()?,
            entries: Mutex::new(BTreeMap::new()),
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
        if let Some(cached) = entries.get(&key) {
            return Ok(cached.clone());
        }
        let byte_size = bytes.iter().map(|(_, data)| data.len()).sum::<usize>();
        anyhow::ensure!(
            entries.len() < 64
                && entries.values().map(|entry| entry.byte_size).sum::<usize>() + byte_size
                    <= 512 * 1024 * 1024,
            "native artifact cache admission limit exceeded"
        );
        let directory = self.root.path().join(&key);
        // TempDir cleans a partial extraction if its future is cancelled. The
        // complete directory is published atomically, with no await between
        // rename and recording its bounded cache ownership.
        let pending = tempfile::Builder::new()
            .prefix("pending-")
            .tempdir_in(self.root.path())?;
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
        std::fs::rename(pending.path(), &directory)?;
        let primary = paths.remove(0);
        let loaded = LoadedArtifacts {
            assembly_path: primary.path,
            assembly_sha256: primary.sha256,
            assembly_dependencies: paths,
            byte_size,
        };
        entries.insert(key, loaded.clone());
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
mod tests {
    use super::*;
    fn capsule() -> Value {
        serde_json::json!({"format":"convex-dotnet-capsule","version":1,"moduleKind":"functions","export":null,"assembly":{"name":"Functions.dll","sha256":sha256_hex(b"artifact"),"bytes":base64::encode(b"artifact")},"assemblyDependencies":[]})
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
}
