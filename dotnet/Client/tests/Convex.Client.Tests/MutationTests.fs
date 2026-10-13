module Convex.Client.Tests.MutationTests

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

let private queryRef () = FunctionReference.publicQuery "counter:read" (Contract.arguments<unit>()) (Contract.derived<int>())
let private writeRef () = FunctionReference.publicMutation "counter:add" (Contract.arguments<{|amount:int|}>()) (Contract.derived<int>())
let private actionRef () = FunctionReference.publicAction "external:run" (Contract.arguments<unit>()) (Contract.derived<int>())
let private query () = Reactive.query (queryRef()) ()
let private receive (socket:WebSocket) token = task {
    use stream=new MemoryStream()
    let buffer=Array.zeroCreate<byte> 8192
    let mutable doneReading=false
    while not doneReading do
        let! part=socket.ReceiveAsync(buffer.AsMemory(),token)
        if part.MessageType<>WebSocketMessageType.Text then failwith "Expected sync text"
        stream.Write(buffer,0,part.Count)
        doneReading<-part.EndOfMessage
    use json=JsonDocument.Parse(stream.ToArray())
    return json.RootElement.Clone()
}
let private send (socket:WebSocket) token value = task {
    do! socket.SendAsync((JsonSerializer.SerializeToUtf8Bytes value).AsMemory(),WebSocketMessageType.Text,true,token)
}
let private stamp value =
    let bytes=Array.zeroCreate<byte> 8
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes,uint64 value)
    Convert.ToBase64String bytes
let private version queries time = {|querySet=queries;ts=stamp time;identity=0|}
let private transition socket token first next id value =
    send socket token {|``type``="Transition";startVersion=version (if first=0 then 0 else 1) first;endVersion=version 1 next;
                       modifications=[|{|``type``="QueryUpdated";queryId=id;value=value;logLines=Array.empty<string>;journal=(null:string)|}|]|}
let private response socket token id time value =
    send socket token {|``type``="MutationResponse";requestId=id;success=true;result=value;ts=stamp time;logLines=Array.empty<string>|}
let private wait token predicate = task {
    while not(predicate()) do do! Task.Delay(5,token)
}
let private value (client:Reactive.Client) = match Reactive.Snapshot.read (query()) client.Current with Reactive.Value.Ready value -> value | _ -> -9999
let private server action = task {
    use reserve=new TcpListener(IPAddress.Loopback,0)
    reserve.Start()
    let port=(reserve.LocalEndpoint :?> IPEndPoint).Port
    reserve.Stop()
    use listener=new HttpListener()
    listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
    listener.Start()
    use stop=new CancellationTokenSource(TimeSpan.FromSeconds 10.)
    let accept () = task {
        let! request=listener.GetContextAsync().WaitAsync(stop.Token)
        let! upgraded=request.AcceptWebSocketAsync(null)
        return upgraded.WebSocket
    }
    try do! action (ConvexHttp.endpoint(sprintf "http://127.0.0.1:%d" port)) stop accept
    finally stop.Cancel();listener.Stop()
}
let private start endpoint (stop:CancellationTokenSource) (accept:unit -> Task<WebSocket>) (action:Reactive.Client -> WebSocket -> JsonElement -> int -> Task<unit>) = task {
    use client=new Reactive.Client(endpoint)
    use watched=client.Watch(query())
    let run=client.RunAsync(stop.Token)
    use! socket=accept()
    let! hello=receive socket stop.Token
    let! added=receive socket stop.Token
    let id=(added.GetProperty("modifications").EnumerateArray() |> Seq.head).GetProperty("queryId").GetInt32()
    do! transition socket stop.Token 0 1 id 10
    do! wait stop.Token (fun () -> value client=10)
    do! action client socket hello id
    stop.Cancel()
    do! run
}

[<Fact>]
let ``optimistic mutation settles only after its committed query transition`` () = server(fun endpoint stop accept ->
    start endpoint stop accept (fun client socket _ queryId -> task {
        let mutation=client.MutateAsync(writeRef(),{|amount=1|},optimisticUpdate=Reactive.Snapshot.update (query()) ((+)1))
        Assert.Equal(11,value client)
        Assert.Equal(1,Reactive.Snapshot.optimisticCount client.Current)
        let! request=receive socket stop.Token
        let id=request.GetProperty("requestId").GetInt32()
        do! response socket stop.Token id 9 11
        do! Task.Delay(30,stop.Token)
        Assert.False(mutation.IsCompleted)
        Assert.Equal(11,value client)
        do! transition socket stop.Token 1 9 queryId 11
        let! result=mutation.WaitAsync(stop.Token)
        match result with ConvexHttp.Completed 11 -> () | other -> failwithf "%A" other
        Assert.Equal(11,value client)
        Assert.Equal(0,Reactive.Snapshot.optimisticCount client.Current)
    }))

