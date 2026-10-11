namespace Convex.Client

open System
open System.IO
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Convex.FSharp

/// Typed queries and session-scoped mutations share one coherent synchronization
/// connection. HTTP remains a separate, explicitly one-attempt transport.
module Reactive =
    [<RequireQualifiedAccess>]
    type Connection = Connecting | Connected | Reconnecting | Stopped | Failed of ConvexHttp.InvocationRefusal
    [<RequireQualifiedAccess>]
    type Value<'T> = Loading | Ready of 'T | Failed of ConvexHttp.InvocationRefusal
    type Query<'T> = private { Key: string; Path: string; Arguments: JsonElement; Decode: JsonElement -> 'T; Encode: 'T -> JsonElement }
    type private WireValue = Success of JsonElement | Failure of ConvexHttp.ApplicationError
    type Snapshot = private { Values: Map<string, WireValue>; Stamp: uint64 option; Status: Connection; Revision: int64; OptimisticCount: int; Identity: int }

    let private canonical (value: JsonElement) =
        use bytes = new MemoryStream()
        use writer = new Utf8JsonWriter(bytes)
        let rec write depth (item: JsonElement) =
            if depth > 64 then invalidArg "arguments" "Query arguments exceed their depth bound."
            match item.ValueKind with
            | JsonValueKind.Object ->
                writer.WriteStartObject()
                for field in item.EnumerateObject() |> Seq.sortBy _.Name do
                    writer.WritePropertyName field.Name
                    write (depth + 1) field.Value
                writer.WriteEndObject()
            | JsonValueKind.Array ->
                writer.WriteStartArray()
                for child in item.EnumerateArray() do write (depth + 1) child
                writer.WriteEndArray()
            | _ -> item.WriteTo writer
        write 0 value
        writer.Flush()
        if bytes.Length > 128L * 1024L then invalidArg "arguments" "Query arguments exceed 128 KiB."
        Encoding.UTF8.GetString(bytes.ToArray())

    let query (reference: PublicQueryReference<'Args, 'Result>) arguments : Query<'Result> =
        match reference.Target with
        | FunctionTarget.Named _ -> ()
        | _ -> invalidArg "reference" "A public root query is required for client subscriptions."
        let encoded = reference.Arguments.Encode arguments |> Codec.decode<ConvexValue> |> ConvexJson.toJson
        use document = JsonDocument.Parse encoded
        let argument = document.RootElement.Clone()
        ConvexHttp.validateWire argument
        if argument.ValueKind <> JsonValueKind.Object then invalidArg "arguments" "A query requires an object argument envelope."
        let path = reference.Address
        if String.IsNullOrWhiteSpace path || path.Length > 1024 then invalidArg "reference" "Invalid query address."
        { Key = path + ":" + canonical argument; Path = path; Arguments = argument
          Decode = fun value -> reference.Returns.Decode(ConvexWire.toNative(value.GetRawText()))
          Encode = fun value ->
              let wire=reference.Returns.Encode value |> Codec.decode<ConvexValue> |> ConvexJson.toJson
              use document=JsonDocument.Parse wire
              document.RootElement.Clone() }

    module Snapshot =
        let read query snapshot =
            match snapshot.Values.TryFind query.Key with
            | None -> Value.Loading
            | Some(Failure error) -> Value.Failed(ConvexHttp.InvocationRefusal.ApplicationError error)
            | Some(Success value) ->
                try Value.Ready(query.Decode value)
                with _ -> Value.Failed ConvexHttp.InvocationRefusal.IncompatibleResponse
        /// Optimistic transformations are pure values. Recomputing a projection
        /// never authorizes an additional mutation or external effect.
        let set (query:Query<'T>) value snapshot =
            let encoded=query.Encode value
            ConvexHttp.validateWire encoded
            {snapshot with Values=snapshot.Values.Add(query.Key,Success encoded)}
        let update query transform snapshot =
            match read query snapshot with
            | Value.Ready value -> set query (transform value) snapshot
            | _ -> snapshot
        let optimisticCount snapshot = snapshot.OptimisticCount
        /// Changes when authentication is replaced; never contains a token.
        let identityGeneration snapshot = snapshot.Identity
        let timestamp snapshot = snapshot.Stamp
        let connection snapshot = snapshot.Status
        let revision snapshot = snapshot.Revision

    type private RequestStage = Queued | Dispatched of connection: int | Committed of result:JsonElement * timestamp:uint64
    type private Request = {
        Id:int; Kind:string; Path:string; Arguments:JsonElement
        Completion:TaskCompletionSource<ConvexHttp.InvocationOutcome<JsonElement>>
        Projection:(Snapshot -> Snapshot) option; Stage:RequestStage
    }

    type private Registration = { Id: int; Key: string; Path: string; Arguments: JsonElement; References: int }
    type private Version = { QuerySet: int; Timestamp: uint64; Identity: int }
    type private ServerState = { Version: Version; Values: Map<int, WireValue>; Journals: Map<int, JsonElement> }
    let private emptyServer = { Version = { QuerySet = 0; Timestamp = 0UL; Identity = 0 }; Values = Map.empty; Journals = Map.empty }
    let private incompatible () = invalidOp "Incompatible Convex query synchronization response."
    let private field name (value: JsonElement) = value.GetProperty(name: string)
    let private text name value = (field name value).GetString()
    let private number (value: JsonElement) =
        let mutable result = 0
        if not(value.TryGetInt32(&result)) || result < 0 then incompatible()
        result
    let private timestamp (value: JsonElement) =
        let encoded = value.GetString()
        let bytes = Convert.FromBase64String encoded
        if bytes.Length <> 8 || Convert.ToBase64String bytes <> encoded then incompatible()
        Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian bytes
    let private encodeTimestamp stamp =
        let bytes = Array.zeroCreate<byte> 8
        Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, stamp)
        Convert.ToBase64String bytes
    let private version value =
        { QuerySet = number(field "querySet" value); Timestamp = timestamp(field "ts" value); Identity = number(field "identity" value) }
    let private parse (bytes: byte[]) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte> bytes, JsonDocumentOptions(MaxDepth = 64))
        let rec unique depth (value: JsonElement) =
            if depth > 64 then incompatible()
            match value.ValueKind with
            | JsonValueKind.Object ->
                let names = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                for property in value.EnumerateObject() do
                    if not(names.Add property.Name) then incompatible()
                    unique (depth + 1) property.Value
            | JsonValueKind.Array -> for child in value.EnumerateArray() do unique (depth + 1) child
            | _ -> ()
        unique 0 document.RootElement
        document.RootElement.Clone()

    /// Apply the entire transition before exposing any value to subscribers.
    let private transition sentVersion expectedIdentity known (state: ServerState) message =
        let first = version(field "startVersion" message)
        let next = version(field "endVersion" message)
        if first <> state.Version || next.QuerySet < first.QuerySet || next.QuerySet > sentVersion ||
           next.Timestamp < first.Timestamp || next.Identity < first.Identity || next.Identity > expectedIdentity then incompatible()
        let modifications = field "modifications" message
        if modifications.ValueKind <> JsonValueKind.Array || modifications.GetArrayLength() > 1024 then incompatible()
        let seen = Collections.Generic.HashSet<int>()
        let mutable values = state.Values
        let mutable journals = state.Journals
        for change in modifications.EnumerateArray() do
            let id = number(field "queryId" change)
            if not(Set.contains id known) || not(seen.Add id) then incompatible()
            match text "type" change with
            | "QueryUpdated" ->
                let value = (field "value" change).Clone()
                ConvexHttp.validateWire value
                values <- values.Add(id, Success value)
            | "QueryFailed" -> values <- values.Add(id, Failure(ConvexHttp.applicationError change))
            | "QueryRemoved" -> values <- values.Remove id; journals <- journals.Remove id
            | _ -> incompatible()
            match change.TryGetProperty "journal" with
            | true, journal when journal.ValueKind = JsonValueKind.Null || journal.ValueKind = JsonValueKind.String ->
                if journal.ValueKind = JsonValueKind.String && journal.GetString().Length > 131072 then incompatible()
                journals <- journals.Add(id, journal.Clone())
            | true, _ -> incompatible()
            | _ -> ()
        if expectedIdentity <> next.Identity && modifications.GetArrayLength() > 0 then incompatible()
        { Version = next; Values = values; Journals = journals }

    /// One socket and one canonical subscription per query/arguments pair.
    /// Snapshots are immutable; observers are notified once per server transition.
    type Client(endpoint: ConvexHttp.Endpoint, ?maximumQueries: int, ?maximumPendingRequests:int) =
        let maximum = defaultArg maximumQueries 128
        do if maximum < 1 || maximum > 512 then invalidArg "maximumQueries" "Select 1..512 distinct active queries."
        let requestLimit=defaultArg maximumPendingRequests 256
        do if requestLimit<1 || requestLimit>1024 then invalidArg "maximumPendingRequests" "Select 1..1024 pending requests."
        let gate = obj()
        let changes = Channel.CreateBounded<unit>(BoundedChannelOptions(1, FullMode = BoundedChannelFullMode.DropOldest))
        let lifetime = new CancellationTokenSource()
        let sessionId = Guid.NewGuid().ToString("D")
        let mutable desired: Map<string, Registration> = Map.empty
        let mutable observers: Map<int, SnapshotObserver.Subscription<Snapshot>> = Map.empty
        let mutable nextObserver = 0
        let mutable nextQuery = 0
        let mutable epoch = 0
        let mutable token: string option = None
        let mutable connections = 0
        let mutable running = false
        let mutable disposed = false
        let mutable current = { Values = Map.empty; Stamp = None; Status = Connection.Stopped; Revision = 0L; OptimisticCount=0; Identity=0 }
        let mutable baseSnapshot=current
        let mutable requests:Map<int,Request>=Map.empty
        let mutable nextRequest=0
        let mutable projecting=false
        let mutable lastTimestamp = 0UL
        let mutable retainedJournals: Map<string, JsonElement> = Map.empty
        let mutable connectionCancellation: CancellationTokenSource option = None
        let notify () = changes.Writer.TryWrite() |> ignore
        let cancelConnection (source:CancellationTokenSource) =
            // Cleanup may dispose a captured connection before cancellation
            // runs. No callback is invoked while holding the client-state lock.
            try source.Cancel()
            with :? ObjectDisposedException -> ()
        let check () =
            if disposed then raise(ObjectDisposedException("Reactive.Client"))
            if projecting then invalidOp "An optimistic transformation must be pure and cannot call the client."
        let selected () = lock gate (fun () -> desired, token, epoch)
        let uncertainty cause = ConvexHttp.Uncertain {Cause=cause;RequestMayHaveReachedOwner=true}
        let cancelledRequest request =
            match request.Stage with
            | Queued -> ConvexHttp.Refused ConvexHttp.InvocationRefusal.Unavailable
            | _ -> uncertainty ConvexHttp.TransportCause.Cancelled
        let recomputeLocked () =
            let mutable view=baseSnapshot
            let mutable applied=0
            projecting<-true
            try
                for KeyValue(_,request) in requests do
                    match request.Projection with
                    | Some project ->
                        // A later server result can invalidate an optimistic
                        // assumption. Drop only that projection, never replay
                        // or misclassify its still-pending database operation.
                        try view<-project view;applied<-applied+1
                        with _ -> ()
                    | None -> ()
            finally projecting<-false
            current<-{view with Status=baseSnapshot.Status;Stamp=baseSnapshot.Stamp;Identity=baseSnapshot.Identity;
                                Values=view.Values |> Map.filter(fun key _ -> desired.ContainsKey key)
                                Revision=current.Revision+1L;OptimisticCount=applied}
            current,observers |> Map.toArray |> Array.map snd
        let announce (snapshot,callbacks) = for callback in callbacks do SnapshotObserver.publish callback snapshot
        let publish selectedEpoch status stamp values =
            let notification=lock gate (fun () ->
                if selectedEpoch<>epoch || disposed then None else
                baseSnapshot<-{baseSnapshot with Values=values |> Map.filter(fun key _ -> desired.ContainsKey key);Stamp=stamp;Status=status}
                let completed=requests |> Map.toArray |> Array.choose(fun (id,request) ->
                    match request.Stage,stamp with
                    | Committed(result,committed),Some observed when status=Connection.Connected && committed<=observed -> Some(id,request,result)
                    | _ -> None)
                for id,_,_ in completed do requests<-requests.Remove id
                Some(recomputeLocked(),completed))
            match notification with
            | Some(view,completed) ->
                // Current already contains the committed transition with the
                // matching overlays removed before any awaiter is released.
                for _,request,result in completed do request.Completion.TrySetResult(ConvexHttp.Completed result) |> ignore
                announce view
            | None -> ()
        let refresh () =
            let notification=lock gate (fun () -> if disposed then None else Some(recomputeLocked()))
            notification |> Option.iter announce
        let failRequests select outcome =
            let removed,notification=lock gate (fun () ->
                let removed=requests |> Map.toArray |> Array.filter(fun (_,request)->select request)
                for id,_ in removed do requests<-requests.Remove id
                removed,(if disposed then None else Some(recomputeLocked())))
            for _,request in removed do request.Completion.TrySetResult(outcome request) |> ignore
            notification |> Option.iter announce
        let enqueue kind path arguments projection =
            if String.IsNullOrWhiteSpace path || path.Length>1024 then invalidArg "reference" "Invalid function address."
            ConvexHttp.validateWire arguments
            if arguments.ValueKind<>JsonValueKind.Object || (canonical arguments).Length>128*1024 then invalidArg "arguments" "Invalid request envelope."
            let completion=TaskCompletionSource<ConvexHttp.InvocationOutcome<JsonElement>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let notification=lock gate (fun () ->
                check()
                if requests.Count>=requestLimit || nextRequest=Int32.MaxValue then invalidOp "Client pending-request capacity exhausted."
                // An invalid initial projection refuses the call before enqueue.
                projecting<-true
                try projection |> Option.iter(fun project -> project current |> ignore)
                finally projecting<-false
                let id=nextRequest
                nextRequest<-nextRequest+1
                requests<-requests.Add(id,{Id=id;Kind=kind;Path=path;Arguments=arguments;Completion=completion;Projection=projection;Stage=Queued})
                recomputeLocked())
            announce notification
            notify()
            completion.Task
        let response selectedEpoch message =
            let id=number(field "requestId" message)
            let request=lock gate (fun () -> if selectedEpoch=epoch then requests.TryFind id else None)
            match request with
            | None -> () // A safely retransmitted mutation may reply after its transition settled.
            | Some request ->
                if text "type" message<>request.Kind+"Response" then incompatible()
                match request.Stage with
                | Committed _ -> ()
                | Queued -> incompatible()
                | Dispatched _ ->
                    if (field "success" message).GetBoolean() then
                        let result=(field "result" message).Clone()
                        ConvexHttp.validateWire result
                        if request.Kind="Mutation" then
                            let committed=timestamp(field "ts" message)
                            let currentBase=lock gate (fun () ->
                                if selectedEpoch=epoch && requests.ContainsKey id then requests<-requests.Add(id,{request with Stage=Committed(result,committed)})
                                baseSnapshot)
                            publish selectedEpoch currentBase.Status currentBase.Stamp currentBase.Values
                        else
                            failRequests (fun value -> value.Id=id) (fun _ -> ConvexHttp.Completed result)
                    else
                        let error=Collections.Generic.Dictionary<string,obj>()
                        error["errorMessage"]<-box(text "result" message)
                        match message.TryGetProperty "errorData" with
                        | true,data -> error["errorData"]<-box data
                        | _ -> ()
                        let failure=ConvexHttp.applicationError(JsonSerializer.SerializeToElement error)
                        failRequests (fun value -> value.Id=id) (fun _ -> ConvexHttp.Refused(ConvexHttp.InvocationRefusal.ApplicationError failure))
        let send (socket: ClientWebSocket) (cancellation: CancellationToken) (value: obj) = task {
            let bytes = JsonSerializer.SerializeToUtf8Bytes value
            if bytes.Length > 1024 * 1024 then invalidOp "Query-set update exceeds 1 MiB."
            do! socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellation)
        }
        let receive (socket: ClientWebSocket) (cancellation: CancellationToken) = task {
            use deadline = CancellationTokenSource.CreateLinkedTokenSource cancellation
            deadline.CancelAfter(TimeSpan.FromSeconds 65.)
            use output = new MemoryStream()
            let buffer = Array.zeroCreate<byte> 8192
            let mutable finished = false
            while not finished do
                let! result = socket.ReceiveAsync(buffer.AsMemory(), deadline.Token)
                if result.MessageType = WebSocketMessageType.Close then raise(IOException("Convex closed the query connection."))
                if result.MessageType <> WebSocketMessageType.Text || output.Length + int64 result.Count > 2L*1024L*1024L then incompatible()
                output.Write(buffer, 0, result.Count)
                finished <- result.EndOfMessage
            return parse(output.ToArray())
        }
        let connect (cancellation: CancellationToken) = task {
            let _, authentication, selectedEpoch = selected()
            let expectedIdentity = if authentication.IsSome then 1 else 0
            let address = UriBuilder(ConvexHttp.address endpoint)
            address.Scheme <- if address.Scheme = "https" then "wss" else "ws"
            address.Path <- "/api/1.46.0/sync"
            use socket = new ClientWebSocket()
            if not(OperatingSystem.IsBrowser()) then socket.Options.KeepAliveInterval <- TimeSpan.FromSeconds 15.
            use local = CancellationTokenSource.CreateLinkedTokenSource cancellation
            lock gate (fun () -> connectionCancellation <- Some local)
            let connectionNumber = lock gate (fun () -> let number = connections in connections <- connections + 1; number)
            failRequests (fun request -> request.Kind="Action" && request.Stage<>Queued) (fun _ -> uncertainty ConvexHttp.TransportCause.ConnectionLost)
            let cached=lock gate (fun () -> baseSnapshot)
            publish selectedEpoch Connection.Connecting cached.Stamp cached.Values
            try
                use deadline = CancellationTokenSource.CreateLinkedTokenSource local.Token
                deadline.CancelAfter(TimeSpan.FromSeconds 20.)
                do! socket.ConnectAsync(address.Uri, deadline.Token)
                let hello = Collections.Generic.Dictionary<string,obj>()
                hello["type"] <- box "Connect"; hello["sessionId"] <- box sessionId
                hello["connectionCount"] <- box connectionNumber; hello["lastCloseReason"] <- box "NativeSharedQueries"
                hello["clientTs"] <- box Environment.TickCount64
                if lastTimestamp > 0UL then hello["maxObservedTimestamp"] <- box(encodeTimestamp lastTimestamp)
                do! send socket local.Token hello
                match authentication with
                | Some value -> do! send socket local.Token {| ``type``="Authenticate"; baseVersion=0; tokenType="User"; value=value |}
                | None -> ()
                let mutable sent: Map<int, Registration> = Map.empty
                let mutable known = Set.empty
                let mutable sentVersion = 0
                let mutable state = emptyServer
                let mutable journals = lock gate (fun () -> retainedJournals)
                let mutable sentRequests=Set.empty<int>
                let update () = task {
                    let requested, _, nowEpoch = selected()
                    if nowEpoch <> selectedEpoch || (requested.IsEmpty && lock gate (fun () -> requests.IsEmpty)) then local.Cancel()
                    let next = requested |> Map.toSeq |> Seq.map(fun (_,value) -> value.Id,value) |> Map.ofSeq
                    let additions = next |> Map.toSeq |> Seq.filter(fun (id,_) -> not(sent.ContainsKey id)) |> Seq.toArray
                    let removals = sent |> Map.toSeq |> Seq.filter(fun (id,_) -> not(next.ContainsKey id)) |> Seq.toArray
                    if additions.Length + removals.Length > 0 then
                        let edits = ResizeArray<obj>()
                        for id,_ in removals do edits.Add(box {| ``type``="Remove"; queryId=id |})
                        for id,registration in additions do
                            let item = Collections.Generic.Dictionary<string,obj>()
                            item["type"]<-box "Add"; item["queryId"]<-box id; item["udfPath"]<-box registration.Path
                            item["args"]<-box [|registration.Arguments|]
                            match journals.TryFind registration.Key with Some journal -> item["journal"]<-box journal | None -> ()
                            edits.Add item
                        if sentVersion = Int32.MaxValue then invalidOp "Query-set version exhausted."
                        do! send socket local.Token {| ``type``="ModifyQuerySet"; baseVersion=sentVersion; newVersion=sentVersion+1; modifications=edits.ToArray() |}
                        sentVersion <- sentVersion+1
                        known <- Set.union known (next |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
                        if known.Count > 8192 then raise(IOException("Renewing a long-lived query connection."))
                        sent <- next
                    let pending=lock gate (fun () -> requests |> Map.toArray |> Array.map snd)
                    for request in pending do
                        let eligible=lock gate (fun () ->
                            if epoch<>selectedEpoch || not(requests.ContainsKey request.Id) then false else
                            let live=requests[request.Id]
                            let sendNow = match live.Stage with
                                          | Queued -> true
                                          | Dispatched connection -> live.Kind="Mutation" && connection<>connectionNumber
                                          | Committed _ -> live.Kind="Mutation" && not(sentRequests.Contains live.Id)
                            if sendNow then
                                let stage=match live.Stage with Committed _ -> live.Stage | _ -> Dispatched connectionNumber
                                requests<-requests.Add(live.Id,{live with Stage=stage})
                            sendNow)
                        if eligible then
                            sentRequests<-sentRequests.Add request.Id
                            do! send socket local.Token {|``type``=request.Kind;requestId=request.Id;udfPath=request.Path;args=[|request.Arguments|]|}
                }
                do! update()
                let mutable incoming = receive socket local.Token
                let mutable changed = changes.Reader.ReadAsync(local.Token).AsTask()
                let chunks = StringBuilder()
                let mutable chunkId = ""
                let mutable chunkCount = 0
                let mutable chunkIndex = 0
                while not local.IsCancellationRequested do
                    let! ready = Task.WhenAny(incoming :> Task, changed :> Task)
                    if Object.ReferenceEquals(ready, changed) then
                        do! changed
                        do! update()
                        changed <- changes.Reader.ReadAsync(local.Token).AsTask()
                    else
                        let! message = incoming
                        let mutable selectedMessage = Some message
                        if text "type" message = "TransitionChunk" then
                            let index = number(field "partNumber" message)
                            let count = number(field "totalParts" message)
                            let identity = text "transitionId" message
                            if count < 1 || count > 4096 || index >= count || String.IsNullOrEmpty identity || identity.Length > 128 then incompatible()
                            if chunkIndex = 0 then chunkId <- identity; chunkCount <- count
                            if index <> chunkIndex || count <> chunkCount || identity <> chunkId then incompatible()
                            chunks.Append(text "chunk" message) |> ignore
                            if chunks.Length > 8*1024*1024 then incompatible()
                            chunkIndex <- chunkIndex+1
                            if chunkIndex = count then
                                selectedMessage <- Some(parse(Encoding.UTF8.GetBytes(chunks.ToString())))
                                chunks.Clear() |> ignore
                                chunkIndex<-0
                                if text "type" selectedMessage.Value <> "Transition" then incompatible()
                            else selectedMessage <- None
                        elif chunkIndex <> 0 && not(List.contains (text "type" message) ["Ping";"MutationResponse";"ActionResponse"]) then incompatible()
                        match selectedMessage with
                        | None -> ()
                        | Some message ->
                            match text "type" message with
                            | "Ping" -> ()
                            | "MutationResponse" | "ActionResponse" -> response selectedEpoch message
                            | "AuthError" ->
                                failRequests (fun _ -> selectedEpoch=epoch) (fun request -> match request.Stage with Queued -> ConvexHttp.Refused ConvexHttp.InvocationRefusal.AuthorityDenied | _ -> uncertainty ConvexHttp.TransportCause.ConnectionLost)
                                publish selectedEpoch (Connection.Failed ConvexHttp.InvocationRefusal.AuthorityDenied) None Map.empty
                                local.Cancel()
                            | "FatalError" -> incompatible()
                            | "Transition" ->
                                state <- transition sentVersion expectedIdentity known state message
                                lastTimestamp <- max lastTimestamp state.Version.Timestamp
                                let wanted,_,_=selected()
                                let values = wanted |> Map.toSeq |> Seq.choose(fun(key,registration) -> state.Values.TryFind registration.Id |> Option.map(fun value -> key,value)) |> Map.ofSeq
                                for KeyValue(_,registration) in sent do
                                    match state.Journals.TryFind registration.Id with
                                    | Some journal -> journals <- journals.Add(registration.Key,journal)
                                    | _ -> ()
                                lock gate (fun () ->
                                    if selectedEpoch = epoch then
                                        retainedJournals <- journals |> Map.filter(fun key _ -> desired.ContainsKey key))
                                publish selectedEpoch Connection.Connected (Some state.Version.Timestamp) values
                            | _ -> incompatible()
                        incoming <- receive socket local.Token
            finally
                socket.Abort()
                local.Cancel()
                lock gate (fun () -> connectionCancellation <- None)
        }

        member _.Watch(query: Query<'T>) =
            lock gate (fun () ->
                check()
                match desired.TryFind query.Key with
                | Some value -> desired <- desired.Add(query.Key, {value with References=value.References+1})
                | None ->
                    if desired.Count >= maximum || nextQuery = Int32.MaxValue then invalidOp "Shared query subscription capacity exhausted."
                    desired <- desired.Add(query.Key,{Id=nextQuery;Key=query.Key;Path=query.Path;Arguments=query.Arguments;References=1})
                    nextQuery <- nextQuery+1)
            notify()
            let mutable released = 0
            { new IDisposable with
                member _.Dispose() =
                    if Interlocked.Exchange(&released,1)=0 then
                        lock gate (fun () ->
                            match desired.TryFind query.Key with
                            | Some value when value.References > 1 -> desired <- desired.Add(query.Key,{value with References=value.References-1})
                            | Some _ ->
                                desired <- desired.Remove query.Key
                                retainedJournals <- retainedJournals.Remove query.Key
                            | None -> ())
                        refresh()
                        notify() }
        member _.Observe(callback: Snapshot -> unit) =
            let observer = SnapshotObserver.create Snapshot.revision callback
            let id,snapshot = lock gate (fun () ->
                check()
                let id=nextObserver
                nextObserver<-nextObserver+1
                observers<-observers.Add(id,observer)
                id,current)
            SnapshotObserver.publish observer snapshot
            { new IDisposable with
                member _.Dispose() =
                    SnapshotObserver.stop observer
                    lock gate (fun () -> observers<-observers.Remove id) }
        member _.Current = lock gate (fun () -> current)
        member _.QueryCount = lock gate (fun () -> desired.Count)
        member _.SubscriptionCount = lock gate (fun () -> desired |> Map.toSeq |> Seq.sumBy(fun (_,value) -> value.References))
        member _.ConnectionCount = lock gate (fun () -> connections)
        /// Session request identities are retained across reconnects. Only
        /// mutations are replayed, through Convex's own deduplicating protocol.
        member _.MutateAsync(reference:PublicMutationReference<'Args,'Result>, arguments:'Args, ?optimisticUpdate:Snapshot -> Snapshot) =
            match reference.Target with FunctionTarget.Named _ -> () | _ -> invalidArg "reference" "A public root mutation is required."
            let raw=reference.Arguments.Encode arguments |> Codec.decode<ConvexValue> |> ConvexJson.toJson
            use document=JsonDocument.Parse raw
            let pending=enqueue "Mutation" reference.Address (document.RootElement.Clone()) optimisticUpdate
            task {
                let! outcome=pending
                return match outcome with
                       | ConvexHttp.Completed value ->
                           try ConvexHttp.Completed(reference.Returns.Decode(ConvexWire.toNative(value.GetRawText())))
                           with _ -> uncertainty ConvexHttp.TransportCause.InvalidReply
                       | ConvexHttp.Refused failure -> ConvexHttp.Refused failure
                       | ConvexHttp.Uncertain failure -> ConvexHttp.Uncertain failure
            }
        member _.ActionAsync(reference:PublicActionReference<'Args,'Result>, arguments:'Args) =
            match reference.Target with FunctionTarget.Named _ -> () | _ -> invalidArg "reference" "A public root action is required."
            let raw=reference.Arguments.Encode arguments |> Codec.decode<ConvexValue> |> ConvexJson.toJson
            use document=JsonDocument.Parse raw
            let pending=enqueue "Action" reference.Address (document.RootElement.Clone()) None
            task {
                let! outcome=pending
                return match outcome with
                       | ConvexHttp.Completed value ->
                           try ConvexHttp.Completed(reference.Returns.Decode(ConvexWire.toNative(value.GetRawText())))
                           with _ -> uncertainty ConvexHttp.TransportCause.InvalidReply
                       | ConvexHttp.Refused failure -> ConvexHttp.Refused failure
                       | ConvexHttp.Uncertain failure -> ConvexHttp.Uncertain failure
            }
        member _.PendingMutations = lock gate (fun () -> requests |> Map.toSeq |> Seq.filter(fun(_,request)->request.Kind="Mutation") |> Seq.length)
        member _.PendingActions = lock gate (fun () -> requests |> Map.toSeq |> Seq.filter(fun(_,request)->request.Kind="Action") |> Seq.length)
        member _.SetAuthentication(value: string option) =
            value |> Option.iter(fun value -> if String.IsNullOrWhiteSpace value || value.Length>16384 || Seq.exists Char.IsControl value then invalidArg "value" "Invalid authentication token.")
            let removed,notification,cancellation = lock gate (fun () ->
                check()
                token<-value; epoch<-epoch+1; retainedJournals<-Map.empty
                // Clear only the previous identity's queue in the same critical
                // section as the identity switch. Newly enqueued calls must not
                // be cancelled by a later cleanup of the old identity.
                let removed=requests |> Map.toArray |> Array.map snd
                requests<-Map.empty
                baseSnapshot<-{baseSnapshot with Values=Map.empty;Stamp=None;Status=Connection.Connecting;Identity=baseSnapshot.Identity+1}
                removed,recomputeLocked(),connectionCancellation)
            cancellation |> Option.iter cancelConnection
            for request in removed do request.Completion.TrySetResult(cancelledRequest request) |> ignore
            announce notification
            notify()
        member _.Reconnect() =
            let cancellation=lock gate (fun () -> check();epoch<-epoch+1;connectionCancellation)
            cancellation |> Option.iter cancelConnection
            notify()
        member _.RunAsync(cancellation: CancellationToken) = task {
            lock gate (fun () ->
                check()
                if running then invalidOp "A shared query client has one owner loop."
                running <- true)
            use stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token)
            let mutable delay = 250
            try
                try
                    while not stop.IsCancellationRequested do
                        let requested,_,selectedEpoch = selected()
                        if requested.IsEmpty && lock gate (fun () -> requests.IsEmpty) then
                            publish selectedEpoch Connection.Stopped None Map.empty
                            do! changes.Reader.ReadAsync(stop.Token).AsTask()
                        else
                            try
                                do! connect stop.Token
                                delay<-250
                            with
                            | :? OperationCanceledException when stop.IsCancellationRequested -> ()
                            | :? OperationCanceledException ->
                                let _,_,nowEpoch=selected()
                                let snapshot=lock gate (fun()->current)
                                match snapshot.Status with
                                | Connection.Failed _ -> ()
                                | _ when nowEpoch=selectedEpoch ->
                                    publish selectedEpoch Connection.Reconnecting snapshot.Stamp snapshot.Values
                                    do! Task.Delay(delay,stop.Token)
                                    delay<-min 10000 (delay*2)
                                | _ -> ()
                            | :? IOException | :? WebSocketException ->
                                let snapshot=lock gate (fun()->current)
                                publish selectedEpoch Connection.Reconnecting snapshot.Stamp snapshot.Values
                                do! Task.Delay(delay,stop.Token)
                                delay<-min 10000 (delay*2)
                            | _ ->
                                failRequests (fun _ -> selectedEpoch=epoch) (fun request -> match request.Stage with Queued -> ConvexHttp.Refused ConvexHttp.InvocationRefusal.IncompatibleResponse | _ -> uncertainty ConvexHttp.TransportCause.InvalidReply)
                                publish selectedEpoch (Connection.Failed ConvexHttp.InvocationRefusal.IncompatibleResponse) None Map.empty
                        let failed=lock gate (fun()->match current.Status with Connection.Failed _ -> true | _ -> false)
                        if failed then do! changes.Reader.ReadAsync(stop.Token).AsTask()
                with :? OperationCanceledException when stop.IsCancellationRequested -> ()
            finally
                lock gate (fun()->running<-false)
                failRequests (fun _ -> true) cancelledRequest
                let _,_,selectedEpoch=selected()
                publish selectedEpoch Connection.Stopped None Map.empty
        }
        interface IDisposable with
            member _.Dispose() =
                let cleanup=lock gate (fun () ->
                    if projecting then invalidOp "An optimistic transformation cannot dispose the client."
                    if disposed then None else
                    let callbacks=observers |> Map.toArray |> Array.map snd
                    disposed<-true;desired<-Map.empty;observers<-Map.empty
                    Some(callbacks,connectionCancellation))
                match cleanup with
                | None -> ()
                | Some(callbacks,cancellation) ->
                    for callback in callbacks do SnapshotObserver.stop callback
                    cancelConnection lifetime
                    cancellation |> Option.iter cancelConnection
                    failRequests (fun _ -> true) cancelledRequest
