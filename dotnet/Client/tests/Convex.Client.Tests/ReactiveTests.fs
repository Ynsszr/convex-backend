module Convex.Client.Tests.ReactiveTests

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

let reference name = FunctionReference.publicQuery name (Contract.arguments<unit>()) (Contract.derived<int>())
let left () = Reactive.query (reference "counter:left") ()
let right () = Reactive.query (reference "counter:right") ()
let endpoint () = ConvexHttp.endpoint "http://127.0.0.1:3210"

[<Fact>]
let ``identical typed queries share a registration and reference count`` () =
    use client = new Reactive.Client(endpoint())
    use first = client.Watch(left())
    let second = client.Watch(Reactive.query (reference "counter:left") ())
    Assert.Equal(1,client.QueryCount)
    Assert.Equal(2,client.SubscriptionCount)
    second.Dispose(); second.Dispose()
    Assert.Equal(1,client.SubscriptionCount)
    first.Dispose()
    Assert.Equal(0,client.QueryCount)

[<Fact>]
let ``distinct arguments keep distinct server queries`` () =
    let fn=FunctionReference.publicQuery "counter:byId" (Contract.arguments<{|id:int|}>()) (Contract.derived<int>())
    use client = new Reactive.Client(endpoint())
    use first=client.Watch(Reactive.query fn {|id=1|})
    use second=client.Watch(Reactive.query fn {|id=2|})
    Assert.Equal(2,client.QueryCount)

[<Fact>]
let ``capacity applies to distinct queries not observers`` () =
    use client = new Reactive.Client(endpoint(),maximumQueries=1)
    use first=client.Watch(left())
    use repeated=client.Watch(left())
    Assert.Throws<InvalidOperationException>(fun () -> client.Watch(right()) |> ignore) |> ignore

[<Fact>]
let ``disposed clients refuse new subscriptions`` () =
    let client=new Reactive.Client(endpoint())
    (client:>IDisposable).Dispose()
    Assert.Throws<ObjectDisposedException>(fun () -> client.Watch(left()) |> ignore) |> ignore

[<Fact>]
let ``snapshot explicitly represents loading`` () =
    use client=new Reactive.Client(endpoint())
    match Reactive.Snapshot.read (left()) client.Current with
    | Reactive.Value.Loading -> ()
    | _ -> failwith "No query has run."

let private receive (socket:WebSocket) token = task {
    use stream=new MemoryStream()
    let buffer=Array.zeroCreate<byte> 8192
    let mutable complete=false
    while not complete do
        let! item=socket.ReceiveAsync(buffer.AsMemory(),token)
        if item.MessageType<>WebSocketMessageType.Text then failwith "Expected a client message."
        stream.Write(buffer,0,item.Count)
        complete<-item.EndOfMessage
    use value=JsonDocument.Parse(stream.ToArray())
    return value.RootElement.Clone()
}
let private send (socket:WebSocket) token value = task {
    let bytes=JsonSerializer.SerializeToUtf8Bytes value
    do! socket.SendAsync(bytes.AsMemory(),WebSocketMessageType.Text,true,token)
}
let private stamp value =
    let bytes=BitConverter.GetBytes(uint64 value)
    if not BitConverter.IsLittleEndian then Array.Reverse bytes
    Convert.ToBase64String bytes
let private version queries time = {|querySet=queries;ts=stamp time;identity=0|}
let private change id value = {|``type``="QueryUpdated";queryId=id;value=value;logLines=Array.empty<string>;journal=(null:string)|}
let private server action = task {
    use reserve=new TcpListener(IPAddress.Loopback,0)
    reserve.Start()
    let port=(reserve.LocalEndpoint :?> IPEndPoint).Port
    reserve.Stop()
    use listener=new HttpListener()
    listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
    listener.Start()
    use stop=new CancellationTokenSource(TimeSpan.FromSeconds 12.)
    let accept () = task {
        let! request=listener.GetContextAsync().WaitAsync(stop.Token)
        let! context=request.AcceptWebSocketAsync(null)
        return context.WebSocket
    }
    try do! action (ConvexHttp.endpoint(sprintf "http://127.0.0.1:%d" port)) stop accept
    finally listener.Stop()
}

