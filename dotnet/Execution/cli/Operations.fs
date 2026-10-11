namespace Convex.DotNet.Development

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open Convex.DotNet.Deployment

/// Thin native commands over Convex's existing administrative APIs. Credentials
/// and environment values are selected by variable name, never logged as args.
module Operations =
    let variable name =
        if String.IsNullOrWhiteSpace name || not(Regex.IsMatch(name,@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")) then
            invalidArg "variable" "Select a valid environment variable name."
        let value=Environment.GetEnvironmentVariable name
        if isNull value then invalidArg "variable" "The selected environment variable is not set."
        value
    let private origin value =
        let result=Uri(value: string)
        NativeDeployment.validateOrigin result
        result
    let private name (value:string) =
        if not(Regex.IsMatch(value,@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")) then invalidArg "name" "Invalid environment variable name."
        value
    let internal request originValue credential (method:HttpMethod) (path:string) (body:string option) : Result<JsonElement option,int> =
        let endpoint=origin originValue
        let administrator=variable credential
        if String.IsNullOrWhiteSpace administrator || administrator.Length>65536 || Seq.exists Char.IsControl administrator then
            invalidArg "credential" "A bounded deployment credential is required."
        use handler=new HttpClientHandler(AllowAutoRedirect=false,UseCookies=false)
        use client=new HttpClient(handler,Timeout=TimeSpan.FromSeconds 65.)
        use message=new HttpRequestMessage(method,Uri(endpoint,path))
        message.Headers.Add("Authorization","Convex "+administrator)
        body |> Option.iter(fun value -> message.Content<-new StringContent(value,Encoding.UTF8,"application/json"))
        use deadline=new CancellationTokenSource(TimeSpan.FromSeconds 65.)
        try
            use response=client.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,deadline.Token).GetAwaiter().GetResult()
            if not response.IsSuccessStatusCode then
                let code=int response.StatusCode
                if code>=500 || code=408 then
                    eprintfn "Backend returned HTTP %d; the operation was not retried and its outcome may be uncertain." code
                    Error 3
                else
                    eprintfn "Owning backend refused the operation (HTTP %d)." code
                    Error 2
            else
                use stream=response.Content.ReadAsStreamAsync(deadline.Token).GetAwaiter().GetResult()
                use bytes=new MemoryStream()
                let buffer=Array.zeroCreate<byte> 16384
                let mutable reading=true
                while reading do
                    let count=stream.ReadAsync(buffer.AsMemory(),deadline.Token).GetAwaiter().GetResult()
                    if count=0 then reading<-false else
                    if bytes.Length+int64 count>8L*1024L*1024L then invalidOp "Backend response exceeded the command's bound."
                    bytes.Write(buffer,0,count)
                let text=Encoding.UTF8.GetString(bytes.ToArray())
                if String.IsNullOrWhiteSpace text then Ok None else
                use json=JsonDocument.Parse(text,JsonDocumentOptions(MaxDepth=64))
                Ok(Some(json.RootElement.Clone()))
        with :? HttpRequestException | :? OperationCanceledException | :? IOException | :? JsonException | :? InvalidOperationException ->
            eprintfn "The operation's outcome is uncertain; it was not automatically retried."
            Error 3
    let private query origin credential path arguments =
        request origin credential HttpMethod.Post "/api/query" (Some(JsonSerializer.Serialize {|path=path;args=arguments;format="convex_encoded_json"|}))
    let private response (value:Result<JsonElement option,int>) =
        match value with
        | Error code -> code
        | Ok None -> printfn "confirmed";0
        | Ok(Some json) ->
            printfn "%s" (json.GetRawText())
            match json.TryGetProperty "status" with
            | true,status when status.ValueKind=JsonValueKind.String && status.GetString()="error" -> 2
            | _ -> 0
    let private queryValue (response:Result<JsonElement option,int>) =
        match response with
        | Error code -> Error code
        | Ok(Some json) when json.GetProperty("status").GetString()="success" -> Ok(json.GetProperty("value").Clone())
        | _ -> Error 2
    let environmentList origin credential =
        match query origin credential "_system/cli/queryEnvironmentVariables:default" (JsonSerializer.SerializeToElement(Map.empty<string,string>)) |> queryValue with
        | Error code -> code
        | Ok values ->
            let names=values.EnumerateArray() |> Seq.map(fun item -> item.GetProperty("name").GetString()) |> Seq.sort |> Seq.toArray
            printfn "%s" (JsonSerializer.Serialize names)
            0
    let environmentSet origin key valueVariable credential =
        let key=name key
        let value=variable valueVariable
        if Encoding.UTF8.GetByteCount value>65536 then invalidArg "value" "Environment value exceeds the command's bound."
        request origin credential HttpMethod.Post "/api/update_environment_variables" (Some(JsonSerializer.Serialize {|changes=[|{|name=key;value=value|}|]|})) |> response
    let environmentRemove origin key credential =
        request origin credential HttpMethod.Post "/api/update_environment_variables" (Some(JsonSerializer.Serialize {|changes=[|{|name=name key;value=(null:string)|}|]|})) |> response
    let runFunction origin kind address argumentFile credential =
        if not(List.contains kind ["query";"mutation";"action"]) then invalidArg "kind" "Select query, mutation or action."
        if String.IsNullOrWhiteSpace address || address.Length>1024 || not(Regex.IsMatch(address,@"^[A-Za-z0-9_/-]+:[A-Za-z0-9_]+$")) then
            invalidArg "function" "Select an exact module:function address."
        let input=FileInfo argumentFile
        if not input.Exists || input.Length>128L*1024L || input.LinkTarget<>null then invalidArg "arguments" "Select a bounded regular JSON argument file."
        use document=JsonDocument.Parse(File.ReadAllBytes input.FullName,JsonDocumentOptions(MaxDepth=64))
        if document.RootElement.ValueKind<>JsonValueKind.Object then invalidArg "arguments" "Function arguments must be an object."
        request origin credential HttpMethod.Post ("/api/"+kind) (Some(JsonSerializer.Serialize {|path=address;args=document.RootElement;format="convex_encoded_json"|})) |> response
    let logs origin (cursor:string) credential =
        let mutable parsed=0.
        if not(Double.TryParse(cursor,Globalization.NumberStyles.Float,Globalization.CultureInfo.InvariantCulture,&parsed)) || not(Double.IsFinite parsed) || parsed<0. then
            invalidArg "cursor" "Select a nonnegative numeric log cursor."
        request origin credential HttpMethod.Get ("/api/stream_function_logs?cursor="+Uri.EscapeDataString cursor) None |> response

    let internal queryData origin credential path arguments =
        query origin credential path arguments |> queryValue
