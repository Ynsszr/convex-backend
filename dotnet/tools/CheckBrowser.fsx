open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Net.WebSockets
open System.Diagnostics
open System.Text.Json
open System.Threading
open System.Threading.Tasks

let main () =
    let arguments=fsi.CommandLineArgs |> Array.skip 1
    if arguments.Length<>2 then invalidArg "arguments" "Expected LOOPBACK_WEB_URL NEW_RESULT_DIRECTORY"
    let url=Uri arguments[0]
    if not url.IsLoopback || url.Scheme<>"http" then invalidArg "url" "This browser check only targets an isolated loopback application."
    let root=Path.GetFullPath arguments[1]
    Directory.CreateDirectory root |> ignore
    use lease=new FileStream(Path.Combine(root,"check.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)
    let resultPath=Path.Combine(root,"result.json")
    if File.Exists resultPath then
        printfn "%s" (File.ReadAllText resultPath)
        exit 0
    use stop=new CancellationTokenSource(TimeSpan.FromSeconds 100.)
    use reserve=new TcpListener(IPAddress.Loopback,0)
    reserve.Start()
    let port=(reserve.LocalEndpoint:?>IPEndPoint).Port
    reserve.Stop()
    let debug=Uri(sprintf "http://127.0.0.1:%d" port)
    use http=new HttpClient(Timeout=TimeSpan.FromSeconds 2.)
    let profile=Path.Combine(root,"browser-profile")
    let options=ProcessStartInfo("/usr/bin/google-chrome",UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true)
    for arg in ["--headless=new";"--disable-gpu";"--no-first-run";"--no-default-browser-check";"--remote-debugging-address=127.0.0.1";
                "--remote-debugging-port="+string port;"--user-data-dir="+profile;url.AbsoluteUri] do options.ArgumentList.Add arg
    use browser=Process.Start options
    let errors=browser.StandardError.ReadToEndAsync()
    let output=browser.StandardOutput.ReadToEndAsync()
    let mutable requestId=0
    let receive (socket:ClientWebSocket) = task {
        use bytes=new MemoryStream()
        let buffer=Array.zeroCreate<byte> 8192
        let mutable doneReading=false
        while not doneReading do
            let! chunk=socket.ReceiveAsync(buffer.AsMemory(),stop.Token)
            if chunk.MessageType<>WebSocketMessageType.Text || bytes.Length+int64 chunk.Count>12L*1024L*1024L then invalidOp "Unexpected browser debugging response."
            bytes.Write(buffer,0,chunk.Count)
            doneReading<-chunk.EndOfMessage
        use message=JsonDocument.Parse(bytes.ToArray())
        return message.RootElement.Clone()
    }
    let call (socket:ClientWebSocket) methodName parameters = task {
        requestId<-requestId+1
        let id=requestId
        let bytes=JsonSerializer.SerializeToUtf8Bytes {|id=id;``method``=methodName;``params``=parameters|}
        do! socket.SendAsync(bytes.AsMemory(),WebSocketMessageType.Text,true,stop.Token)
        let mutable reply=None
        while reply.IsNone do
            let! message=receive socket
            match message.TryGetProperty "id" with
            | true,value when value.GetInt32()=id -> reply<-Some message
            | _ -> ()
        let message=reply.Value
        if (message.TryGetProperty "error" |> fst) then invalidOp (message.GetProperty("error").GetRawText())
        return message.GetProperty("result").Clone()
    }
    let evaluate socket expression = task {
        let! result=call socket "Runtime.evaluate" (box {|expression=expression;returnByValue=true;awaitPromise=true|})
        if (result.TryGetProperty "exceptionDetails" |> fst) then invalidOp (result.GetProperty("exceptionDetails").GetRawText())
        return result.GetProperty("result").Clone()
    }
    let waitFor socket expression = task {
        let mutable ready=false
        let deadline=DateTime.UtcNow.AddSeconds 35.
        while not ready && DateTime.UtcNow<deadline do
            let! value=evaluate socket expression
            ready<-(match value.TryGetProperty "value" with true,v when v.ValueKind=JsonValueKind.True -> true | _ -> false)
            if not ready then do! Task.Delay(200,stop.Token)
        if not ready then
            let! body=evaluate socket "document.body.innerText"
            invalidOp ("Browser condition did not become true: "+expression+"; "+body.GetRawText())
    }
    let socketFor (target:JsonElement) = task {
        let socket=new ClientWebSocket()
        do! socket.ConnectAsync(Uri(target.GetProperty("webSocketDebuggerUrl").GetString()),stop.Token)
        return socket
    }
    let check () = task {
        let mutable target=None
        while target.IsNone do
            try
                let! body=http.GetStringAsync(Uri(debug,"/json/list"),stop.Token)
                use parsed=JsonDocument.Parse body
                target<-parsed.RootElement.EnumerateArray() |> Seq.tryFind(fun item->item.GetProperty("type").GetString()="page") |> Option.map _.Clone()
            with :? HttpRequestException | :? OperationCanceledException -> do! Task.Delay(150,stop.Token)
        use! first=socketFor target.Value
        do! waitFor first "document.querySelector('#connection')?.textContent === 'Live'"
        use request=new HttpRequestMessage(HttpMethod.Put,Uri(debug,"/json/new?"+url.AbsoluteUri))
        let! response=http.SendAsync(request,stop.Token)
        response.EnsureSuccessStatusCode() |> ignore
        let! body=response.Content.ReadAsStringAsync(stop.Token)
        use secondTarget=JsonDocument.Parse body
        use! second=socketFor secondTarget.RootElement
        do! waitFor second "document.querySelector('#connection')?.textContent === 'Live'"
        let marker="F# browser "+Guid.NewGuid().ToString("N").Substring(0,12)
        let encoded=JsonSerializer.Serialize marker
        let! _=evaluate first ("(()=>{const i=document.querySelector('#message-input');i.value="+encoded+";i.dispatchEvent(new Event('input',{bubbles:true}));return true;})()")
        do! waitFor first "document.querySelector('button')?.disabled === false"
        let! _=evaluate first "document.querySelector('button').click()"
        let visible="document.querySelector('#messages')?.textContent.includes("+encoded+") === true"
        do! waitFor first visible
        do! waitFor second visible
        let consistent="parseInt(document.querySelector('#message-count').textContent,10) === document.querySelectorAll('#messages li').length"
        do! waitFor first consistent
        do! waitFor second consistent
        let! screenshot=call first "Page.captureScreenshot" (box {|format="png";captureBeyondViewport=false|})
        File.WriteAllBytes(Path.Combine(root,"application.png"),Convert.FromBase64String(screenshot.GetProperty("data").GetString()))
        File.WriteAllText(resultPath,JsonSerializer.Serialize {|fsharpWebAssembly=true;nativeMutation=true;twoTabReactivity=true;coherentVisibleCount=true;browserProfileIsolated=true|})
        printfn "PASS F# WebAssembly, native message mutation, two-tab reactivity and coherent count."
    }
    try check().GetAwaiter().GetResult()
    finally
        if not browser.HasExited then browser.Kill(true)
        browser.WaitForExit()
        File.WriteAllText(Path.Combine(root,"browser.log"),errors.GetAwaiter().GetResult())
        if Directory.Exists profile then Directory.Delete(profile,true)

main()
