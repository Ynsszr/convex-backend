namespace Convex.FSharp.NativeExecution

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Convex.FSharp

module internal NativeHttpRequests =
    let maximumBody=20*1024*1024
    let maximumResponse=8*1024*1024

    // Web Request JSON is ordinary JSON. Its tagged-looking objects must not
    // be mistaken for the separate lossless Convex wire representation.
    let private ordinaryJson text =
        use document=JsonDocument.Parse(text:string)
        let rec value (element:JsonElement) : obj =
            match element.ValueKind with
            | JsonValueKind.Null -> null
            | JsonValueKind.True -> box true
            | JsonValueKind.False -> box false
            | JsonValueKind.String -> box (element.GetString())
            | JsonValueKind.Number -> box (element.GetDouble())
            | JsonValueKind.Array -> element.EnumerateArray() |> Seq.map value |> Seq.toArray |> box
            | JsonValueKind.Object ->
                let result=Collections.Generic.Dictionary<string,obj>(StringComparer.Ordinal)
                for field in element.EnumerateObject() do result[field.Name]<-value field.Value
                box result
            | _ -> invalidOp "Unsupported HTTP JSON value"
        value document.RootElement

    let private readBody (host:ITransactionHostOperations) = async {
        use body=new MemoryStream()
        let mutable finished=false
        while not finished do
            let! part=host.Call("dotnet/httpReadBody",HostArguments.empty(),true)
            let bytes=HostArguments.field "bytes" part |> Codec.decode<byte[]>
            finished<-HostArguments.field "done" part |> Codec.decode<bool>
            if bytes.Length>65536 || (bytes.Length=0 && not finished) then invalidOp "Invalid HTTP body chunk"
            if body.Length+int64 bytes.Length>int64 maximumBody then invalidOp "The HTTP request body exceeds its owning limit"
            body.Write bytes
        return body.ToArray()
    }

    type private Request(method:string,url:string,headers:(string*string) list,body:Lazy<Threading.Tasks.Task<byte[]>>) as this =
        let mutable used=0
        let consume () = async {
            // Fetch requests cannot carry bodies for GET or HEAD. Reading
            // their empty body neither consumes it nor calls the owner stream.
            if method="GET" || method="HEAD" then return Array.empty<byte>
            else
                if Interlocked.Exchange(&used,1)<>0 then invalidOp "The HTTP request body has already been consumed"
                let! bytes=body.Value |> Async.AwaitTask
                return Array.copy bytes
        }
        interface IHttpRequest with
            member _.Method=method
            member _.Url=url
            member _.Headers=headers
            member _.Header name=
                Http.response 200 "" [name,""] |> Http.validate |> ignore
                let values=headers |> List.choose(fun (key,value) -> if String.Equals(key,name,StringComparison.OrdinalIgnoreCase) then Some value else None)
                if values.IsEmpty then None else Some (String.concat ", " values)
            member _.Bytes()=consume()
            member _.Text()=async { let! bytes=consume() in return Encoding.UTF8.GetString bytes }
            member _.JsonRaw()=async { let! bytes=consume() in return Encoding.UTF8.GetString bytes |> ordinaryJson }
            member _.Raw=box this
        interface INativeHttpRequestState with
            member _.BodyUsed=Volatile.Read(&used)<>0
            member _.Clone()=
                if Volatile.Read(&used)<>0 then invalidOp "A consumed HTTP request cannot be cloned"
                Request(method,url,headers,body) :> IHttpRequest

    let create (host:ITransactionHostOperations) (head:JsonElement) : IHttpRequest =
        let text name (value:JsonElement) =
            let field=value.GetProperty(name:string)
            if field.ValueKind<>JsonValueKind.String then invalidOp "Invalid HTTP request metadata"
            field.GetString()
        let method=text "method" head
        let url=text "url" head
        let fields=head.GetProperty "headers"
        if fields.ValueKind<>JsonValueKind.Array then invalidOp "HTTP request headers must be an array"
        let headers=fields.EnumerateArray() |> Seq.map(fun field -> text "name" field,text "value" field) |> Seq.toList
        let body=lazy (readBody host |> Async.StartAsTask)
        Request(method,url,headers,body) :> IHttpRequest

    let response (value:HttpResponse) =
        let value=Http.validate value
        let bytes=
            match value.BinaryBody with
            | Some bytes when not(isNull bytes) -> bytes
            | _ when not(isNull value.Body) -> Encoding.UTF8.GetBytes value.Body
            | _ -> [||]
        if bytes.Length>maximumResponse then invalidOp "The buffered native HTTP response exceeds 8 MiB"
        let headers=value.Headers |> List.map(fun (name,value) -> HostArguments.objectValue ["name",box name;"value",box value]) |> List.toArray
        HostArguments.objectValue ["status",box value.Status;"headers",box headers;"body",box bytes]

    let sendResponse (host:ITransactionHostOperations) method (value:NativeHttpResponse) = async {
        match value with
        | NativeHttpResponse.Buffered value -> return response value
        | NativeHttpResponse.Streamed value ->
            let value=Http.stream value.Status value.Headers value.Body
            return! AsyncResource.usingAsync (fun () -> value.Body) (fun source -> source.Close()) (fun source -> async {
                let headers=value.Headers |> List.map(fun (name,value) -> HostArguments.objectValue ["name",box name;"value",box value]) |> List.toArray
                let! _=host.Call("dotnet/httpResponseHead",HostArguments.objectValue ["status",box value.Status;"headers",box headers],true)
                if method<>"HEAD" then
                    let mutable finished=false
                    let mutable count=0L
                    while not finished do
                        let! chunk=source.Read()
                        match chunk with
                        | None -> finished<-true
                        | Some bytes ->
                            if isNull bytes || bytes.Length=0 || bytes.Length>65536 then
                                invalidOp "A native response chunk must contain between 1 and 65536 bytes"
                            count<-count+int64 bytes.Length
                            if count>int64 maximumBody then invalidOp "The native HTTP response exceeds the owning 20 MiB limit"
                            let! _=host.Call("dotnet/httpResponseChunk",HostArguments.objectValue ["bytes",box (Array.copy bytes)],true)
                            ()
                return HostArguments.objectValue ["streamed",box true]
            })
    }
