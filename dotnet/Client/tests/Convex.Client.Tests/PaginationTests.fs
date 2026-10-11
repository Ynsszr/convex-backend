module Convex.Client.Tests.PaginationTests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Net.WebSockets
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Convex.Client
open Convex.FSharp
open Xunit

let private reference () = FunctionReference.publicQuery "messages:page" (Contract.arguments<AdvancedPaginationOptions>()) (Contract.derived<AdvancedPaginationResult<int>>())
let private factory options = Reactive.query (reference()) options
let private page values doneReading cursor status split : AdvancedPaginationResult<int> =
    {Page=values;IsDone=doneReading;ContinueCursor=cursor;PageStatus=status;SplitCursor=split}
let private wire value =
    let encoded=(reference()).Returns.Encode value |> Codec.decode<ConvexValue> |> ConvexJson.toJson
    use json=JsonDocument.Parse encoded
    json.RootElement.Clone()
let private receive (socket:WebSocket) token = task {
    let buffer=Array.zeroCreate<byte> 8192
    use stream=new MemoryStream()
    let mutable complete=false
    while not complete do
        let! part=socket.ReceiveAsync(buffer.AsMemory(),token)
        if part.MessageType<>WebSocketMessageType.Text then invalidOp "Unexpected test frame"
        stream.Write(buffer,0,part.Count)
        complete<-part.EndOfMessage
    use value=JsonDocument.Parse(stream.ToArray())
    return value.RootElement.Clone()
}
let private timestamp value =
    let bytes=Array.zeroCreate<byte> 8
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes,value)
    Convert.ToBase64String bytes
let private version queries time = {|querySet=queries;ts=timestamp time;identity=0|}
let private update (socket:WebSocket) token first next values = task {
    let changes=values |> List.map(fun(id,value) -> {|``type``="QueryUpdated";queryId=id;value=wire value;journal="test-journal"|}) |> List.toArray
    let bytes=JsonSerializer.SerializeToUtf8Bytes {|``type``="Transition";startVersion=first;endVersion=next;modifications=changes|}
    do! socket.SendAsync(bytes.AsMemory(),WebSocketMessageType.Text,true,token)
}
let private wait (token:CancellationToken) predicate = task {
    while not(predicate()) do do! Task.Delay(5,token)
}
let private id (item:JsonElement) = item.GetProperty("queryId").GetInt32()
let private edits (item:JsonElement) = item.GetProperty("modifications").EnumerateArray() |> Seq.toArray
let private withServer (action:Reactive.Client -> Paginated.Client<int> -> WebSocket -> int -> CancellationToken -> Task<unit>) = task {
    use reserve=new TcpListener(IPAddress.Loopback,0)
    reserve.Start()
    let port=(reserve.LocalEndpoint :?> IPEndPoint).Port
    reserve.Stop()
    use listener=new HttpListener()
    listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
    listener.Start()
    use stop=new CancellationTokenSource(TimeSpan.FromSeconds 8.)
    use client=new Reactive.Client(ConvexHttp.endpoint(sprintf "http://127.0.0.1:%d" port))
    use pages=new Paginated.Client<int>(client,factory,initialNumItems=2)
    let running=client.RunAsync(stop.Token)
    let! request=listener.GetContextAsync().WaitAsync(stop.Token)
    let! accepted=request.AcceptWebSocketAsync(null)
    use socket=accepted.WebSocket
    let! _=receive socket stop.Token
    let! first=receive socket stop.Token
    let firstId=id ((edits first)[0])
    try do! action client pages socket firstId stop.Token
    finally stop.Cancel();listener.Stop();socket.Abort()
    try do! running with :? OperationCanceledException -> ()
}

[<Fact>]
let ``pagination starts with one loading query and does not load ahead`` () =
    use client=new Reactive.Client(ConvexHttp.endpoint "http://127.0.0.1:3210")
    use pages=new Paginated.Client<int>(client,factory,initialNumItems=2)
    Assert.Equal(1,client.QueryCount)
    Assert.Equal(Paginated.Status.LoadingFirstPage,pages.Current.Status)
    Assert.False(pages.LoadMore())

[<Fact>]
let ``pagination owns its subscriptions but never the shared client`` () =
    use client=new Reactive.Client(ConvexHttp.endpoint "http://127.0.0.1:3210")
    let pages=new Paginated.Client<int>(client,factory)
    (pages :> IDisposable).Dispose()
    Assert.Equal(0,client.QueryCount)
    use lease=client.Watch(factory(AdvancedPaginationOptions.Create 1))
    Assert.Equal(1,client.QueryCount)

