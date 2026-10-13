namespace Convex.Client

open System
open System.IO
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Convex.FSharp

/// Query-only Convex sync transport pinned to convex-js 1.46.0. It cannot
/// deliver mutations or actions, and a received snapshot never grants effects.
module ConvexSubscriptions =
    type Request = private { Endpoint: ConvexHttp.Endpoint; Path: string; Arguments: JsonElement }
    type Session = private { Id: Guid; mutable Connections: int; mutable MaximumTimestamp: uint64 }
    type Refusal = InvalidRequest | Unavailable | IncompatibleResponse | FunctionRefused | AuthorityDenied
    exception Rejected of Refusal
    let private reject reason = raise (Rejected reason)
    let private json value = JsonSerializer.SerializeToElement value
    let private field name (value: JsonElement) = value.GetProperty(name: string)
    let private kind value = (field "type" value).GetString()
    let private number maximum (value: JsonElement) =
        let mutable selected = 0
        if value.ValueKind <> JsonValueKind.Number || not (value.TryGetInt32(&selected)) || selected < 0 || selected > maximum then reject IncompatibleResponse
        selected
    let private objectValue (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then reject IncompatibleResponse
        let seen = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        for property in value.EnumerateObject() do
            if not (seen.Add property.Name) then reject IncompatibleResponse
        value
    let private timestamp (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.String then reject IncompatibleResponse
        let bytes = Convert.FromBase64String(value.GetString())
        if bytes.Length <> 8 || Convert.ToBase64String bytes <> value.GetString() then reject IncompatibleResponse
        Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian bytes
    let private encodedTimestamp value =
        let bytes = Array.zeroCreate<byte> 8
        Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, value)
        Convert.ToBase64String bytes
    let private state maximumIdentity value =
        let value = objectValue value
        number 1 (field "querySet" value), timestamp (field "ts" value), number maximumIdentity (field "identity" value)
    let request endpoint path (arguments: JsonElement) =
        if String.IsNullOrEmpty path || path.Length > 256
           || not (Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z0-9_/-]+:[A-Za-z0-9_]+$"))
           || arguments.ValueKind <> JsonValueKind.Object || Encoding.UTF8.GetByteCount(arguments.GetRawText()) > 128 * 1024 then reject InvalidRequest
        try ConvexHttp.validateWire arguments with _ -> reject InvalidRequest
        {Endpoint = endpoint; Path = path; Arguments = arguments.Clone()}
    let session () = {Id = Guid.NewGuid(); Connections = 0; MaximumTimestamp = 0UL}
    let path request = request.Path
    let private wireAddress request =
        let selected = UriBuilder(ConvexHttp.address request.Endpoint)
        selected.Scheme <- if selected.Scheme = "https" then "wss" else "ws"
        selected.Path <- "/api/1.46.0/sync"
        selected.Uri
    let private send (socket: WebSocket) (cancellation: CancellationToken) value = task {
        let bytes = JsonSerializer.SerializeToUtf8Bytes value
        if bytes.Length > 256 * 1024 then reject InvalidRequest
        do! socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellation)
    }
    let private receive (socket: WebSocket) (cancellation: CancellationToken) = task {
        use idle = CancellationTokenSource.CreateLinkedTokenSource cancellation
        idle.CancelAfter(TimeSpan.FromSeconds 60.)
        use output = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 8192
        let mutable finished = false
        while not finished do
            let! received = socket.ReceiveAsync(buffer.AsMemory(), idle.Token)
            if received.MessageType = WebSocketMessageType.Close then reject Unavailable
            if received.MessageType <> WebSocketMessageType.Text || output.Length + int64 received.Count > 2L * 1024L * 1024L then reject IncompatibleResponse
            output.Write(buffer, 0, received.Count)
            finished <- received.EndOfMessage
        return UTF8Encoding(false, true).GetString(output.ToArray())
    }
    let private parse (raw: string) =
        use document = JsonDocument.Parse(raw, JsonDocumentOptions(MaxDepth = 64))
        let value = objectValue document.RootElement
        // Validate nested duplicate names without treating protocol fields as
        // authority. Convex values themselves use the shared canonical codec.
        let rec duplicates depth (value: JsonElement) =
            if depth > 64 then reject IncompatibleResponse
            match value.ValueKind with
            | JsonValueKind.Object ->
                objectValue value |> ignore
                for property in value.EnumerateObject() do duplicates (depth + 1) property.Value
            | JsonValueKind.Array -> for item in value.EnumerateArray() do duplicates (depth + 1) item
            | _ -> ()
        duplicates 0 value
        value.Clone()
    let private subscribeWith token request session (cancellation: CancellationToken) (snapshot: JsonElement -> unit) (functionError: ConvexHttp.ApplicationError -> unit) = task {
        let maximumIdentity = if Option.isSome token then 1 else 0
        use socket = new ClientWebSocket()
        if not(OperatingSystem.IsBrowser()) then socket.Options.KeepAliveInterval <- TimeSpan.FromSeconds 15.
        use start = CancellationTokenSource.CreateLinkedTokenSource cancellation
        start.CancelAfter(TimeSpan.FromSeconds 20.)
        try
            try
                do! socket.ConnectAsync(wireAddress request, start.Token)
                let connection = session.Connections
                if connection >= Int32.MaxValue then reject InvalidRequest
                session.Connections <- connection + 1
                let message = Collections.Generic.Dictionary<string, obj>()
                message.Add("type", box "Connect")
                message.Add("sessionId", box (session.Id.ToString("D")))
                message.Add("connectionCount", box connection)
                message.Add("lastCloseReason", box (if connection = 0 then "InitialConnect" else "NativeQueryReconnect"))
                message.Add("clientTs", box Environment.TickCount64)
                if session.MaximumTimestamp <> 0UL then message.Add("maxObservedTimestamp", box (encodedTimestamp session.MaximumTimestamp))
                do! send socket start.Token message
                match token with
                | Some value -> do! send socket start.Token {| ``type`` = "Authenticate"; baseVersion = 0; tokenType = "User"; value = value |}
                | None -> ()
                do! send socket start.Token {| ``type`` = "ModifyQuerySet"; baseVersion = 0; newVersion = 1
                                               modifications = [|{|``type`` = "Add"; queryId = 0; udfPath = request.Path; args = [|request.Arguments|]|}|] |}
                let mutable current = 0, 0UL, 0
                let mutable chunkIdentity = ""
                let mutable chunkTotal = 0
                let mutable chunkPart = 0
                let chunks = StringBuilder()
                while not cancellation.IsCancellationRequested do
                    let! raw = receive socket cancellation
                    let mutable value = parse raw
                    if kind value = "TransitionChunk" then
                        let part = number 4095 (field "partNumber" value)
                        let total = number 4096 (field "totalParts" value)
                        let identity = (field "transitionId" value).GetString()
                        let chunk = (field "chunk" value).GetString()
                        if total = 0 || part >= total || String.IsNullOrEmpty identity || identity.Length > 128 || isNull chunk then reject IncompatibleResponse
                        if chunkPart = 0 then chunkIdentity <- identity; chunkTotal <- total
                        if identity <> chunkIdentity || total <> chunkTotal || part <> chunkPart
                           || int64 chunks.Length + int64 chunk.Length > 8L * 1024L * 1024L then reject IncompatibleResponse
                        chunks.Append chunk |> ignore
                        chunkPart <- chunkPart + 1
                        if chunkPart = chunkTotal then
                            value <- parse (chunks.ToString())
                            chunks.Clear() |> ignore
                            chunkPart <- 0
                            if kind value <> "Transition" then reject IncompatibleResponse
                        else value <- json {|``type`` = "BufferedChunk"|}
                    elif chunkPart <> 0 && kind value <> "Ping" then reject IncompatibleResponse
                    match kind value with
                    | "Ping" | "BufferedChunk" -> ()
                    | "FatalError" -> reject FunctionRefused
                    | "AuthError" -> reject AuthorityDenied
                    | "Transition" ->
                        if state maximumIdentity (field "startVersion" value) <> current then reject IncompatibleResponse
                        let next = state maximumIdentity (field "endVersion" value)
                        let querySet, stamp, identity = next
                        let previousQueries, previousStamp, previousIdentity = current
                        if querySet < previousQueries || stamp < previousStamp || identity < previousIdentity then reject IncompatibleResponse
                        let modifications = field "modifications" value
                        if modifications.ValueKind <> JsonValueKind.Array || modifications.GetArrayLength() > 64 then reject IncompatibleResponse
                        let updates = ResizeArray<Choice<JsonElement, ConvexHttp.ApplicationError>>()
                        for modification in modifications.EnumerateArray() do
                            if number 0 (field "queryId" modification) <> 0 then reject IncompatibleResponse
                            match kind modification with
                            | "QueryUpdated" ->
                                let result = (field "value" modification).Clone()
                                try ConvexHttp.validateWire result with _ -> reject IncompatibleResponse
                                updates.Add(Choice1Of2 result)
                            | "QueryFailed" -> updates.Add(Choice2Of2(ConvexHttp.applicationError modification))
                            | _ -> reject IncompatibleResponse
                        if maximumIdentity = 1 && identity <> 1 && updates.Count > 0 then reject IncompatibleResponse
                        current <- next
                        session.MaximumTimestamp <- max session.MaximumTimestamp stamp
                        for value in updates do
                            match value with
                            | Choice1Of2 snapshotValue -> snapshot snapshotValue
                            | Choice2Of2 error -> functionError error
                    | _ -> reject IncompatibleResponse
            with
            | :? OperationCanceledException when cancellation.IsCancellationRequested -> ()
            | Rejected reason -> reject reason
            | :? WebSocketException | :? OperationCanceledException | :? IOException -> reject Unavailable
            | :? JsonException | :? FormatException | :? InvalidOperationException | :? ArgumentException | :? Collections.Generic.KeyNotFoundException -> reject IncompatibleResponse
        finally socket.Abort()
    }

    let subscribe request session cancellation snapshot =
        subscribeWith None request session cancellation snapshot (fun _ -> reject FunctionRefused)

    /// One pinned human token per connection. The identity owner cancels and
    /// reconnects queries when a retained token rotates; effects are never sent.
    let subscribeAuthenticated request session (token: string) cancellation snapshot =
        if String.IsNullOrEmpty token || token.Length > 16384 || token |> Seq.exists Char.IsControl then reject InvalidRequest
        subscribeWith (Some token) request session cancellation snapshot (fun _ -> reject FunctionRefused)

    /// The same canonical query reference owns argument and snapshot decoding.
    /// Query errors are observable state; they carry their safe Convex error data.
    type TypedRequest<'Args, 'Result> = private {
        Raw: Request
        Reference: PublicQueryReference<'Args, 'Result>
    }

    let requestTyped endpoint (reference: PublicQueryReference<'Args, 'Result>) arguments =
        let encoded=reference.Arguments.Encode arguments |> Codec.decode<ConvexValue> |> ConvexJson.toJson
        use document=JsonDocument.Parse encoded
        { Raw=request endpoint reference.Address document.RootElement; Reference=reference }

    let private subscribeTypedWith token (request: TypedRequest<'Args, 'Result>) session cancellation (observe: ConvexHttp.InvocationOutcome<'Result> -> unit) = task {
        let received (value: JsonElement) =
            let outcome =
                try ConvexHttp.Completed(request.Reference.Returns.Decode(ConvexWire.toNative(value.GetRawText())))
                with _ -> ConvexHttp.Refused ConvexHttp.InvocationRefusal.IncompatibleResponse
            observe outcome
        let refused error = observe(ConvexHttp.Refused(ConvexHttp.InvocationRefusal.ApplicationError error))
        try
            do! subscribeWith token request.Raw session cancellation received refused
        with
        | Rejected reason ->
            let refusal =
                match reason with
                | InvalidRequest -> ConvexHttp.InvocationRefusal.InvalidRequest
                | AuthorityDenied -> ConvexHttp.InvocationRefusal.AuthorityDenied
                | IncompatibleResponse -> ConvexHttp.InvocationRefusal.IncompatibleResponse
                | Unavailable | FunctionRefused -> ConvexHttp.InvocationRefusal.Unavailable
            observe(ConvexHttp.Refused refusal)
    }

    let subscribeTyped request session cancellation observe =
        subscribeTypedWith None request session cancellation observe

    let subscribeAuthenticatedTyped request session (token: string) cancellation observe =
        if String.IsNullOrEmpty token || token.Length>16384 || (token |> Seq.exists Char.IsControl) then reject InvalidRequest
        subscribeTypedWith (Some token) request session cancellation observe
