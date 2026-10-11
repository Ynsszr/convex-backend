namespace Convex.FSharp

open System

type HttpResponse =
    { Status:int
      Headers:(string*string) list
      Body:string
      BinaryBody:byte[] option }

type NativeHttpStreamingResponse =
    { Status: int
      Headers: (string * string) list
      Body: INativeByteSource }

[<RequireQualifiedAccess>]
type NativeHttpResponse =
    | Buffered of HttpResponse
    | Streamed of NativeHttpStreamingResponse

type IHttpRequest =
    abstract Method:string
    abstract Url:string
    abstract Headers:(string*string) list
    abstract Header:name:string -> string option
    abstract Text:unit -> Async<string>
    abstract JsonRaw:unit -> Async<obj>
    abstract Bytes:unit -> Async<byte[]>
    abstract Raw:obj

/// Native requests expose owning body-consumption state without pretending to
/// be a JavaScript Web Request. Clones have independent consumption state.
type INativeHttpRequestState =
    abstract BodyUsed: bool
    abstract Clone: unit -> IHttpRequest

module HttpRequest =
    let adapt (request:obj) : IHttpRequest =
        match request with
        | :? IHttpRequest as native -> native
        | _ -> invalidArg "request" "A native HTTP execution request is required"

[<AutoOpen>]
module HttpRequestExtensions =
    type IHttpRequest with
        member inline this.Json<'T>() : Async<'T> = async {
            let! raw=this.JsonRaw()
            return Codec.decode<'T> raw }
        member inline this.ConvexJson<'T>() : Async<'T> = async {
            let! text=this.Text()
            return ConvexWire.decode<'T> text }
        member this.BodyUsed =
            match this with
            | :? INativeHttpRequestState as request -> request.BodyUsed
            | _ -> invalidOp "HTTP body state requires an owning request adapter"
        member this.Clone() =
            match this with
            | :? INativeHttpRequestState as request -> request.Clone()
            | _ -> invalidOp "HTTP cloning requires an owning request adapter"

[<RequireQualifiedAccess>]
module Http =
    let response status body headers : HttpResponse = {Status=status;Headers=headers;Body=body;BinaryBody=None}
    let ok body = response 200 body ["Content-Type","text/plain; charset=utf-8"]
    let notFound body = response 404 body ["Content-Type","text/plain; charset=utf-8"]
    let badRequest body = response 400 body ["Content-Type","text/plain; charset=utf-8"]
    let inline json (data:'T) : HttpResponse =
        response 200 (HttpJson.encode data) ["Content-Type","application/json"]
    let inline convexJson (data:'T) : HttpResponse =
        response 200 (ConvexWire.encode data) ["Content-Type","application/json"]
    let bytes data contentType : HttpResponse =
        {Status=200;Headers=["Content-Type",contentType];Body="";BinaryBody=Some data}
    let noContent : HttpResponse = {Status=204;Headers=[];Body="";BinaryBody=None}
    let withStatus status (response:HttpResponse) = {response with Status=status}
    let withHeader name value (response:HttpResponse) = {response with Headers=response.Headers @ [name,value]}
    let redirect status location =
        if not(List.contains status [301;302;303;307;308]) then invalidArg "status" "A redirect requires a redirect status"
        response status "" ["Location",location]

    let validate (response:HttpResponse) =
        if response.Status<200 || response.Status>599 then invalidArg "response" "A Web Response status must be between 200 and 599"
        let token c = (c>='a' && c<='z') || (c>='A' && c<='Z') || (c>='0' && c<='9') || "!#$%&'*+-.^_`|~".Contains c
        for name,value in response.Headers do
            if String.IsNullOrEmpty name || isNull value ||
               (name |> Seq.exists(fun c -> not(token c))) ||
               (value |> Seq.exists(fun c -> c='\r' || c='\n' || int c=0)) then
                invalidArg "response" "Invalid HTTP header"
        if List.contains response.Status [204;205;304] && (not(String.IsNullOrEmpty response.Body) || (response.BinaryBody |> Option.exists(fun bytes -> bytes.Length>0))) then
            invalidArg "response" "This HTTP status cannot carry a response body"
        response

    /// The backend owns the response stream and disconnect cancellation.
    /// The producer is pulled only after each previous chunk is admitted.
    let stream status headers (body: INativeByteSource) : NativeHttpStreamingResponse =
        response status "" headers |> validate |> ignore
        if List.contains status [204;205;304] then
            invalidArg "status" "A body-free response uses the buffered response contract"
        if isNull (box body) then invalidArg "body" "A native response source is required"
        { Status=status; Headers=headers; Body=body }

    let toNativeResponse (response:HttpResponse) = validate response
    /// Retained authored API name; native handlers receive the checked response.
    let toWebResponse (response:HttpResponse) : obj = toNativeResponse response |> box
