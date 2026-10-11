namespace Convex.DotNet.Execution

open System
open System.IO
open System.Text.Json
open System.Threading
open Convex.Runtime

/// Framing belongs to the execution adapter, not the application value codec.
/// Syscall envelopes can contain the SDK's $undefined patch/range marker.
module InvocationProtocol =
    let maximumFrame = 16 * 1024 * 1024

    let encode (value: obj) =
        let payload = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType())
        if payload.Length > maximumFrame then invalidArg "value" "Runtime frame exceeds 16 MiB"
        payload

    let read (input: Stream) = task {
        let prefix=Array.zeroCreate<byte> 4
        let! first=input.ReadAsync(prefix.AsMemory(0,1))
        if first=0 then return None else
        do! input.ReadExactlyAsync(prefix.AsMemory(1,3))
        let length=Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(prefix)
        if length=0u || length>uint32 maximumFrame then invalidOp "Invalid runtime frame length"
        let payload=Array.zeroCreate<byte> (int length)
        do! input.ReadExactlyAsync(payload.AsMemory())
        use document=JsonDocument.Parse(ReadOnlyMemory<byte>(payload),JsonDocumentOptions(MaxDepth=128))
        if document.RootElement.ValueKind<>JsonValueKind.Object then invalidOp "A runtime frame must be an object"
        return Some (document.RootElement.Clone())
    }

    let write (output: Stream) value = task {
        let payload=encode value
        let prefix=Array.zeroCreate<byte> 4
        Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(prefix,uint32 payload.Length)
        do! output.WriteAsync(prefix.AsMemory())
        do! output.WriteAsync(payload.AsMemory())
        do! output.FlushAsync()
    }

    let stringField (name: string) (element: JsonElement) =
        let value=element.GetProperty(name)
        if value.ValueKind<>JsonValueKind.String then invalidOp ("Invalid runtime field: "+name)
        value.GetString()

    let checkVersion (element: JsonElement) =
        if element.GetProperty("version").GetInt32()<>1 then invalidOp "Unsupported runtime protocol version"

/// An owning host operation refused the request. Preserve its error identity
/// through native execution instead of replacing it with a worker error code.
type HostOperationException = Convex.Runtime.HostOperationException

/// A channel expires with its invocation. Detached application work cannot use
/// the next invocation's transaction or identity through a retained capability.
type InvocationChannel(input: Stream, output: Stream, invocationId: string) =
    let gate=new SemaphoreSlim(1,1)
    let mutable active=1
    let mutable pending=0
    let mutable sequence=0

    let call name (arguments: JsonElement) asynchronous = async {
        if Volatile.Read(&active)=0 then invalidOp "The invocation capability has expired"
        Interlocked.Increment(&pending) |> ignore
        let mutable acquired=false
        try
            do! gate.WaitAsync() |> Async.AwaitTask
            acquired<-true
            if Volatile.Read(&active)=0 then invalidOp "The invocation capability has expired"
            sequence<-sequence+1
            let request=sequence
            let frame=RuntimeJson.objectValue [
                "type",box "syscall";"version",box 1;"invocationId",box invocationId
                "requestId",box request;"name",box name;"args",box arguments;"async",box asynchronous
            ]
            do! InvocationProtocol.write output frame |> Async.AwaitTask
            let! incoming=InvocationProtocol.read input |> Async.AwaitTask
            let response=incoming |> Option.defaultWith(fun () -> raise (EndOfStreamException("The transaction owner disconnected")))
            InvocationProtocol.checkVersion response
            if InvocationProtocol.stringField "type" response<>"syscallResult" ||
               InvocationProtocol.stringField "invocationId" response<>invocationId ||
               response.GetProperty("requestId").GetInt32()<>request then
                invalidOp "A syscall response did not match its invocation and request"
            let mutable error=Unchecked.defaultof<JsonElement>
            if response.TryGetProperty("error",&error) && error.ValueKind<>JsonValueKind.Null then
                if InvocationProtocol.stringField "code" error="ConvexError" then
                    let data=error.GetProperty("data").Clone()
                    raise (Convex.Runtime.ApplicationException(InvocationProtocol.stringField "message" error,data))
                else raise (HostOperationException(InvocationProtocol.stringField "code" error,InvocationProtocol.stringField "message" error))
            return response.GetProperty("value").Clone()
        finally
            if acquired then gate.Release() |> ignore
            Interlocked.Decrement(&pending) |> ignore
    }

    member _.Close() =
        Volatile.Write(&active,0)
        // Returning while another operation reads the owner's reply would let
        // it consume the next invocation's frame. Stop the worker instead of
        // admitting a terminal result with an outstanding capability use.
        if Volatile.Read(&pending)<>0 then
            invalidOp "The invocation returned with unawaited host operations"
    interface IDisposable with member this.Dispose()=this.Close()
    interface IInvocationHost with
        member _.InvocationId = invocationId
        member _.CallAsync(name,arguments,asynchronous)=call name arguments asynchronous |> Async.StartAsTask
        member _.Call(name,arguments)=call name arguments false |> Async.RunSynchronously