[<Fact>]
let ``two related query results are published in one snapshot over one socket`` () = server (fun endpoint stop accept -> task {
    use client=new Reactive.Client(endpoint)
    use first=client.Watch(left())
    use repeated=client.Watch(left())
    use second=client.Watch(right())
    let arrived=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable torn=false
    use brokenObserver=client.Observe(fun _ -> failwith "A consumer callback is isolated.")
    use observer=client.Observe(fun snapshot ->
        match Reactive.Snapshot.read (left()) snapshot,Reactive.Snapshot.read (right()) snapshot with
        | Reactive.Value.Ready a,Reactive.Value.Ready b ->
            if a<>b then torn<-true
            if a=2 then arrived.TrySetResult() |> ignore
        | _ -> ())
    let run=client.RunAsync(stop.Token)
    use! socket=accept()
    let! hello=receive socket stop.Token
    Assert.Equal("Connect",hello.GetProperty("type").GetString())
    let! subscribe=receive socket stop.Token
    let ids=subscribe.GetProperty("modifications").EnumerateArray() |> Seq.map(fun v -> v.GetProperty("queryId").GetInt32()) |> Seq.toArray
    Assert.Equal(2,ids.Length)
    do! send socket stop.Token {|``type``="Transition";startVersion=version 0 0;endVersion=version 1 1;modifications=[|change ids[0] 1;change ids[1] 1|]|}
    do! send socket stop.Token {|``type``="Transition";startVersion=version 1 1;endVersion=version 1 2;modifications=[|change ids[0] 2;change ids[1] 2|]|}
    do! arrived.Task.WaitAsync(stop.Token)
    Assert.False(torn)
    Assert.Equal(1,client.ConnectionCount)
    stop.Cancel()
    do! run
})

[<Fact>]
let ``server version mismatch discards the transition`` () = server (fun endpoint stop accept -> task {
    use client=new Reactive.Client(endpoint)
    use lease=client.Watch(left())
    let failed=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    use observer=client.Observe(fun snapshot -> match Reactive.Snapshot.connection snapshot with Reactive.Connection.Failed _ -> failed.TrySetResult() |> ignore | _ -> ())
    let run=client.RunAsync(stop.Token)
    use! socket=accept()
    let! _=receive socket stop.Token
    let! add=receive socket stop.Token
    let id=(add.GetProperty("modifications").EnumerateArray() |> Seq.head).GetProperty("queryId").GetInt32()
    do! send socket stop.Token {|``type``="Transition";startVersion=version 0 5;endVersion=version 1 6;modifications=[|change id 42|]|}
    do! failed.Task.WaitAsync(stop.Token)
    match Reactive.Snapshot.read (left()) client.Current with Reactive.Value.Loading -> () | _ -> failwith "Invalid snapshot was published."
    stop.Cancel()
    do! run
})

[<Fact>]
let ``authentication changes immediately remove previous query data`` () = server (fun endpoint stop accept -> task {
    use client=new Reactive.Client(endpoint)
    use lease=client.Watch(left())
    let ready=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    use observer=client.Observe(fun snapshot -> match Reactive.Snapshot.read (left()) snapshot with Reactive.Value.Ready _ -> ready.TrySetResult() |> ignore | _ -> ())
    let run=client.RunAsync(stop.Token)
    use! socket=accept()
    let! _=receive socket stop.Token
    let! add=receive socket stop.Token
    let id=(add.GetProperty("modifications").EnumerateArray() |> Seq.head).GetProperty("queryId").GetInt32()
    do! send socket stop.Token {|``type``="Transition";startVersion=version 0 0;endVersion=version 1 1;modifications=[|change id 42|]|}
    do! ready.Task.WaitAsync(stop.Token)
    client.SetAuthentication(Some "synthetic-new-identity")
    match Reactive.Snapshot.read (left()) client.Current with Reactive.Value.Loading -> () | _ -> failwith "Previous identity remained visible."
    stop.Cancel()
    do! run
})

