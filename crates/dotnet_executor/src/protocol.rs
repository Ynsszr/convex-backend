use std::path::PathBuf;

use anyhow::Context;
use serde::{
    Deserialize,
    Serialize,
};
use serde_json::Value;
use tokio::io::{
    AsyncRead,
    AsyncReadExt,
    AsyncWrite,
    AsyncWriteExt,
};

pub const PROTOCOL_VERSION: u32 = 1;
pub const MAX_FRAME_BYTES: usize = 16 * 1024 * 1024;

#[derive(Clone, Debug, Deserialize, Eq, Ord, PartialEq, PartialOrd, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AssemblyDependency {
    pub path: PathBuf,
    pub sha256: String,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, Ord, PartialEq, PartialOrd, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum FunctionKind {
    Query,
    Mutation,
    Action,
    HttpAction,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct HttpHeader {
    pub name: String,
    pub value: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HttpRequestHead {
    pub method: String,
    pub url: String,
    pub headers: Vec<HttpHeader>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FunctionContract {
    pub visibility: String,
    pub arguments: Value,
    pub returns: Value,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct WorkerError {
    pub code: String,
    pub message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub data: Option<Value>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Invoke {
    #[serde(rename = "type")]
    pub message_type: &'static str,
    pub version: u32,
    pub invocation_id: String,
    pub assembly_path: String,
    pub assembly_sha256: String,
    pub assembly_dependencies: Vec<AssemblyDependency>,
    pub function_path: String,
    pub entry_point: Option<String>,
    pub kind: FunctionKind,
    pub function_contract: FunctionContract,
    pub args: Value,
    pub http_request: Option<HttpRequestHead>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub preparation_protocol_version: Option<u32>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Describe {
    #[serde(rename = "type")]
    pub message_type: &'static str,
    pub version: u32,
    pub invocation_id: String,
    pub assembly_path: String,
    pub assembly_sha256: String,
    pub assembly_dependencies: Vec<AssemblyDependency>,
    pub time_ms: f64,
    pub module_kind: Option<crate::capsule::ModuleKind>,
    pub export: Option<String>,
}

/// Owner-selected pure child argument initialization during deployment
/// typechecking.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InitializeComponent {
    #[serde(rename = "type")]
    pub message_type: &'static str,
    pub version: u32,
    pub invocation_id: String,
    pub assembly_path: String,
    pub assembly_sha256: String,
    pub assembly_dependencies: Vec<AssemblyDependency>,
    pub export: String,
    pub definition_path: String,
    pub component_name: String,
    pub component_definition: Value,
    pub args: Value,
}

#[derive(Debug, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase", deny_unknown_fields)]
pub enum WorkerMessage {
    /// Exactly one trusted admission barrier for an explicitly opted-in invoke.
    #[serde(rename_all = "camelCase")]
    Prepared { version: u32, invocation_id: String },
    #[serde(rename_all = "camelCase")]
    Syscall {
        version: u32,
        invocation_id: String,
        request_id: u64,
        name: String,
        args: Value,
        #[serde(rename = "async")]
        is_async: bool,
    },
    #[serde(rename_all = "camelCase")]
    Result {
        version: u32,
        invocation_id: String,
        value: Value,
    },
    #[serde(rename_all = "camelCase")]
    Error {
        version: u32,
        invocation_id: String,
        error: WorkerError,
    },
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SyscallResult {
    #[serde(rename = "type")]
    pub message_type: &'static str,
    pub version: u32,
    pub invocation_id: String,
    pub request_id: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub value: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<WorkerError>,
}

pub async fn read_frame(reader: &mut (impl AsyncRead + Unpin)) -> anyhow::Result<Value> {
    let length = reader
        .read_u32_le()
        .await
        .context("reading worker frame header")? as usize;
    anyhow::ensure!(
        length > 0 && length <= MAX_FRAME_BYTES,
        "invalid worker frame length {length}"
    );
    let mut bytes = vec![0; length];
    reader
        .read_exact(&mut bytes)
        .await
        .context("reading worker frame payload")?;
    serde_json::from_slice(&bytes).context("invalid UTF-8 JSON worker frame")
}

pub async fn write_frame(
    writer: &mut (impl AsyncWrite + Unpin),
    message: &impl Serialize,
) -> anyhow::Result<()> {
    let bytes = serde_json::to_vec(message)?;
    anyhow::ensure!(bytes.len() <= MAX_FRAME_BYTES, "worker frame exceeds limit");
    writer.write_u32_le(bytes.len().try_into()?).await?;
    writer.write_all(&bytes).await?;
    writer.flush().await?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn canonical_values_survive_framing() -> anyhow::Result<()> {
        let input = serde_json::json!({
            "integer": {"$integer":"/////////38="},
            "float": {"$float":"AAAAAAAA8H8="},
            "bytes": {"$bytes":"AAH/"},
            "null": null,
            "unicode":"İstanbul 🧠"
        });
        let mut bytes = Vec::new();
        write_frame(&mut bytes, &input).await?;
        assert_eq!(read_frame(&mut bytes.as_slice()).await?, input);
        Ok(())
    }

    #[tokio::test]
    async fn oversize_and_truncated_frames_fail() {
        let bytes = ((MAX_FRAME_BYTES + 1) as u32).to_le_bytes();
        assert!(read_frame(&mut bytes.as_slice()).await.is_err());
        let truncated = [4, 0, 0, 0, b'{'];
        assert!(read_frame(&mut truncated.as_slice()).await.is_err());
        let empty = [0, 0, 0, 0];
        assert!(read_frame(&mut empty.as_slice()).await.is_err());
    }

    #[test]
    fn worker_cannot_inject_namespace_or_identity() {
        let value = serde_json::json!({"type":"syscall","version":1,"invocationId":"1",
            "requestId":1,"name":"1.0/get","args":{},"async":true,"identity":"admin"});
        assert!(serde_json::from_value::<WorkerMessage>(value).is_err());
    }
    #[test]
    fn prepared_frame_has_only_exact_scope_fields() {
        let value = serde_json::json!({"type":"prepared","version":1,"invocationId":"exact"});
        assert!(matches!(
            serde_json::from_value::<WorkerMessage>(value.clone()).unwrap(),
            WorkerMessage::Prepared { .. }
        ));
        for name in [
            "preparationProtocolVersion",
            "value",
            "requestId",
            "identity",
        ] {
            let mut forged = value.clone();
            forged[name] = serde_json::json!(1);
            assert!(serde_json::from_value::<WorkerMessage>(forged).is_err());
        }
    }
}
