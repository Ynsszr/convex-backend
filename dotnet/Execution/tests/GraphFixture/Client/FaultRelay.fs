module GraphFaultRelay

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Net.WebSockets
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Convex.Client
open GraphContracts
open GraphGenerated

let private receive (socket:WebSocket) (token:CancellationToken) = task {
    use stream=new MemoryStream()
    let buffer=Array.zeroCreate<byte> 8192
    let mutable complete=false
    while not complete do
        let! frame=socket.ReceiveAsync(buffer.AsMemory(),token)
        if frame.MessageType<>WebSocketMessageType.Text then raise(IOException "The test connection closed")
        if stream.Length+int64 frame.Count>2L*1024L*1024L then invalidOp "Unexpected test frame size"
        stream.Write(buffer,0,frame.Count)
        complete<-frame.EndOfMessage
    return stream.ToArray()
}

/// Drop the real server's first successful reply AFTER it committed, then
/// observe the SDK's reconnect through the unmodified backend protocol.
let run (origin:string) = task {
    use reserve=new TcpListener(IPAddress.Loopback,0)
    reserve.Start()
    let port=(reserve.LocalEndpoint :?> IPEndPoint).Port
    reserve.Stop()
    use stop=new CancellationTokenSource(TimeSpan.FromSeconds 25.)
    use listener=new HttpListener()
    listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
    listener.Start()
    let gate=obj()
    let hellos=ResizeArray<string>()
    let writes=ResizeArray<string>()
    let mutable dropped=false
    let address=UriBuilder(origin)
    address.Scheme<-"ws";address.Path<-"/api/1.46.0/sync"
    let relay=task {
        try
            while not stop.IsCancellationRequested do
                let! request=listener.GetContextAsync().WaitAsync(stop.Token)
                let! accepted=request.AcceptWebSocketAsync(null)
                use downstream=accepted.WebSocket
                use upstream=new ClientWebSocket()
                do! upstream.ConnectAsync(address.Uri,stop.Token)
                use connection=CancellationTokenSource.CreateLinkedTokenSource stop.Token
                let upload=task {
                    while not connection.IsCancellationRequested do
                        let! bytes=receive downstream connection.Token
                        use message=JsonDocument.Parse bytes
                        let item=message.RootElement
                        match item.GetProperty("type").GetString() with
                        | "Connect" -> lock gate (fun () -> hellos.Add(item.GetProperty("sessionId").GetString()))
                        | "Mutation" -> lock gate (fun () -> writes.Add(item.GetRawText()))
                        | _ -> ()
                        do! upstream.SendAsync(bytes.AsMemory(),WebSocketMessageType.Text,true,connection.Token)
                }
                let download=task {
                    while not connection.IsCancellationRequested do
                        let! bytes=receive upstream connection.Token
                        use message=JsonDocument.Parse bytes
                        let item=message.RootElement
                        let drop=lock gate (fun () ->
                            if not dropped && item.GetProperty("type").GetString()="MutationResponse" && item.GetProperty("success").GetBoolean() then
                                dropped<-true;true
                            else false)
                        if drop then connection.Cancel();upstream.Abort();downstream.Abort()
                        else do! downstream.SendAsync(bytes.AsMemory(),WebSocketMessageType.Text,true,connection.Token)
                }
                try let! _=Task.WhenAny(upload,download) in ()
                finally connection.Cancel();upstream.Abort();downstream.Abort()
                try do! upload with :? OperationCanceledException | :? IOException | :? WebSocketException -> ()
                try do! download with :? OperationCanceledException | :? IOException | :? WebSocketException -> ()
        with :? OperationCanceledException | :? HttpListenerException when stop.IsCancellationRequested -> ()
    }
    use client=new Reactive.Client(ConvexHttp.endpoint(sprintf "http://127.0.0.1:%d" port))
    let selected=Reactive.query Api.root.readProbe ()
    use watched=client.Watch selected
    let ready=TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    use observed=client.Observe(fun snapshot -> match Reactive.Snapshot.read selected snapshot with Reactive.Value.Ready value -> ready.TrySetResult value |> ignore | _ -> ())
    let connection=client.RunAsync(stop.Token)
    try
        let! original=ready.Task.WaitAsync(stop.Token)
        let mutation=client.MutateAsync(Api.root.writeProbe,{amount=1},optimisticUpdate=Reactive.Snapshot.update selected ((+)1))
        match Reactive.Snapshot.read selected client.Current with
        | Reactive.Value.Ready value when value=original+1 -> ()
        | _ -> failwith "The real client did not apply the optimistic update"
        let! result=mutation.WaitAsync(stop.Token)
        match result with ConvexHttp.Completed value when value=original+1 -> () | value -> failwithf "Unexpected replay result: %A" value
        let! actual=ConvexHttp.queryAsync (ConvexHttp.endpoint origin) ConvexHttp.Anonymous Api.root.readProbe ()
        match actual with ConvexHttp.Completed value when value=original+1 -> () | _ -> failwith "Lost-reply reconnect duplicated a committed mutation"
        lock gate (fun () ->
            if not dropped || writes.Count<>2 || writes[0]<>writes[1] || hellos.Count<>2 || hellos[0]<>hellos[1] then
                failwith "The lost-reply test did not preserve the exact session/request identity")
        if Reactive.Snapshot.optimisticCount client.Current<>0 then failwith "Optimistic update remained after confirmed commit"
        return true
    finally
        stop.Cancel();listener.Stop()
        try connection.GetAwaiter().GetResult() with :? OperationCanceledException -> ()
        try relay.GetAwaiter().GetResult() with :? OperationCanceledException -> ()
}
