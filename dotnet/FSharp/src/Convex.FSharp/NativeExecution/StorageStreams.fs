namespace Convex.FSharp.NativeExecution

open System
open System.Globalization
open System.Threading
open Convex.FSharp

/// Handles are scoped by the original action task executor. This adapter never
/// opens a storage URL, retries an upload, or invents completion after a failure.
module internal NativeStorageStreams =
    open HostArguments

    type private UploadState = Open | Uncertain | Committing | Committed | Aborted

    let private handle value =
        let result=field "streamId" value |> Codec.decode<string>
        if String.IsNullOrEmpty result || result.Length>256 then invalidOp "Invalid owning storage stream handle"
        result

    let private length value =
        let text=field "contentLength" value |> Codec.decode<string>
        let mutable result=0L
        if String.IsNullOrEmpty text || text.Length>19 || text |> Seq.exists(fun c -> c<'0' || c>'9') ||
           not(Int64.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,&result)) then
            invalidOp "Storage content length requires an exact nonnegative int64"
        result

    let private serialized (gate:SemaphoreSlim) operation = async {
        // No two reads/writes can interleave through a single opaque handle.
        if not(gate.Wait 0) then invalidOp "A storage stream operation is already pending"
        try return! operation ()
        finally gate.Release() |> ignore
    }

    type private Download(host:ITransactionHostOperations,identity:string,contentType:string option,contentLength:int64) =
        let gate=new SemaphoreSlim(1,1)
        let mutable closed=false
        let mutable ended=false
        let mutable uncertain=false
        let mutable read=0L
        interface INativeStorageDownload with
            member _.ContentType=contentType
            member _.ContentLength=contentLength
            member _.Read()=serialized gate (fun () -> async {
                if closed || uncertain then invalidOp "The storage download is closed or uncertain"
                if ended then return None else
                uncertain<-true
                let! result=host.Call("dotnet/storageRead",objectValue ["streamId",box identity],true)
                let bytes=field "bytes" result |> Codec.decode<byte[]>
                let doneReading=field "done" result |> Codec.decode<bool>
                if isNull bytes || bytes.Length>65536 || (bytes.Length=0 && not doneReading) then
                    invalidOp "Invalid owning storage download chunk"
                read<-read+int64 bytes.Length
                if read>contentLength || (doneReading && read<>contentLength) then
                    invalidOp "Storage download bytes differ from owning metadata"
                ended<-doneReading
                uncertain<-false
                return if bytes.Length=0 then None else Some(Array.copy bytes)
            })
            member _.Close()=serialized gate (fun () -> async {
                if not closed then
                    // The marker precedes the call. A lost close reply does
                    // not permit replay through an uncertain handle.
                    closed<-true
                    let! _=host.Call("dotnet/storageClose",objectValue ["streamId",box identity],true)
                    ()
            })

    type private Upload(host:ITransactionHostOperations,identity:string,contentLength:int64 option) =
        let gate=new SemaphoreSlim(1,1)
        let mutable state=Open
        let mutable written=0L
        interface INativeStorageUpload with
            member _.Write bytes=serialized gate (fun () -> async {
                if state<>Open then invalidOp "The storage upload is no longer writable"
                if isNull bytes || bytes.Length=0 || bytes.Length>65536 then
                    invalidArg "bytes" "A storage upload chunk must contain between 1 and 65536 bytes"
                let next=written+int64 bytes.Length
                if next<written || contentLength |> Option.exists(fun limit -> next>limit) then
                    invalidArg "bytes" "Storage upload exceeds its declared content length"
                // Any failed write fences this upload. No chunk is redelivered.
                state<-Uncertain
                let! _=host.Call("dotnet/storageWrite",objectValue ["streamId",box identity;"bytes",box (Array.copy bytes)],true)
                written<-next
                state<-Open
            })
            member _.Commit()=serialized gate (fun () -> async {
                if state<>Open then invalidOp "The storage upload cannot be committed again"
                if contentLength |> Option.exists(fun limit -> limit<>written) then
                    invalidOp "Storage upload is shorter than its declared content length"
                state<-Committing
                let! result=host.Call("dotnet/storageCommit",objectValue ["streamId",box identity],true)
                let id=Codec.decode<string> result
                if String.IsNullOrEmpty id then invalidOp "The owner did not confirm a storage identifier"
                state<-Committed
                return id
            })
            member _.Abort()=serialized gate (fun () -> async {
                if state<>Committed && state<>Aborted then
                    state<-Aborted
                    let! _=host.Call("dotnet/storageAbort",objectValue ["streamId",box identity],true)
                    ()
            })

    let create (host:ITransactionHostOperations) : INativeStorageStreaming =
        { new INativeStorageStreaming with
            member _.OpenRead id=async {
                let! result=host.Call("dotnet/storageOpenRead",objectValue ["storageId",box id],true)
                if isNull result then return None else
                return Some(Download(host,handle result,optionalText "contentType" result,length result) :> INativeStorageDownload)
            }
            member _.OpenWrite(?contentType,?contentLength,?sha256)=async {
                if contentLength |> Option.exists(fun value -> value<0L) then invalidArg "contentLength" "Storage content length must be nonnegative"
                match sha256 with
                | Some digest ->
                    let bytes=Convert.FromBase64String digest
                    if bytes.Length<>32 || Convert.ToBase64String bytes<>digest then invalidArg "sha256" "An exact base64 SHA-256 digest is required"
                | None -> ()
                let! result=host.Call("dotnet/storageOpenWrite",objectValue [
                    "contentType",contentType |> Option.map box |> Option.defaultValue null
                    "contentLength",contentLength |> Option.map(fun value -> box (value.ToString CultureInfo.InvariantCulture)) |> Option.defaultValue null
                    "sha256",sha256 |> Option.map box |> Option.defaultValue null],true)
                return Upload(host,handle result,contentLength) :> INativeStorageUpload
            } }

    let capability (storage:IStorageActionWriter) =
        match box storage with
        | :? INativeStorageStreaming as streaming -> streaming
        | _ -> invalidOp "This storage adapter has no native streaming capability"

    let store (storage:INativeStorageStreaming) contentType contentLength sha256 (source:INativeByteSource) = async {
        return! AsyncResource.usingAsync (fun () -> source) (fun source -> source.Close()) (fun source -> async {
            let! upload=storage.OpenWrite(?contentType=contentType,?contentLength=contentLength,?sha256=sha256)
            return! AsyncResource.usingAsync (fun () -> upload) (fun upload -> upload.Abort()) (fun upload -> async {
                let mutable ended=false
                while not ended do
                    let! part=source.Read()
                    match part with
                    | None -> ended<-true
                    | Some bytes -> do! upload.Write bytes
                return! upload.Commit()
            })
        })
    }
