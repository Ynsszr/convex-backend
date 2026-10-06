//! Invocation-scoped native byte streams. Storage still uses TaskExecutor's
//! upload/download authorization and usage owners; HTTP retains the original
//! response streamers, with payload permits held until their Bytes are dropped.
use std::{
    collections::BTreeMap,
    sync::Arc,
};

use anyhow::Context;
use common::{
    runtime::{
        Runtime,
        SpawnHandle,
    },
    sync::spsc,
};
use dotnet_executor::protocol::HttpHeader;
use errors::ErrorMetadata;
use futures::{
    stream::BoxStream,
    StreamExt,
};
use http::{
    HeaderMap,
    HeaderName,
    StatusCode,
};
use serde::{
    de::DeserializeOwned,
    Deserialize,
};
use serde_json::{
    json,
    Value,
};
use tokio::sync::{
    oneshot,
    OwnedSemaphorePermit,
    Semaphore,
};
use udf::{
    HttpActionResponseHead,
    HttpActionResponsePart,
    HttpActionResponseStreamer,
    HTTP_ACTION_BODY_LIMIT,
};
use value::{
    id_v6::DeveloperDocumentId,
    ConvexValue,
};

use super::task_executor::TaskExecutor;

pub(super) const CHUNK_LIMIT: usize = 64 * 1024;
const QUEUE_CHUNKS: usize = 16;
const STREAM_LIMIT: usize = 32;

fn invalid(message: &'static str) -> anyhow::Error {
    ErrorMetadata::bad_request("InvalidNativeStreamOperation", message).into()
}

fn parse<T: DeserializeOwned>(args: Value) -> anyhow::Result<T> {
    serde_json::from_value(args).map_err(|_| invalid("Invalid native stream arguments"))
}

