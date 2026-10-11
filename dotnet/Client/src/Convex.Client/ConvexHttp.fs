namespace Convex.Client

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Convex.FSharp

/// One request is delivered once. Losing an effectful request's reply produces
/// Uncertain; only the application owner can reconcile and authorize redelivery.
module ConvexHttp =
    type Endpoint = private Endpoint of Uri
    type Authentication = Anonymous | Bearer of string | Admin of string
    type Operation = Query | Mutation | Action

    // The retained raw adapter is used only while its native callers migrate.
    type Refusal = InvalidRequest | AuthorityDenied | Unavailable | IncompatibleResponse | FunctionRefused
    exception Rejected of Refusal

    type ApplicationError = { Message: string; Data: ConvexValue option }

    [<RequireQualifiedAccess>]
    type InvocationRefusal =
        | InvalidRequest
        | AuthorityDenied
        | Unavailable
        | IncompatibleResponse
        | ApplicationError of ApplicationError

    [<RequireQualifiedAccess>]
    type TransportCause = Cancelled | DeadlineExceeded | ConnectionLost | HttpStatus of int | InvalidReply

    type TransportFailure = { Cause: TransportCause; RequestMayHaveReachedOwner: bool }

    /// The compatibility adapter must not turn lost effect delivery into a
    /// refusal or observation failure. Its owner retains the original attempt.
    exception DeliveryUncertain of TransportFailure

    type InvocationOutcome<'Result> =
        | Completed of 'Result
        | Refused of InvocationRefusal
        | Uncertain of TransportFailure

    /// A canonical decoded value and the exact bounded owner reply that supplied
    /// it. Retained review evidence must not be recreated with another serializer.
    type Reply<'Result> internal (value:'Result, wire:JsonElement) =
        let snapshot=wire.Clone()
        member _.Value=value
        member _.WireSnapshot=snapshot.Clone()

    type RequestOptions = {
        ConnectionTimeout: TimeSpan
        IdleTimeout: TimeSpan
        ExecutionTimeout: TimeSpan
        MaximumRequestBytes: int
        MaximumResponseBytes: int
    }

    let queryOptions = {
        ConnectionTimeout=TimeSpan.FromSeconds 20.
        IdleTimeout=TimeSpan.FromSeconds 30.
        ExecutionTimeout=TimeSpan.FromSeconds 60.
        MaximumRequestBytes=128*1024
        MaximumResponseBytes=2*1024*1024
    }
    let actionOptions = { queryOptions with ExecutionTimeout=TimeSpan.FromMinutes 11. }

    let private reject reason = raise (Rejected reason)
    let private validText maximum (value: string) =
        not(String.IsNullOrWhiteSpace value) && value.Length<=maximum && not(Seq.exists Char.IsControl value)

    let endpoint raw =
        if not(validText 2048 raw) || raw.Contains '\\' || Seq.exists Char.IsWhiteSpace raw then reject InvalidRequest
        let mutable address=Unchecked.defaultof<Uri>
        if not(Uri.TryCreate(raw, UriKind.Absolute, &address)) then reject InvalidRequest
        if address.UserInfo<>"" || address.Query<>"" || address.Fragment<>"" || address.AbsolutePath<>"/" then reject InvalidRequest
        let local=Regex.IsMatch(raw, @"^http://(?:127\.0\.0\.1|localhost|\[::1\]):[0-9]+/?$", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)
        if address.Scheme<>"https" && not(address.Scheme="http" && local) then reject InvalidRequest
        Endpoint address

    let address (Endpoint value) = value.AbsoluteUri.TrimEnd '/'

    let validateWire (value: JsonElement) =
        let rec fields depth (value: JsonElement) =
            if depth>64 then reject IncompatibleResponse
            match value.ValueKind with
            | JsonValueKind.Object ->
                let names=Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                for field in value.EnumerateObject() do
                    if not(names.Add field.Name) then reject IncompatibleResponse
                    fields (depth+1) field.Value
            | JsonValueKind.Array -> for item in value.EnumerateArray() do fields (depth+1) item
            | _ -> ()
        try
            fields 0 value
            ConvexWire.parseValue(value.GetRawText()) |> ignore
        with _ -> reject IncompatibleResponse

    let private optionsValid options =
        let boundedTime (value: TimeSpan) = value>TimeSpan.Zero && value<=TimeSpan.FromHours 1.
        boundedTime options.ConnectionTimeout && boundedTime options.IdleTimeout &&
        boundedTime options.ExecutionTimeout && options.MaximumRequestBytes>0 &&
        options.MaximumRequestBytes<=2*1024*1024 && options.MaximumResponseBytes>0 &&
        options.MaximumResponseBytes<=16*1024*1024

    let private readBody options (stream: Stream) (cancellation: CancellationToken) = task {
        use output=new MemoryStream()
        let buffer=Array.zeroCreate<byte> 8192
        let mutable reading=true
        while reading do
            use idle=CancellationTokenSource.CreateLinkedTokenSource cancellation
            idle.CancelAfter options.IdleTimeout
            let! count=stream.ReadAsync(buffer.AsMemory(), idle.Token)
            if count=0 then reading<-false
            else
                if output.Length+int64 count>int64 options.MaximumResponseBytes then reject IncompatibleResponse
                output.Write(buffer, 0, count)
        return output.ToArray()
    }

    let internal applicationError (value: JsonElement) =
        let message =
            match value.TryGetProperty "errorMessage" with
            | true, item when item.ValueKind=JsonValueKind.String && item.GetString().Length<=16384 ->
                item.GetString()
            | false, _ -> "Convex function refused"
            | _ -> reject IncompatibleResponse
        let data =
            match value.TryGetProperty "errorData" with
            | true, item ->
                validateWire item
                Some(ConvexWire.parseValue(item.GetRawText()))
            | _ -> None
        { Message=message; Data=data }

    let private unavailable operation cause =
        match operation, cause with
        | Query, TransportCause.InvalidReply -> Refused InvocationRefusal.IncompatibleResponse
        | Query, _ -> Refused InvocationRefusal.Unavailable
        | (Mutation | Action), _ -> Uncertain { Cause=cause; RequestMayHaveReachedOwner=true }

    let private executeAsync (Endpoint backend) authentication operation path (arguments: JsonElement) options (cancellation: CancellationToken) = task {
        let valid =
            validText 1024 path &&
            Regex.IsMatch(path, @"^[A-Za-z0-9_/-]+:[A-Za-z0-9_]+$", RegexOptions.CultureInvariant) &&
            arguments.ValueKind=JsonValueKind.Object && optionsValid options
        if not valid then return Refused InvocationRefusal.InvalidRequest
        else
            let mutable dispatched=false
            try
                validateWire arguments
                let category = match operation with Query -> "query" | Mutation -> "mutation" | Action -> "action"
                let payload=JsonSerializer.Serialize {| path=path; args=arguments; format="convex_encoded_json" |}
                if Encoding.UTF8.GetByteCount payload>options.MaximumRequestBytes then reject InvalidRequest
                use client =
                    if OperatingSystem.IsBrowser() then new HttpClient(Timeout=Timeout.InfiniteTimeSpan)
                    else
                        let handler=new SocketsHttpHandler(AllowAutoRedirect=false, UseCookies=false, ConnectTimeout=options.ConnectionTimeout)
                        new HttpClient(handler, true, Timeout=Timeout.InfiniteTimeSpan)
                use deadline=CancellationTokenSource.CreateLinkedTokenSource cancellation
                deadline.CancelAfter options.ExecutionTimeout
                use request=new HttpRequestMessage(System.Net.Http.HttpMethod.Post, Uri(backend, "/api/" + category))
                request.Headers.TryAddWithoutValidation("Convex-Client", "convex-dotnet-1") |> ignore
                let authorize scheme token =
                    if not(validText 65536 token) then reject InvalidRequest
                    request.Headers.Authorization<-AuthenticationHeaderValue(scheme, token)
                match authentication with
                | Anonymous -> ()
                | Bearer token -> authorize "Bearer" token
                | Admin token -> authorize "Convex" token
                request.Content<-new StringContent(payload, Encoding.UTF8, "application/json")
                cancellation.ThrowIfCancellationRequested()
                dispatched<-true
                let! response=client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                use response=response
                let status=int response.StatusCode
                if status=401 || status=403 then return Refused InvocationRefusal.AuthorityDenied
                elif not response.IsSuccessStatusCode && status<>560 then
                    return unavailable operation (TransportCause.HttpStatus status)
                else
                    let! input=response.Content.ReadAsStreamAsync(deadline.Token)
                    use input=input
                    let! bytes=readBody options input deadline.Token
                    use document=JsonDocument.Parse(ReadOnlyMemory<byte>(bytes), JsonDocumentOptions(MaxDepth=64))
                    let result=document.RootElement
                    if result.ValueKind<>JsonValueKind.Object then reject IncompatibleResponse
                    let names=Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                    for field in result.EnumerateObject() do
                        if not(names.Add field.Name) then reject IncompatibleResponse
                    match result.TryGetProperty "status" with
                    | true, state when state.ValueKind=JsonValueKind.String && state.GetString()="success" && response.IsSuccessStatusCode ->
                        match result.TryGetProperty "value" with
                        | true, value ->
                            validateWire value
                            return Completed(value.Clone())
                        | _ -> return unavailable operation TransportCause.InvalidReply
                    | true, state when state.ValueKind=JsonValueKind.String && state.GetString()="error" ->
                        return Refused(InvocationRefusal.ApplicationError(applicationError result))
                    | _ -> return unavailable operation TransportCause.InvalidReply
            with
            | Rejected InvalidRequest -> return Refused InvocationRefusal.InvalidRequest
            | Rejected _ | :? JsonException | :? FormatException ->
                return if dispatched then unavailable operation TransportCause.InvalidReply else Refused InvocationRefusal.InvalidRequest
            | :? OperationCanceledException ->
                let cause=if cancellation.IsCancellationRequested then TransportCause.Cancelled else TransportCause.DeadlineExceeded
                if dispatched then return unavailable operation cause
                else return Refused InvocationRefusal.Unavailable
            | :? HttpRequestException | :? IOException ->
                return if dispatched then unavailable operation TransportCause.ConnectionLost else Refused InvocationRefusal.Unavailable
            | :? InvalidOperationException | :? ArgumentException ->
                return if dispatched then unavailable operation TransportCause.InvalidReply else Refused InvocationRefusal.InvalidRequest
    }

    let private typedSnapshotAsync operation endpoint authentication options cancellation
        (reference: FunctionReference<'Kind, 'Visibility, 'Args, 'Result>) arguments = task {
        try
            let encoded=reference.Arguments.Encode arguments |> Codec.decode<ConvexValue> |> ConvexJson.toJson
            use document=JsonDocument.Parse encoded
            let! outcome=executeAsync endpoint authentication operation reference.Address document.RootElement options cancellation
            match outcome with
            | Completed value ->
                try return Completed(Reply(reference.Returns.Decode(ConvexWire.toNative(value.GetRawText())), value))
                with _ -> return if operation=Query then Refused InvocationRefusal.IncompatibleResponse else Uncertain { Cause=TransportCause.InvalidReply; RequestMayHaveReachedOwner=true }
            | Refused reason -> return Refused reason
            | Uncertain failure -> return Uncertain failure
        with
        | :? ArgumentException | :? InvalidOperationException | :? JsonException -> return Refused InvocationRefusal.InvalidRequest
    }

    let private typedAsync operation endpoint authentication options cancellation reference arguments = task {
        let! outcome=typedSnapshotAsync operation endpoint authentication options cancellation reference arguments
        return
            match outcome with
            | Completed reply -> Completed reply.Value
            | Refused reason -> Refused reason
            | Uncertain failure -> Uncertain failure
    }

    let querySnapshotAsync endpoint authentication (reference:PublicQueryReference<'Args,'Result>) arguments =
        typedSnapshotAsync Query endpoint authentication queryOptions CancellationToken.None reference arguments
    let mutateSnapshotAsync endpoint authentication (reference:PublicMutationReference<'Args,'Result>) arguments =
        typedSnapshotAsync Mutation endpoint authentication queryOptions CancellationToken.None reference arguments
    let querySnapshotWithOptionsAsync endpoint authentication options cancellation (reference:PublicQueryReference<'Args,'Result>) arguments =
        typedSnapshotAsync Query endpoint authentication options cancellation reference arguments
    let mutateSnapshotWithOptionsAsync endpoint authentication options cancellation (reference:PublicMutationReference<'Args,'Result>) arguments =
        typedSnapshotAsync Mutation endpoint authentication options cancellation reference arguments
    let querySnapshot endpoint authentication reference arguments =
        (querySnapshotAsync endpoint authentication reference arguments).GetAwaiter().GetResult()
    let mutateSnapshot endpoint authentication reference arguments =
        (mutateSnapshotAsync endpoint authentication reference arguments).GetAwaiter().GetResult()

    let queryWithOptionsAsync endpoint authentication options cancellation (reference: PublicQueryReference<'Args, 'Result>) arguments =
        typedAsync Query endpoint authentication options cancellation reference arguments
    let mutateWithOptionsAsync endpoint authentication options cancellation (reference: PublicMutationReference<'Args, 'Result>) arguments =
        typedAsync Mutation endpoint authentication options cancellation reference arguments
    let actWithOptionsAsync endpoint authentication options cancellation (reference: PublicActionReference<'Args, 'Result>) arguments =
        typedAsync Action endpoint authentication options cancellation reference arguments
    let queryAsync endpoint authentication reference arguments =
        queryWithOptionsAsync endpoint authentication queryOptions CancellationToken.None reference arguments
    let mutateAsync endpoint authentication reference arguments =
        mutateWithOptionsAsync endpoint authentication queryOptions CancellationToken.None reference arguments
    let actAsync endpoint authentication reference arguments =
        actWithOptionsAsync endpoint authentication actionOptions CancellationToken.None reference arguments

    type Administrator = private Administrator of string
    let administrator token =
        if not(validText 65536 token) then reject InvalidRequest
        Administrator token

    let queryInternalAsync endpoint (Administrator token) (reference: InternalQueryReference<'Args, 'Result>) arguments =
        typedAsync Query endpoint (Admin token) queryOptions CancellationToken.None reference arguments
    let mutateInternalAsync endpoint (Administrator token) (reference: InternalMutationReference<'Args, 'Result>) arguments =
        typedAsync Mutation endpoint (Admin token) queryOptions CancellationToken.None reference arguments
    let actInternalAsync endpoint (Administrator token) (reference: InternalActionReference<'Args, 'Result>) arguments =
        typedAsync Action endpoint (Admin token) actionOptions CancellationToken.None reference arguments

    let query endpoint authentication reference arguments = (queryAsync endpoint authentication reference arguments).GetAwaiter().GetResult()
    let mutate endpoint authentication reference arguments = (mutateAsync endpoint authentication reference arguments).GetAwaiter().GetResult()
    let act endpoint authentication reference arguments = (actAsync endpoint authentication reference arguments).GetAwaiter().GetResult()

    /// Existing native transport edges retain their exact raw API during caller
    /// migration. New application code uses kind-specific references and outcomes.
    let execute endpoint authentication operation path arguments =
        let options=if operation=Action then actionOptions else queryOptions
        match (executeAsync endpoint authentication operation path arguments options CancellationToken.None).GetAwaiter().GetResult() with
        | Completed value -> value
        | Uncertain failure -> raise (DeliveryUncertain failure)
        | Refused reason ->
            reject (match reason with
                    | InvocationRefusal.InvalidRequest -> InvalidRequest
                    | InvocationRefusal.AuthorityDenied -> AuthorityDenied
                    | InvocationRefusal.Unavailable -> Unavailable
                    | InvocationRefusal.IncompatibleResponse -> IncompatibleResponse
                    | InvocationRefusal.ApplicationError _ -> FunctionRefused)