[<Fact>]
let ``failed mutations roll back optimistic changes and preserve safe errors`` () = server(fun endpoint stop accept ->
    start endpoint stop accept (fun client socket _ _ -> task {
        let mutation=client.MutateAsync(writeRef(),{|amount=1|},optimisticUpdate=Reactive.Snapshot.update (query()) ((+)1))
        let! request=receive socket stop.Token
        do! send socket stop.Token {|``type``="MutationResponse";requestId=request.GetProperty("requestId").GetInt32();success=false;result="invalid amount";logLines=Array.empty<string>|}
        let! result=mutation.WaitAsync(stop.Token)
        match result with ConvexHttp.Refused(ConvexHttp.InvocationRefusal.ApplicationError error) -> Assert.Equal("invalid amount",error.Message) | other -> failwithf "%A" other
        Assert.Equal(10,value client)
        Assert.Equal(0,client.PendingMutations)
    }))

[<Fact>]
let ``pending optimistic transformations rebase in request order`` () = server(fun endpoint stop accept ->
    start endpoint stop accept (fun client socket _ queryId -> task {
        let first=client.MutateAsync(writeRef(),{|amount=1|},optimisticUpdate=Reactive.Snapshot.update (query()) ((+)1))
        let second=client.MutateAsync(writeRef(),{|amount=11|},optimisticUpdate=Reactive.Snapshot.update (query()) ((*)2))
        Assert.Equal(22,value client)
        let! one=receive socket stop.Token
        let! two=receive socket stop.Token
        do! response socket stop.Token (one.GetProperty("requestId").GetInt32()) 2 11
        do! transition socket stop.Token 1 2 queryId 11
        let! _=first.WaitAsync(stop.Token)
        Assert.Equal(22,value client)
        do! transition socket stop.Token 2 3 queryId 12
        do! wait stop.Token (fun () -> value client=24)
        do! response socket stop.Token (two.GetProperty("requestId").GetInt32()) 4 24
        do! transition socket stop.Token 3 4 queryId 24
        let! _=second.WaitAsync(stop.Token)
        Assert.Equal(24,value client)
        Assert.Equal(0,client.PendingMutations)
    }))

[<Fact>]
let ``mutation replay retains session request identity and exact arguments`` () = server(fun endpoint stop accept ->
    start endpoint stop accept (fun client socket hello queryId -> task {
        let mutation=client.MutateAsync(writeRef(),{|amount=1|})
        let! first=receive socket stop.Token
        client.Reconnect()
        use! reconnect=accept()
        let! again=receive reconnect stop.Token
        Assert.Equal(hello.GetProperty("sessionId").GetString(),again.GetProperty("sessionId").GetString())
        let! _=receive reconnect stop.Token
        let! replay=receive reconnect stop.Token
        Assert.Equal(first.GetRawText(),replay.GetRawText())
        do! response reconnect stop.Token (replay.GetProperty("requestId").GetInt32()) 3 11
        do! transition reconnect stop.Token 0 3 queryId 11
        let! result=mutation.WaitAsync(stop.Token)
        match result with ConvexHttp.Completed 11 -> () | other -> failwithf "%A" other
    }))

[<Fact>]
let ``actions are never automatically replayed after a lost reply`` () = server(fun endpoint stop accept ->
    start endpoint stop accept (fun client socket _ _ -> task {
        let action=client.ActionAsync(actionRef(),())
        let! sent=receive socket stop.Token
        Assert.Equal("Action",sent.GetProperty("type").GetString())
        client.Reconnect()
        use! next=accept()
        let! _=receive next stop.Token
        let! _=receive next stop.Token
        let! result=action.WaitAsync(stop.Token)
        match result with ConvexHttp.Uncertain failure -> Assert.True(failure.RequestMayHaveReachedOwner) | other -> failwithf "%A" other
        Assert.Equal(0,client.PendingActions)
    }))

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``automatic reconnect preserves the server base beneath optimistic mutations`` success = server(fun endpoint stop accept ->
    start endpoint stop accept (fun client socket hello queryId -> task {
        let mutation=client.MutateAsync(writeRef(),{|amount=1|},optimisticUpdate=Reactive.Snapshot.update (query()) ((+)1))
        let! first=receive socket stop.Token
        let acceptReplay () = task {
            let! next=accept()
            let! connection=receive next stop.Token
            Assert.Equal(hello.GetProperty("sessionId").GetString(),connection.GetProperty("sessionId").GetString())
            let! _=receive next stop.Token
            let! replay=receive next stop.Token
            Assert.Equal(first.GetRawText(),replay.GetRawText())
            Assert.Equal(11,value client)
            Assert.Equal(1,Reactive.Snapshot.optimisticCount client.Current)
            return next,replay
        }
        // Exercise transport-driven retries, without an explicit Reconnect(),
        // and repeat the loss to catch projections compounding on each retry.
        do! socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"test disconnect",stop.Token)
        let! second,_=acceptReplay()
        use second=second
        do! second.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"second disconnect",stop.Token)
        let! third,replay=acceptReplay()
        use third=third
        let id=replay.GetProperty("requestId").GetInt32()
        if success then
            do! response third stop.Token id 3 11
            do! transition third stop.Token 0 3 queryId 11
        else
            do! send third stop.Token {|``type``="MutationResponse";requestId=id;success=false;result="rejected";logLines=Array.empty<string>|}
        let! result=mutation.WaitAsync(stop.Token)
        match success,result with
        | true,ConvexHttp.Completed 11 -> Assert.Equal(11,value client)
        | false,ConvexHttp.Refused(ConvexHttp.InvocationRefusal.ApplicationError _) -> Assert.Equal(10,value client)
        | _ -> failwithf "Unexpected outcome: %A" result
        Assert.Equal(0,Reactive.Snapshot.optimisticCount client.Current)
    }))

