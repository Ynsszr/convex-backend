//! A finite, maintenance-only admission worker transport. The sibling owner
//! creates the unchanged restricted worker sandbox outside the clone's sandbox.
//! It owns actual descendant RSS, process/thread bounds and reaping. Normal
//! execution never selects this transport, and no application Invoke is
//! admitted.
#![cfg(target_os = "linux")]

use std::{
    os::unix::fs::{
        FileTypeExt,
        PermissionsExt,
    },
    path::{
        Path,
        PathBuf,
    },
    sync::OnceLock,
    time::Duration,
};

use anyhow::Context;
use serde::Deserialize;
use serde_json::json;
use tokio::net::{
    unix::{
        OwnedReadHalf,
        OwnedWriteHalf,
    },
    UnixStream,
};

use crate::{
    manifest::{
        NativeFunction,
        WorkerConfig,
        WorkerProfile,
    },
    protocol::{
        self,
        FunctionKind,
        PROTOCOL_VERSION,
    },
};

struct Config {
    socket: PathBuf,
    token: String,
}

// Configuration is supplied only by the explicit local-backend maintenance
// command before opening persistence. It is not a manifest or environment
// field.
static CONFIG: OnceLock<Config> = OnceLock::new();

fn hex(value: &str, length: usize) -> bool {
    value.len() == length
        && value
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

fn private_path(path: &Path) -> anyhow::Result<std::fs::Metadata> {
    anyhow::ensure!(
        path.is_absolute(),
        "maintenance broker path must be absolute"
    );
    for ancestor in path.ancestors() {
        let metadata = std::fs::symlink_metadata(ancestor)?;
        anyhow::ensure!(
            !metadata.file_type().is_symlink(),
            "maintenance broker paths cannot traverse links"
        );
    }
    let metadata = std::fs::symlink_metadata(path)?;
    anyhow::ensure!(
        metadata.permissions().mode() & 0o077 == 0,
        "maintenance broker endpoint must be private"
    );
    Ok(metadata)
}

pub(crate) fn configure(
    suspended: bool,
    loopback: bool,
    socket: Option<&Path>,
    token_file: Option<&Path>,
) -> anyhow::Result<()> {
    match (socket, token_file) {
        (None, None) => {
            anyhow::ensure!(
                CONFIG.get().is_none(),
                "maintenance broker cannot be cleared in a running process"
            );
            Ok(())
        },
        (Some(socket), Some(token_file)) => {
            anyhow::ensure!(
                suspended && loopback,
                "native admission broker requires loopback-only suspended maintenance"
            );
            anyhow::ensure!(
                std::env::var_os("CONVEX_DOTNET_MANIFEST").is_some(),
                "native admission broker requires an explicit native manifest"
            );
            anyhow::ensure!(
                socket.as_os_str().as_encoded_bytes().len() < 108,
                "maintenance broker socket path exceeds its bound"
            );
            anyhow::ensure!(
                private_path(socket)?.file_type().is_socket(),
                "maintenance broker endpoint is not a Unix socket"
            );
            let metadata = private_path(token_file)?;
            anyhow::ensure!(
                metadata.is_file() && metadata.len() == 64,
                "maintenance broker token must be a bounded private regular file"
            );
            let token = std::fs::read_to_string(token_file)?;
            anyhow::ensure!(
                hex(&token, 64),
                "maintenance broker token has an invalid shape"
            );
            if let Some(current) = CONFIG.get() {
                anyhow::ensure!(
                    current.socket == socket && current.token == token,
                    "maintenance broker selection changed"
                );
                return Ok(());
            }
            CONFIG
                .set(Config {
                    socket: socket.to_owned(),
                    token,
                })
                .map_err(|_| anyhow::anyhow!("maintenance broker was already configured"))
        },
        _ => anyhow::bail!("maintenance broker socket and token file must be selected together"),
    }
}

pub(crate) fn selected() -> bool {
    CONFIG.get().is_some()
}

#[derive(Deserialize)]
#[serde(tag = "status", rename_all = "camelCase", deny_unknown_fields)]
enum Reply {
    Started {
        version: u32,
        lease: String,
    },
    #[serde(rename_all = "camelCase")]
    Observed {
        version: u32,
        lease: String,
        alive: bool,
        resident_bytes: u64,
    },
    #[serde(rename_all = "camelCase")]
    Retired {
        version: u32,
        lease: String,
        alive: bool,
        resident_bytes: u64,
    },
    Refused {
        version: u32,
        code: String,
    },
}

async fn reply(stream: &mut UnixStream) -> anyhow::Result<Reply> {
    let raw = protocol::read_frame(stream).await?;
    let response: Reply =
        serde_json::from_value(raw).context("invalid maintenance worker broker reply")?;
    if let Reply::Refused { version, code } = &response {
        anyhow::ensure!(
            *version == PROTOCOL_VERSION,
            "unsupported maintenance broker protocol"
        );
        anyhow::ensure!(
            !code.is_empty()
                && code.len() <= 80
                && code
                    .bytes()
                    .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_'),
            "invalid maintenance broker refusal"
        );
        anyhow::bail!("maintenance worker broker refused: {code}");
    }
    Ok(response)
}

pub(crate) struct Lease {
    config: &'static Config,
    id: String,
}

pub(crate) struct Started {
    pub lease: Lease,
    pub read: OwnedReadHalf,
    pub write: OwnedWriteHalf,
}

impl Lease {
    pub(crate) async fn spawn(
        config: &WorkerConfig,
        target: &NativeFunction,
    ) -> anyhow::Result<Started> {
        let broker = CONFIG
            .get()
            .context("maintenance worker broker was not configured")?;
        anyhow::ensure!(
            matches!(config.profile, WorkerProfile::RestrictedFirstParty),
            "maintenance broker requires restricted native admission"
        );
        anyhow::ensure!(
            target.deployment == "native-admission"
                && target.component_path.is_empty()
                && target.function_path == "admission:describe"
                && target.entry_point.is_none()
                && target.kind == FunctionKind::Query
                && target.http_route.is_none(),
            "maintenance broker admits root module descriptions only"
        );
        let operation = async {
            let mut stream = UnixStream::connect(&broker.socket)
                .await
                .context("connecting maintenance worker broker")?;
            protocol::write_frame(
                &mut stream,
                &json!({
                    "version": PROTOCOL_VERSION,
                    "operation": "launch",
                    "token": broker.token,
                    "memoryMiB": config.memory_mi_b,
                    "invocationTimeoutMs": config.invocation_timeout_ms,
                    "target": {
                        "deployment": target.deployment,
                        "componentPath": target.component_path,
                        "functionPath": target.function_path,
                        "kind": target.kind,
                        "assemblyPath": target.assembly_path,
                        "assemblySha256": target.assembly_sha256,
                        "assemblyDependencies": target.assembly_dependencies,
                    },
                }),
            )
            .await?;
            let id = match reply(&mut stream).await? {
                Reply::Started { version, lease }
                    if version == PROTOCOL_VERSION && hex(&lease, 32) =>
                {
                    lease
                },
                _ => anyhow::bail!("maintenance broker did not confirm a worker lease"),
            };
            let (read, write) = stream.into_split();
            Ok(Started {
                lease: Self { config: broker, id },
                read,
                write,
            })
        };
        tokio::time::timeout(Duration::from_secs(10), operation)
            .await
            .context("maintenance worker launch deadline exceeded")?
    }

    async fn control(&self, operation: &str) -> anyhow::Result<Reply> {
        let mut stream = UnixStream::connect(&self.config.socket)
            .await
            .context("connecting maintenance worker control")?;
        protocol::write_frame(
            &mut stream,
            &json!({
                "version": PROTOCOL_VERSION,
                "operation": operation,
                "token": self.config.token,
                "lease": self.id,
            }),
        )
        .await?;
        reply(&mut stream).await
    }

    pub(crate) async fn resident_memory(&self) -> anyhow::Result<u64> {
        let response = tokio::time::timeout(Duration::from_secs(3), self.control("observe"))
            .await
            .context("maintenance worker observation deadline exceeded")??;
        match response {
            Reply::Observed {
                version,
                lease,
                alive: true,
                resident_bytes,
            } if version == PROTOCOL_VERSION && lease == self.id && resident_bytes > 0 => {
                Ok(resident_bytes)
            },
            _ => anyhow::bail!("maintenance worker has no matching live observation"),
        }
    }

    pub(crate) async fn retire(&self) -> anyhow::Result<()> {
        let response = tokio::time::timeout(Duration::from_secs(5), self.control("retire"))
            .await
            .context("maintenance worker retirement deadline exceeded")??;
        match response {
            Reply::Retired {
                version,
                lease,
                alive: false,
                resident_bytes: 0,
            } if version == PROTOCOL_VERSION && lease == self.id => Ok(()),
            _ => anyhow::bail!("maintenance broker did not confirm worker retirement"),
        }
    }
}
