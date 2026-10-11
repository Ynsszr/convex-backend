namespace Convex.DotNet.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open Xunit
open Convex.Runtime
open Convex.DotNet
open Convex.DotNet.Execution

type private BlockedReplies() =
    inherit Stream()
    let started=TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    let completion=TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    member _.Started=started.Task
    member _.Disconnect()=completion.TrySetResult(0) |> ignore
    override _.CanRead=true
    override _.CanWrite=false
    override _.CanSeek=false
    override _.Length=raise(NotSupportedException())
    override _.Position with get()=raise(NotSupportedException()) and set _=raise(NotSupportedException())
    override _.Flush()=()
    override _.Read(_,_,_)=raise(NotSupportedException())
    override _.ReadAsync(_:Memory<byte>,_:CancellationToken)=
        started.TrySetResult() |> ignore
        ValueTask<int>(completion.Task)
    override _.Write(_,_,_)=raise(NotSupportedException())
    override _.Seek(_,_)=raise(NotSupportedException())
    override _.SetLength(_)=raise(NotSupportedException())

module InvocationContracts =
    type ErrorData={quantity:int64;bytes:byte[]}
    let private response identity fields =
        RuntimeJson.objectValue (["type",box "syscallResult";"version",box 1;"invocationId",box identity;"requestId",box 1]@fields)
    let private framed value =
        let memory=new MemoryStream()
        InvocationProtocol.write memory value |> fun operation -> operation.GetAwaiter().GetResult()
        memory.Position<-0L
        memory

    [<Fact>]
    let ``oversize or truncated frames cannot be admitted`` () = task {
        let prefix=Array.zeroCreate<byte> 4
        Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(prefix,uint32 InvocationProtocol.maximumFrame+1u)
        use oversized=new MemoryStream(prefix)
        let! _=Assert.ThrowsAsync<InvalidOperationException>(fun () -> InvocationProtocol.read oversized :> Task)
        use truncated=new MemoryStream([|2uy;0uy;0uy;0uy;123uy|])
        let! _=Assert.ThrowsAsync<EndOfStreamException>(fun () -> InvocationProtocol.read truncated :> Task)
        return ()
    }

    [<Fact>]
    let ``a syscall cannot consume another invocation reply`` () = task {
        use input=framed (response "other" ["value",box true])
        use output=new MemoryStream()
        use channel=new InvocationChannel(input,output,"current")
        let operation=(channel :> IInvocationHost).CallAsync("1.0/getUserIdentity",RuntimeJson.element [],true)
        let! _=Assert.ThrowsAsync<InvalidOperationException>(fun () -> operation :> Task)
        return ()
    }

    [<Fact>]
    let ``closed capabilities refuse before writing another frame`` () = task {
        use input=new MemoryStream()
        use output=new MemoryStream()
        use channel=new InvocationChannel(input,output,"closed")
        channel.Close()
        let operation=(channel :> IInvocationHost).CallAsync("1.0/getUserIdentity",RuntimeJson.element [],true)
        let! _=Assert.ThrowsAsync<InvalidOperationException>(fun () -> operation :> Task)
        Assert.Equal(0L,output.Length)
    }

    [<Fact>]
    let ``returning with an unawaited syscall requires worker retirement`` () = task {
        use input=new BlockedReplies()
        use output=new MemoryStream()
        use channel=new InvocationChannel(input,output,"pending")
        let operation=(channel :> IInvocationHost).CallAsync("1.0/getUserIdentity",RuntimeJson.element [],true)
        do! input.Started.WaitAsync(TimeSpan.FromSeconds 5.)
        Assert.Throws<InvalidOperationException>(fun () -> channel.Close()) |> ignore
        input.Disconnect()
        let! _=Assert.ThrowsAsync<EndOfStreamException>(fun () -> operation :> Task)
        channel.Close()
    }

    [<Fact>]
    let ``nested application errors preserve the exact neutral wire payload`` () = task {
        let expected=RuntimeJson.parse """{"quantity":{"$integer":"AQAAAAAAIAA="},"bytes":{"$bytes":"AP8="}}"""
        let error=RuntimeJson.objectValue ["code",box "ConvexError";"message",box "Refused";"data",box expected]
        use input=framed (response "error" ["value",null;"error",error])
        use output=new MemoryStream()
        use channel=new InvocationChannel(input,output,"error")
        let operation=(channel :> IInvocationHost).CallAsync("1.0/runUdf",RuntimeJson.element [],true)
        let! failure=Assert.ThrowsAsync<Convex.Runtime.ApplicationException>(fun () -> operation :> Task)
        Assert.Equal("Refused",failure.Message)
        Assert.Equal(expected.GetRawText(),failure.EncodedData.GetRawText())
    }
