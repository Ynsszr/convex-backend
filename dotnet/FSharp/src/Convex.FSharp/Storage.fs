namespace Convex.FSharp

/// Invocation-scoped, pull-based binary data. Each nonterminal chunk is
/// nonempty and at most 64 KiB. Cleanup must be awaited by the consumer.
type INativeByteSource =
    abstract Read: unit -> Async<byte[] option>
    abstract Close: unit -> Async<unit>

type INativeStorageDownload =
    inherit INativeByteSource
    abstract ContentType: string option
    abstract ContentLength: int64

type INativeStorageUpload =
    abstract Write: bytes: byte[] -> Async<unit>
    /// Complete once through the owning storage task. An unreadable result
    /// must never be treated as permission to repeat the upload.
    abstract Commit: unit -> Async<string>
    abstract Abort: unit -> Async<unit>

/// Separate native streaming capability; buffered storage contracts remain
/// compatible with previously deployed application code.
type INativeStorageStreaming =
    abstract OpenRead: storageId: string -> Async<INativeStorageDownload option>
    abstract OpenWrite: ?contentType: string * ?contentLength: int64 * ?sha256: string -> Async<INativeStorageUpload>

/// Native counterpart of a Web Blob. Both payload bytes and MIME metadata are
/// preserved by the storage action adapter; it cannot enter transactional code.
type NativeStorageBlob(bytes: byte[], contentType: string option) =
    let payload=Array.copy bytes
    member _.Bytes=Array.copy payload
    member _.ContentType=contentType

/// Metadata for a file stored in Convex storage
type FileMetadata =
    {
        /// SDK legacy metadata identifier, which can differ from the requested modern storage document ID.
        StorageId: string
        Size: int64
        ContentType: string option
        Sha256: string
    }

/// Read-only access to Convex file storage (queries)
type IStorageReader =
    abstract GetUrl: storageId: string -> Async<string option>
    abstract GetMetadata: storageId: string -> Async<FileMetadata option>

/// Transactional read and write access to Convex file storage (mutations)
type IStorageWriter =
    inherit IStorageReader
    abstract GenerateUploadUrl: unit -> Async<string>
    abstract Delete: storageId: string -> Async<unit>

/// Direct blob storage operations available inside Convex actions
type IStorageActionWriter =
    inherit IStorageWriter
    abstract Store: bytes: byte[] * ?contentType: string * ?sha256: string -> Async<string>
    abstract Get: storageId: string -> Async<byte[] option>
    /// Preserve the actual Web Blob for streaming, MIME metadata and zero-copy handoff.
    abstract GetBlob: storageId:string -> Async<obj option>
    abstract StoreBlob: blob:obj * ?sha256:string -> Async<string>