[<Fact>]
let ``only the final query lease evicts the server base before a fresh subscription`` () = server(fun endpoint stop accept -> task {
    use client=new Reactive.Client(endpoint)
    let watched=client.Watch(query())
    let duplicate=client.Watch(query())
    // Keep the connection alive while the counter's last lease is released.
    let other=Reactive.query (FunctionReference.publicQuery "counter:other" (Contract.arguments<unit>()) (Contract.derived<int>())) ()
    use keep=client.Watch(other)
    let run=client.RunAsync(stop.Token)
    use! socket=accept()
    let! _=receive socket stop.Token
    let! added=receive socket stop.Token
    let edit=added.GetProperty("modifications").EnumerateArray() |> Seq.find(fun item -> item.GetProperty("udfPath").GetString()="counter:read")
    do! transition socket stop.Token 0 1 (edit.GetProperty("queryId").GetInt32()) 10
    do! wait stop.Token (fun () -> value client=10)
    duplicate.Dispose()
    Assert.Equal(10,value client)
    watched.Dispose()
    Assert.Equal(Reactive.Value.Loading,Reactive.Snapshot.read (query()) client.Current)
    use replacement=client.Watch(query())
    Assert.Equal(Reactive.Value.Loading,Reactive.Snapshot.read (query()) client.Current)
    let pending=client.MutateAsync(writeRef(),{|amount=1|})
    Assert.Equal(Reactive.Value.Loading,Reactive.Snapshot.read (query()) client.Current)
    stop.Cancel()
    do! run
    let! _=pending
    return ()
})

[<Fact>]
let ``mutations work with no active query subscriptions`` () = server(fun endpoint stop accept -> task {
    use client=new Reactive.Client(endpoint)
    let mutation=client.MutateAsync(writeRef(),{|amount=1|})
    let run=client.RunAsync(stop.Token)
    use! socket=accept()
    let! _=receive socket stop.Token
    let! request=receive socket stop.Token
    Assert.Equal("Mutation",request.GetProperty("type").GetString())
    do! response socket stop.Token (request.GetProperty("requestId").GetInt32()) 2 1
    do! send socket stop.Token {|``type``="Transition";startVersion=version 0 0;endVersion=version 0 2;modifications=Array.empty<obj>|}
    let! result=mutation.WaitAsync(stop.Token)
    match result with ConvexHttp.Completed 1 -> () | other -> failwithf "%A" other
    stop.Cancel();do! run
})

[<Fact>]
let ``a queued write is not sent under a replacement identity`` () = task {
    use client=new Reactive.Client(ConvexHttp.endpoint "http://127.0.0.1:3210")
    let pending=client.MutateAsync(writeRef(),{|amount=1|})
    client.SetAuthentication(Some "new-principal")
    let! result=pending
    match result with ConvexHttp.Refused _ -> () | other -> failwithf "%A" other
    Assert.Equal(0,client.PendingMutations)
}

[<Fact>]
let ``pending write capacity refuses before sending another operation`` () =
    use client=new Reactive.Client(ConvexHttp.endpoint "http://127.0.0.1:3210",maximumPendingRequests=1)
    let pending=client.MutateAsync(writeRef(),{|amount=1|})
    Assert.Throws<InvalidOperationException>(fun () -> client.MutateAsync(writeRef(),{|amount=2|}) |> ignore) |> ignore
    Assert.Equal(1,client.PendingMutations)

[<Fact>]
let ``optimistic callbacks cannot invoke client side effects`` () =
    use client=new Reactive.Client(ConvexHttp.endpoint "http://127.0.0.1:3210")
    Assert.Throws<InvalidOperationException>(fun () -> client.MutateAsync(writeRef(),{|amount=1|},optimisticUpdate=(fun snapshot -> client.Watch(query()) |> ignore;snapshot)) |> ignore) |> ignore
    Assert.Equal(0,client.PendingMutations)