[<Fact>]
let ``continuation pages remain reactive and concatenate coherently`` () = withServer(fun client pages socket first token -> task {
    do! update socket token (version 0 0UL) (version 1 1UL) [first,page [1;2] false "next" None None]
    do! wait token (fun()->pages.Current.Status=Paginated.Status.CanLoadMore)
    Assert.True(pages.LoadMore())
    Assert.False(pages.LoadMore())
    let! modification=receive socket token
    let second=(edits modification)[0]
    let arguments=second.GetProperty("args")[0]
    let decoded=(reference()).Arguments.Decode(ConvexWire.toNative(arguments.GetRawText()))
    Assert.Equal(Some "next",decoded.Cursor)
    do! update socket token (version 1 1UL) (version 2 2UL) [id second,page [3] true "end" None None]
    do! wait token (fun()->pages.Current.Status=Paginated.Status.Exhausted)
    Assert.Equal<int list>([1;2;3],pages.Current.Results)
    Assert.Equal(2,client.QueryCount)
    do! update socket token (version 2 2UL) (version 2 3UL) [first,page [0;1;2] false "next" None None]
    do! wait token (fun()->pages.Current.Results.Length=4)
    Assert.Equal<int list>([0;1;2;3],pages.Current.Results)
    Assert.False(pages.LoadMore())
})

[<Fact>]
let ``required splits wait for both replacement ranges`` () = withServer(fun client pages socket first token -> task {
    do! update socket token (version 0 0UL) (version 1 1UL) [first,page [] false "end" (Some PaginationPageStatus.SplitRequired) (Some "mid")]
    let! modification=receive socket token
    let children=edits modification
    Assert.Equal(2,children.Length)
    Assert.Equal(3,client.QueryCount)
    let left=id children[0]
    let right=id children[1]
    do! update socket token (version 1 1UL) (version 2 2UL) [left,page [1] false "mid" None None]
    do! wait token (fun()->pages.Current.Timestamp=Some 2UL)
    Assert.Empty pages.Current.Results
    do! update socket token (version 2 2UL) (version 2 3UL) [right,page [2] false "end" None None]
    do! wait token (fun()->pages.Current.Results.Length=2)
    Assert.Equal<int list>([1;2],pages.Current.Results)
    Assert.Equal(2,client.QueryCount)
})

[<Fact>]
let ``authentication changes discard loaded pages and previous cursors`` () = withServer(fun client pages socket first token -> task {
    do! update socket token (version 0 0UL) (version 1 1UL) [first,page [1;2] false "private-cursor" None None]
    do! wait token (fun()->pages.Current.Results.Length=2)
    Assert.True(pages.LoadMore())
    client.SetAuthentication(Some "replacement-user-token")
    Assert.Empty pages.Current.Results
    Assert.Equal(1,pages.Current.PageCount)
    Assert.Equal(1,client.QueryCount)
})

[<Fact>]
let ``pagination rejects unreasonable size and capacity`` () =
    use client=new Reactive.Client(ConvexHttp.endpoint "http://127.0.0.1:3210")
    Assert.Throws<ArgumentException>(fun()->new Paginated.Client<int>(client,factory,initialNumItems=0) |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun()->new Paginated.Client<int>(client,factory,maximumPages=0) |> ignore) |> ignore

[<Fact>]
let ``pagination observation serializes its initial callback with later snapshots`` () = withServer(fun _ pages socket first token -> task {
    use entered=new ManualResetEventSlim(false)
    use release=new ManualResetEventSlim(false)
    let mutable calls=0
    let mutable active=0
    let mutable overlap=0
    let completed=System.Collections.Concurrent.ConcurrentQueue<int64>()
    let registration=Task.Run(fun () ->
        pages.Observe(fun view ->
            if Interlocked.Increment(&active)>1 then Interlocked.Exchange(&overlap,1) |> ignore
            try
                if Interlocked.Increment(&calls)=1 then
                    entered.Set()
                    release.Wait(token)
                completed.Enqueue view.Revision
            finally Interlocked.Decrement(&active) |> ignore))
    try
        do! wait token (fun () -> entered.IsSet)
        do! update socket token (version 0 0UL) (version 1 1UL) [first,page [1;2] false "next" None None]
        do! wait token (fun () -> pages.Current.Results.Length=2)
    finally release.Set()
    use! observation=registration.WaitAsync(token)
    do! wait token (fun () -> completed.Count>=2)
    Assert.Equal(0,Volatile.Read(&overlap))
    let revisions=completed.ToArray()
    Assert.True(revisions |> Array.pairwise |> Array.forall(fun (first,next) -> first<next),"Pagination callbacks must never move backward in revision.")
})

[<Fact>]
let ``disposing a pagination observer cancels its queued notification`` () = withServer(fun _ pages socket first token -> task {
    let mutable second:IDisposable option=None
    let mutable delivered=0
    use before=pages.Observe(fun view ->
        if view.Timestamp=Some 1UL then second |> Option.iter(fun observer -> observer.Dispose()))
    use after=pages.Observe(fun view ->
        if view.Timestamp=Some 1UL then Interlocked.Increment(&delivered) |> ignore)
    second<-Some after
    do! update socket token (version 0 0UL) (version 1 1UL) [first,page [1;2] false "next" None None]
    do! wait token (fun () -> pages.Current.Timestamp=Some 1UL)
    // A final observer runs after both subscription slots have been processed.
    let seen=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    use finished=pages.Observe(fun view -> if view.Timestamp=Some 1UL then seen.TrySetResult() |> ignore)
    do! seen.Task.WaitAsync(token)
    Assert.Equal(0,Volatile.Read(&delivered))
})