[<Fact>]
let ``subscriptions can be added and removed without replacing the socket`` () = server (fun endpoint stop accept -> task {
    use client=new Reactive.Client(endpoint)
    use leftLease=client.Watch(left())
    let both=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    use observer=client.Observe(fun snapshot ->
        match Reactive.Snapshot.read (right()) snapshot with
        | Reactive.Value.Ready 2 -> both.TrySetResult() |> ignore
        | _ -> ())
    let run=client.RunAsync(stop.Token)
    use! socket=accept()
    let! _=receive socket stop.Token
    let! initial=receive socket stop.Token
    let leftId=(initial.GetProperty("modifications").EnumerateArray() |> Seq.head).GetProperty("queryId").GetInt32()
    do! send socket stop.Token {|``type``="Transition";startVersion=version 0 0;endVersion=version 1 1;modifications=[|change leftId 1|]|}
    let rightLease=client.Watch(right())
    let! added=receive socket stop.Token
    Assert.Equal(1,added.GetProperty("baseVersion").GetInt32())
    let rightId=(added.GetProperty("modifications").EnumerateArray() |> Seq.head).GetProperty("queryId").GetInt32()
    do! send socket stop.Token {|``type``="Transition";startVersion=version 1 1;endVersion=version 2 2;modifications=[|change leftId 2;change rightId 2|]|}
    do! both.Task.WaitAsync(stop.Token)
    rightLease.Dispose()
    let! removed=receive socket stop.Token
    let edit=removed.GetProperty("modifications").EnumerateArray() |> Seq.head
    Assert.Equal("Remove",edit.GetProperty("type").GetString())
    Assert.Equal(rightId,edit.GetProperty("queryId").GetInt32())
    do! send socket stop.Token {|``type``="Transition";startVersion=version 2 2;endVersion=version 3 3;modifications=[|{|``type``="QueryRemoved";queryId=rightId|}|]|}
    Assert.Equal(1,client.QueryCount)
    Assert.Equal(1,client.ConnectionCount)
    stop.Cancel()
    do! run
})

[<Fact>]
let ``query reconnect preserves monotonic observed timestamp`` () = server (fun endpoint stop accept -> task {
    use client=new Reactive.Client(endpoint)
    use lease=client.Watch(left())
    let first=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let second=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    use observer=client.Observe(fun snapshot ->
        match Reactive.Snapshot.read (left()) snapshot with
        | Reactive.Value.Ready 1 -> first.TrySetResult() |> ignore
        | Reactive.Value.Ready 2 -> second.TrySetResult() |> ignore
        | _ -> ())
    let run=client.RunAsync(stop.Token)
    use! one=accept()
    let! _=receive one stop.Token
    let! add=receive one stop.Token
    let id=(add.GetProperty("modifications").EnumerateArray() |> Seq.head).GetProperty("queryId").GetInt32()
    do! send one stop.Token {|``type``="Transition";startVersion=version 0 0;endVersion=version 1 9;modifications=[|change id 1|]|}
    do! first.Task.WaitAsync(stop.Token)
    client.Reconnect()
    use! two=accept()
    let! hello=receive two stop.Token
    Assert.Equal(stamp 9,hello.GetProperty("maxObservedTimestamp").GetString())
    let! _=receive two stop.Token
    do! send two stop.Token {|``type``="Transition";startVersion=version 0 0;endVersion=version 1 10;modifications=[|change id 2|]|}
    do! second.Task.WaitAsync(stop.Token)
    Assert.Equal(2,client.ConnectionCount)
    stop.Cancel()
    do! run
})


[<Fact>]
let ``disposing the shared client stops remaining queued observers`` () =
    use client=new Reactive.Client(ConvexHttp.endpoint "http://127.0.0.1:3210")
    use first=client.Observe(fun snapshot ->
        if Reactive.Snapshot.identityGeneration snapshot>0 then (client :> IDisposable).Dispose())
    let mutable delivered=0
    use second=client.Observe(fun snapshot ->
        if Reactive.Snapshot.identityGeneration snapshot>0 then delivered<-delivered+1)
    client.SetAuthentication(Some "replacement-identity")
    Assert.Equal(0,delivered)

[<EntryPoint>]
let main _ = 0