fn chunk(value: Value) -> anyhow::Result<bytes::Bytes> {
    let ConvexValue::Bytes(bytes) = ConvexValue::try_from(value)
        .map_err(|_| invalid("A stream chunk requires canonical Convex bytes"))?
    else {
        return Err(invalid("A stream chunk requires canonical Convex bytes"));
    };
    anyhow::ensure!(
        bytes.len() <= CHUNK_LIMIT,
        invalid("Native stream chunks cannot exceed 64KiB")
    );
    Ok(bytes::Bytes::from(Vec::<u8>::from(bytes)))
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StreamId {
    stream_id: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StreamChunk {
    stream_id: String,
    bytes: Value,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenRead {
    storage_id: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenWrite {
    content_type: Option<String>,
    content_length: Option<String>,
    sha256: Option<String>,
}

struct NativeRead {
    stream: BoxStream<'static, anyhow::Result<bytes::Bytes>>,
    pending: bytes::Bytes,
    done: bool,
}

impl NativeRead {
    async fn read(&mut self) -> anyhow::Result<Value> {
        while self.pending.is_empty() && !self.done {
            match self.stream.next().await.transpose()? {
                Some(bytes) => self.pending = bytes,
                None => self.done = true,
            }
        }
        let bytes = self.pending.split_to(self.pending.len().min(CHUNK_LIMIT));
        let bytes: Value = ConvexValue::Bytes(bytes.to_vec().try_into()?).into();
        Ok(json!({"bytes":bytes,"done":self.done}))
    }
}

struct NativeWrite {
    sender: spsc::Sender<anyhow::Result<bytes::Bytes>>,
    result: oneshot::Receiver<anyhow::Result<DeveloperDocumentId>>,
    task: Option<Box<dyn SpawnHandle>>,
}

impl Drop for NativeWrite {
    fn drop(&mut self) {
        // Cancel before closing the producer: dropping an uncommitted handle
        // must not signal a successful end-of-file to its background upload.
        if let Some(task) = &mut self.task {
            task.shutdown();
        }
    }
}

impl NativeWrite {
    async fn commit(mut self) -> anyhow::Result<DeveloperDocumentId> {
        self.sender.close();
        let result = (&mut self.result)
            .await
            .context("native upload owner stopped without a result")?;
        if let Some(task) = self.task.take() {
            task.join().await?;
        }
        result
    }

    async fn abort(mut self) -> anyhow::Result<()> {
        if let Some(task) = self.task.take() {
            task.shutdown_and_join().await?;
        }
        self.sender.close();
        Ok(())
    }
}

enum NativeStorageStream {
    Read(NativeRead),
    Write(NativeWrite),
}

#[derive(Default)]
pub(super) struct NativeStorageStreams {
    streams: BTreeMap<String, NativeStorageStream>,
}

impl NativeStorageStreams {
    pub(super) async fn syscall<RT: Runtime>(
        &mut self,
        tasks: &TaskExecutor<RT>,
        name: &str,
        args: Value,
    ) -> anyhow::Result<Value> {
        match name {
            "dotnet/storageOpenRead" => {
                let args: OpenRead = parse(args)?;
                self.admit()?;
                let Some((stream, metadata)) = tasks
                    .run_storage_get_inner(args.storage_id, uuid::Uuid::nil())
                    .await?
                else {
                    return Ok(Value::Null);
                };
                let stream_id = uuid::Uuid::new_v4().to_string();
                self.streams.insert(
                    stream_id.clone(),
                    NativeStorageStream::Read(NativeRead {
                        stream,
                        pending: bytes::Bytes::new(),
                        done: false,
                    }),
                );
                Ok(
                    json!({"streamId":stream_id,"contentType":metadata.content_type,"contentLength":metadata.content_length.to_string()}),
                )
            },
            "dotnet/storageRead" => {
                let args: StreamId = parse(args)?;
                let Some(NativeStorageStream::Read(stream)) = self.streams.get_mut(&args.stream_id)
                else {
                    return Err(invalid("Unknown or non-readable invocation stream"));
                };
                stream.read().await
            },
            "dotnet/storageClose" => {
                let args: StreamId = parse(args)?;
                anyhow::ensure!(
                    matches!(
                        self.streams.get(&args.stream_id),
                        Some(NativeStorageStream::Read(_))
                    ),
                    invalid("Unknown or non-readable invocation stream")
                );
                self.streams.remove(&args.stream_id);
                Ok(Value::Null)
            },
            "dotnet/storageOpenWrite" => {
                let args: OpenWrite = parse(args)?;
                self.admit()?;
                let writing = self
                    .streams
                    .values()
                    .filter(|stream| matches!(stream, NativeStorageStream::Write(_)))
                    .count();
                anyhow::ensure!(
                    writing < *common::knobs::MAX_CONCURRENT_ACTION_OPS,
                    ErrorMetadata::bad_request(
                        "NativeStreamLimit",
                        "Native uploads exceed the owning action operation limit"
                    )
                );
                if let Some(length) = &args.content_length {
                    anyhow::ensure!(
                        !length.is_empty()
                            && length.len() <= 20
                            && length.bytes().all(|b| b.is_ascii_digit())
                            && length.parse::<u64>().is_ok(),
                        invalid("contentLength must be a bounded decimal string")
                    );
                }
                let (sender, receiver) = spsc::channel(QUEUE_CHUNKS);
                let (result_sender, result) = oneshot::channel();
                let owner = tasks.clone();
                let task = tasks.rt.spawn("native_storage_stream", async move {
                    let stream = futures::stream::unfold(receiver, |mut receiver| async {
                        receiver.recv().await.map(|bytes| (bytes, receiver))
                    })
                    .boxed();
                    let result = owner
                        .run_storage_store_stream(
                            stream,
                            args.content_type,
                            args.content_length,
                            args.sha256.map(|sha256| format!("sha-256={sha256}")),
                        )
                        .await;
                    let _ = result_sender.send(result);
                });
                let stream_id = uuid::Uuid::new_v4().to_string();
                self.streams.insert(
                    stream_id.clone(),
                    NativeStorageStream::Write(NativeWrite {
                        sender,
                        result,
                        task: Some(task),
                    }),
                );
                Ok(json!({"streamId":stream_id}))
            },
            "dotnet/storageWrite" => {
                let args: StreamChunk = parse(args)?;
                let bytes = chunk(args.bytes)?;
                let Some(NativeStorageStream::Write(stream)) =
                    self.streams.get_mut(&args.stream_id)
                else {
                    return Err(invalid("Unknown or non-writable invocation stream"));
                };
                if stream.sender.send(Ok(bytes)).await.is_err() {
                    let Some(NativeStorageStream::Write(stream)) =
                        self.streams.remove(&args.stream_id)
                    else {
                        unreachable!()
                    };
                    // Preserve the original upload owner's error (for example
                    // a refused content type), rather than inventing a retry.
                    stream.commit().await?;
                    return Err(invalid("Native upload closed before commit"));
                }
                Ok(Value::Null)
            },
            "dotnet/storageCommit" => {
                let args: StreamId = parse(args)?;
                anyhow::ensure!(
                    matches!(
                        self.streams.get(&args.stream_id),
                        Some(NativeStorageStream::Write(_))
                    ),
                    invalid("Unknown or non-writable invocation stream")
                );
                let Some(NativeStorageStream::Write(stream)) = self.streams.remove(&args.stream_id)
                else {
                    unreachable!()
                };
                Ok(json!(stream.commit().await?.to_string()))
            },
            "dotnet/storageAbort" => {
                let args: StreamId = parse(args)?;
                anyhow::ensure!(
                    matches!(
                        self.streams.get(&args.stream_id),
                        Some(NativeStorageStream::Write(_))
                    ),
                    invalid("Unknown or non-writable invocation stream")
                );
                let Some(NativeStorageStream::Write(stream)) = self.streams.remove(&args.stream_id)
                else {
                    unreachable!()
                };
                stream.abort().await?;
                Ok(Value::Null)
            },
            _ => Err(invalid("Unknown native storage stream operation")),
        }
    }

    fn admit(&self) -> anyhow::Result<()> {
        anyhow::ensure!(
            self.streams.len() < STREAM_LIMIT,
            ErrorMetadata::bad_request(
                "NativeStreamLimit",
                "Native invocation permits at most32 active storage streams"
            )
        );
        Ok(())
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ResponseHead {
    status: u16,
    headers: Vec<HttpHeader>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ResponseChunk {
    bytes: Value,
}

pub(super) fn validate_http_head(
    status: u16,
    raw_headers: Vec<HttpHeader>,
) -> anyhow::Result<HttpActionResponseHead> {
    anyhow::ensure!(
        (200..=599).contains(&status),
        invalid("Invalid native HTTP response status")
    );
    let mut headers = HeaderMap::new();
    for header in raw_headers {
        let name = HeaderName::from_bytes(header.name.as_bytes())?;
        let value = crate::http::byte_string_to_header(&header.name, &header.value)?;
        headers.append(name, value);
    }
    Ok(HttpActionResponseHead {
        status: StatusCode::from_u16(status)?,
        headers,
    })
}

struct PermittedHttpChunk {
    bytes: bytes::Bytes,
    _permit: OwnedSemaphorePermit,
}
impl AsRef<[u8]> for PermittedHttpChunk {
    fn as_ref(&self) -> &[u8] {
        self.bytes.as_ref()
    }
}

pub(super) struct NativeHttpResponseStream {
    pub(super) streamer: HttpActionResponseStreamer,
    is_head: bool,
    permits: Arc<Semaphore>,
}

impl NativeHttpResponseStream {
    pub(super) fn new(streamer: HttpActionResponseStreamer, is_head: bool) -> Self {
        Self {
            streamer,
            is_head,
            permits: Arc::new(Semaphore::new(QUEUE_CHUNKS)),
        }
    }

    pub(super) async fn syscall(&mut self, name: &str, args: Value) -> anyhow::Result<Value> {
        match name {
            "dotnet/httpResponseHead" => {
                let args: ResponseHead = parse(args)?;
                anyhow::ensure!(
                    !self.streamer.has_started(),
                    invalid("Native HTTP response head can be sent once")
                );
                let head = validate_http_head(args.status, args.headers)?;
                self.streamer
                    .send_part(HttpActionResponsePart::Head(head))??;
            },
            "dotnet/httpResponseChunk" => {
                let args: ResponseChunk = parse(args)?;
                let bytes = chunk(args.bytes)?;
                let head = self
                    .streamer
                    .head()
                    .ok_or_else(|| invalid("Native HTTP response body requires its head"))?;
                anyhow::ensure!(
                    !matches!(head.status.as_u16(), 204 | 205 | 304) || bytes.is_empty(),
                    invalid("Native HTTP response status cannot carry a body")
                );
                if self.is_head || bytes.is_empty() {
                    return Ok(Value::Null);
                }
                anyhow::ensure!(
                    self.streamer.total_bytes_sent().saturating_add(bytes.len())
                        <= HTTP_ACTION_BODY_LIMIT,
                    ErrorMetadata::bad_request(
                        "HttpResponseTooLarge",
                        "HTTP actions support responses up to the owning20MiB limit"
                    )
                );
                let permit = tokio::select! {
                    permit = self.permits.clone().acquire_owned() => permit?,
                    _ = self.streamer.sender.closed() => {
                        return Err(ErrorMetadata::client_disconnect().into());
                    },
                };
                // Existing forwarding streamers retain this Bytes owner. The
                // permit releases only after the transport consumes/drops it,
                // bounding native queued response payloads to16*64KiB.
                let bytes = bytes::Bytes::from_owner(PermittedHttpChunk {
                    bytes,
                    _permit: permit,
                });
                self.streamer
                    .send_part(HttpActionResponsePart::BodyChunk(bytes))??;
            },
            _ => return Err(invalid("Unknown native HTTP response operation")),
        }
        Ok(Value::Null)
    }
}
