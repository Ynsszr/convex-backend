namespace Convex.DotNet.Development

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading
open Convex.DotNet.Deployment

/// Native commands over the original Convex backup/import owners. A write is
/// dispatched once; timeout or loss of response never authorizes repeating it.
module DataOperations =
    let private maximumBytes=2L*1024L*1024L*1024L
    let private noLinks path =
        let mutable current=Path.GetFullPath path
        while not(String.IsNullOrEmpty current) do
            let entry:FileSystemInfo=if Directory.Exists current then DirectoryInfo current else FileInfo current
            if entry.LinkTarget<>null then invalidArg "path" "Backup paths cannot traverse symbolic links."
            current<-Path.GetDirectoryName current
    let private source path =
        let path=Path.GetFullPath path
        noLinks path
        let info=FileInfo path
        if not info.Exists || info.Length<=0L || info.Length>maximumBytes then
            invalidArg "path" "Select a nonempty regular import file no larger than 2 GiB."
        path
    let private origin value =
        let endpoint=Uri(value:string)
        NativeDeployment.validateOrigin endpoint
        endpoint
    let private administrator variable =
        let value=Operations.variable variable
        if String.IsNullOrWhiteSpace value || value.Length>65536 || Seq.exists Char.IsControl value then invalidArg "credential" "Select a valid deployment credential."
        value
    let private transport originValue variable (method:HttpMethod) (path:string) content consume =
        let endpoint=origin originValue
        let key=administrator variable
        use handler=new HttpClientHandler(AllowAutoRedirect=false,UseCookies=false)
        use client=new HttpClient(handler,Timeout=TimeSpan.FromMinutes 5.)
        use deadline=new CancellationTokenSource(TimeSpan.FromMinutes 5.)
        use request=new HttpRequestMessage(method,Uri(endpoint,path))
        request.Headers.Add("Authorization","Convex "+key)
        content |> Option.iter(fun (value:HttpContent)->request.Content<-value)
        try
            use response=client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token).GetAwaiter().GetResult()
            if not response.IsSuccessStatusCode then
                let code=int response.StatusCode
                eprintfn "Backup/import owner returned HTTP %d; the request was not retried." code
                if code>=500 || code=408 then 3 else 2
            else
                consume response deadline.Token
                0
        with :? HttpRequestException | :? OperationCanceledException | :? IOException | :? JsonException | :? InvalidOperationException ->
            eprintfn "Backup/import outcome is uncertain. The request was not automatically repeated."
            3
    let tables origin credential =
        match Operations.queryData origin credential "_system/cli/tables:default" (JsonSerializer.SerializeToElement {|paginationOpts={|numItems=10000;cursor=(null:string)|}|}) with
        | Error code -> code
        | Ok value -> printfn "%s" (value.GetProperty("page").GetRawText());0
    let importFile originValue format table path mode credential =
        origin originValue |> ignore
        if not(List.contains format ["zip";"jsonArray";"jsonLines";"csv"]) then invalidArg "format" "Select zip, jsonArray, jsonLines or csv."
        if not(List.contains mode ["requireEmpty";"append";"replace"]) then invalidArg "mode" "Explicitly select requireEmpty, append or replace."
        if format="zip" then
            if table<>"-" then invalidArg "table" "A ZIP import restores its own table names; use '-'."
        elif String.IsNullOrWhiteSpace table || not(Text.RegularExpressions.Regex.IsMatch(table,"^[A-Za-z][A-Za-z0-9_]*$")) then
            invalidArg "table" "Select a valid table name."
        let path=source path
        let endpoint="/api/import?format="+format+"&mode="+mode+(if format="zip" then "" else "&tableName="+Uri.EscapeDataString table)
        use stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)
        use body=new StreamContent(stream)
        body.Headers.ContentType<-Headers.MediaTypeHeaderValue("application/octet-stream")
        transport originValue credential HttpMethod.Post endpoint (Some body) (fun response token ->
            use input=response.Content.ReadAsStreamAsync(token).GetAwaiter().GetResult()
            use bytes=new MemoryStream()
            let buffer=Array.zeroCreate<byte> 8192
            let mutable reading=true
            while reading do
                let count=input.ReadAsync(buffer.AsMemory(),token).GetAwaiter().GetResult()
                if count=0 then reading<-false else
                if bytes.Length+int64 count>65536L then invalidOp "Import receipt exceeded its bound."
                bytes.Write(buffer,0,count)
            use document=JsonDocument.Parse(bytes.ToArray(),JsonDocumentOptions(MaxDepth=16))
            let mutable count=0UL
            let found,value=document.RootElement.TryGetProperty "numWritten"
            if not found || value.ValueKind<>JsonValueKind.Number || not(value.TryGetUInt64(&count)) then
                invalidOp "Import response omitted its owning write count."
            printfn "%s" (document.RootElement.GetRawText()))
    let private latest origin credential =
        Operations.queryData origin credential "_system/cli/exports:getLatest" (JsonSerializer.SerializeToElement {| |})
    let private timestamp (value:JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Number -> value.GetUInt64().ToString(Globalization.CultureInfo.InvariantCulture)
        | JsonValueKind.Object ->
            let encoded=value.GetProperty("$integer").GetString()
            let bytes=Convert.FromBase64String encoded
            if bytes.Length<>8 || Convert.ToBase64String bytes<>encoded then invalidOp "Invalid snapshot timestamp."
            Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes).ToString(Globalization.CultureInfo.InvariantCulture)
        | _ -> invalidOp "Invalid snapshot timestamp."
    let export originValue destination credential includeStorage =
        origin originValue |> ignore
        let destination=Path.GetFullPath destination
        noLinks destination
        if File.Exists destination || Directory.Exists destination then invalidArg "destination" "Choose a new snapshot file; existing files are never overwritten."
        let previous=
            match latest originValue credential with
            | Error code -> Error code
            | Ok value when value.ValueKind=JsonValueKind.Null -> Ok None
            | Ok value -> Ok(Some(value.GetProperty("_id").GetString()))
        match previous with
        | Error code -> code
        | Ok previous ->
            match Operations.request originValue credential HttpMethod.Post ("/api/export/request/zip?includeStorage="+(if includeStorage then "true" else "false")) None with
            | Error code -> code
            | Ok _ ->
                let until=DateTime.UtcNow.AddMinutes 3.
                let mutable selected=None
                let mutable ready=None
                let mutable failed=0
                while ready.IsNone && failed=0 && DateTime.UtcNow<until do
                    match latest originValue credential with
                    | Error code -> failed<-code
                    | Ok value when value.ValueKind=JsonValueKind.Null -> ()
                    | Ok value ->
                        let id=value.GetProperty("_id").GetString()
                        if Some id<>previous then
                            match selected with
                            | Some selected when selected<>id -> failed<-3
                            | _ ->
                                selected<-Some id
                                match value.GetProperty("state").GetString() with
                                | "completed" -> ready<-Some(timestamp(value.GetProperty "start_ts"))
                                | "failed" | "canceled" -> failed<-2
                                | "requested" | "in_progress" -> ()
                                | _ -> failed<-3
                    if ready.IsNone && failed=0 then Thread.Sleep 100
                match ready with
                | None -> eprintfn "Snapshot did not complete in this command; its creation was not repeated.";if failed=0 then 3 else failed
                | Some stamp ->
                    Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
                    let temporary=destination+".partial-"+Guid.NewGuid().ToString("N")
                    try
                        let result=transport originValue credential HttpMethod.Get ("/api/export/zip/"+stamp) None (fun response token ->
                            use source=response.Content.ReadAsStreamAsync(token).GetAwaiter().GetResult()
                            use target=new FileStream(temporary,FileStreamOptions(Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,
                                UnixCreateMode=Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)))
                            let buffer=Array.zeroCreate<byte> 65536
                            let mutable reading=true
                            while reading do
                                let count=source.ReadAsync(buffer.AsMemory(),token).GetAwaiter().GetResult()
                                if count=0 then reading<-false else
                                if target.Length+int64 count>maximumBytes then invalidOp "Snapshot exceeds the 2 GiB command bound."
                                target.Write(buffer,0,count)
                            target.Flush true)
                        if result=0 then
                            File.Move(temporary,destination,false)
                            printfn "Snapshot downloaded: %s" destination
                        result
                    finally if File.Exists temporary then File.Delete temporary
