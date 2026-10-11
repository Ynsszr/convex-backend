module Convex.DotNet.NativeBackendAcceptance

open System
open System.IO
open System.Diagnostics
open System.Reflection
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Net.WebSockets
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Convex.Runtime
open Convex.DotNet.Deployment

// This executable fixture owns a new database, localhost ports, a generated
// scoped administrator key and a frozen host. It never selects an installation.
let package=Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__,"../.."))
let repo=Path.GetFullPath(Path.Combine(package,"../.."))
let buildConfiguration =
    let attribute=Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()
    if isNull attribute then invalidOp "An exact native acceptance build configuration is required"
    match attribute.Configuration with
    | "Debug" | "Release" as selected -> selected
    | _ -> invalidOp "Native acceptance requires Debug or Release artifacts"
let arguments=Environment.GetCommandLineArgs() |> Array.skip 1
if arguments.Length<>2 && arguments.Length<>3 then invalidArg "arguments" "Expected BACKEND_EXECUTABLE NEW_ABSOLUTE_EVIDENCE_DIRECTORY [EXACT_HOST_DIRECTORY]"
let backendPath=Path.GetFullPath arguments[0]
let evidence=Path.GetFullPath arguments[1]
if Directory.Exists evidence || File.Exists evidence then invalidArg "evidence" "Choose a new evidence directory; prior runs are retained"
Directory.CreateDirectory evidence |> ignore
File.SetUnixFileMode(evidence,UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
let privateFile (path:string) (text:string) =
    File.WriteAllText(path,text)
    File.SetUnixFileMode(path,UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
let digest (path:string) =
    use input=File.OpenRead path
    SHA256.HashData input |> Convert.ToHexStringLower
let require condition message = if not condition then invalidOp message
let json (text:string) = RuntimeJson.parse text
let field name (value:JsonElement)=value.GetProperty(name:string)
let text name value=field name value |> fun item -> item.GetString()
let equal expected actual label=require (expected=actual) (label+": expected "+string expected+", actual "+string actual)
let freshPort() =
    use listener=new TcpListener(IPAddress.Loopback,0)
    listener.Start()
    (listener.LocalEndpoint :?> IPEndPoint).Port
let backendPort=freshPort()
let sitePort=freshPort()
let issuer=Uri("http://127.0.0.1:"+string(freshPort()))
let issuerName=issuer.AbsoluteUri.TrimEnd('/')
let audience="native-acceptance"
let origin=Uri("http://127.0.0.1:"+string backendPort)
let site=Uri("http://127.0.0.1:"+string sitePort)
let runtimeDirectory=System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
let dotnet=Path.GetFullPath(Path.Combine(runtimeDirectory,"../../../dotnet"))
let hostSource=
    if arguments.Length=3 then Path.GetFullPath arguments[2]
    else Path.Combine(package,"src/Convex.DotNet.Host/bin",buildConfiguration,"net10.0")
let hostDirectory=Path.Combine(evidence,"host")
Directory.CreateDirectory hostDirectory |> ignore
for file in Directory.GetFiles hostSource do File.Copy(file,Path.Combine(hostDirectory,Path.GetFileName file))
let host=Path.Combine(hostDirectory,"Convex.DotNet.Host.dll")
let artifact (path:string) = {|path=Path.GetFullPath path;sha256=digest path|}
let frameworkVersion=Environment.Version.ToString()
let runtimeRoot=Path.GetDirectoryName dotnet
let framework=Path.Combine(runtimeRoot,"shared/Microsoft.NETCore.App",frameworkVersion)
let fxr=Path.Combine(runtimeRoot,"host/fxr")
let frameworkFiles=Array.append (Directory.GetFiles(framework,"*",SearchOption.AllDirectories)) (Directory.GetFiles(fxr,"*",SearchOption.AllDirectories))
let worker =
    {| program=dotnet
       programSha256=digest dotnet
       artifacts=Directory.GetFiles hostDirectory |> Array.map artifact
       arguments=[|host|]
       framework={|version=frameworkVersion;artifacts=frameworkFiles |> Array.map artifact|}
       profile="restricted-first-party"
       memoryMiB=256
       invocationTimeoutMs=10000
       preparationProtocolVersion=1
       maxInvocations=100 |}
let manifest = {|version=1;maxWorkers=4;worker=worker;functions=Array.empty<obj>|}
let manifestPath=Path.Combine(evidence,"runtime-manifest.json")
privateFile manifestPath (JsonSerializer.Serialize(manifest,JsonSerializerOptions(WriteIndented=true)))
let backendDigest=digest backendPath
let instance="native-acceptance-"+Guid.NewGuid().ToString("N").Substring(0,12)
let secret=RandomNumberGenerator.GetBytes(32) |> Convert.ToHexStringLower
let startInfo (file:string) (parameters:string list) =
    let info=ProcessStartInfo(file,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=evidence)
    for item in parameters do info.ArgumentList.Add item
    info
let keyInfo=startInfo backendPath ["keygen";"admin-key";"--instance-name";instance;"--instance-secret";secret]
let administrator =
    use keygen=Process.Start keyInfo
    let value=keygen.StandardOutput.ReadToEnd()
    let error=keygen.StandardError.ReadToEnd()
    keygen.WaitForExit()
    require (keygen.ExitCode=0) ("Isolated administrator key generation failed: "+error)
    value.Trim()
let results=ResizeArray<obj>()
let saveEvidence (outcome:string) =
    let value=
        {|version=1;outcome=outcome;recordedAt=DateTimeOffset.UtcNow;configuration=buildConfiguration
          backend={|path=backendPath;sha256=backendDigest|}
          host=artifact host;manifest=artifact manifestPath
          fixture=artifact(Path.Combine(package,"tests/RuntimeFixture/bin",buildConfiguration,"net10.0/RuntimeFixture.dll"))
          origin=origin.AbsoluteUri;site=site.AbsoluteUri;checks=results.ToArray()
          scope="Disposable localhost database and scoped credentials. Existing installations and external services were not used."|}
    privateFile (Path.Combine(evidence,"result.json")) (JsonSerializer.Serialize(value,JsonSerializerOptions(WriteIndented=true)))
let check (name:string) (work:unit->Task<unit>) = task {
    let timer=Stopwatch.StartNew()
    try
        do! work()
        results.Add(box {|name=name;outcome="passed";milliseconds=timer.ElapsedMilliseconds|})
        printfn "PASS %s" name
        saveEvidence "running"
    with error ->
        results.Add(box {|name=name;outcome="failed";milliseconds=timer.ElapsedMilliseconds;failure=error.Message|})
        saveEvidence "failed"
        return raise error
}
let run () = task {
    use timeout=new CancellationTokenSource(TimeSpan.FromMinutes 12.)
    use client=new HttpClient(Timeout=TimeSpan.FromSeconds 45.)
    let token=timeout.Token
    use signingKey=RSA.Create(2048)
    let base64Url (bytes:byte[]) = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
    let publicKey=signingKey.ExportParameters(false)
    let jwks=JsonSerializer.Serialize {|keys=[|{|kty="RSA";alg="RS256";``use``="sig";kid="native-acceptance";n=base64Url publicKey.Modulus;e=base64Url publicKey.Exponent|}|]|}
    let configuration=JsonSerializer.Serialize {|issuer=issuerName;jwks_uri=issuerName+"/jwks";authorization_endpoint=issuerName+"/authorize";token_endpoint=issuerName+"/token";response_types_supported=[|"id_token"|];subject_types_supported=[|"public"|];id_token_signing_alg_values_supported=[|"RS256"|]|}
    use identityServer=new HttpListener()
    identityServer.Prefixes.Add(issuer.AbsoluteUri)
    identityServer.Start()
    let identityLoop=task {
        try
            while identityServer.IsListening do
                let! context=identityServer.GetContextAsync().WaitAsync(token)
                let body=
                    match context.Request.Url.AbsolutePath with
                    | "/.well-known/openid-configuration" -> configuration
                    | "/jwks" -> jwks
                    | _ -> context.Response.StatusCode<-404; "{}"
                let bytes=Encoding.UTF8.GetBytes body
                context.Response.ContentType<-"application/json"
                context.Response.ContentLength64<-int64 bytes.Length
                do! context.Response.OutputStream.WriteAsync(bytes.AsMemory(),token)
                context.Response.Close()
        with
        | :? HttpListenerException when not identityServer.IsListening -> ()
        | :? ObjectDisposedException when not identityServer.IsListening -> ()
        | :? OperationCanceledException when token.IsCancellationRequested -> ()
    }
    let jwt selectedAudience expires =
        let now=DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        let header=JsonSerializer.SerializeToUtf8Bytes {|alg="RS256";typ="JWT";kid="native-acceptance"|} |> base64Url
        let claims=JsonSerializer.SerializeToUtf8Bytes {|iss=issuerName;aud=selectedAudience;sub="native-user";iat=now-60L;exp=now+expires|} |> base64Url
        let unsigned=header+"."+claims
        unsigned+"."+base64Url(signingKey.SignData(Encoding.UTF8.GetBytes unsigned,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))
    use log=new StreamWriter(Path.Combine(evidence,"backend.log"),false,Encoding.UTF8,AutoFlush=true)
    let logGate=obj()
    let info=startInfo backendPath [
        "--interface";"127.0.0.1";"--port";string backendPort;"--site-proxy-port";string sitePort
        "--instance-name";instance;"--instance-secret";secret
        "--convex-origin";origin.AbsoluteUri.TrimEnd('/')
        "--convex-site";site.AbsoluteUri.TrimEnd('/')
        "--local-storage";Path.Combine(evidence,"storage")
        "--disable-beacon";Path.Combine(evidence,"data.sqlite3")]
    info.Environment["CONVEX_DOTNET_MANIFEST"]<-manifestPath
    use backend=new Process(StartInfo=info)
    backend.OutputDataReceived.Add(fun event -> if not(isNull event.Data) then lock logGate (fun () -> log.WriteLine event.Data))
    backend.ErrorDataReceived.Add(fun event -> if not(isNull event.Data) then lock logGate (fun () -> log.WriteLine event.Data))
    require (backend.Start()) "The isolated backend did not start"
    backend.BeginOutputReadLine()
    backend.BeginErrorReadLine()
    let postResponseWith credential (kind:string) (path:string) (args:JsonElement) = task {
        use request=new HttpRequestMessage(HttpMethod.Post,Uri(origin,"/api/"+kind))
        credential |> Option.iter(fun value -> request.Headers.Add("Authorization","Bearer "+value))
        request.Content<-new StringContent(JsonSerializer.Serialize {|path=path;args=args;format="convex_encoded_json"|},Encoding.UTF8,"application/json")
        use! response=client.SendAsync(request,token)
        let! raw=response.Content.ReadAsStringAsync(token)
        require (raw.Length<4*1024*1024) "The proof response exceeded its bound"
        return int response.StatusCode,json raw
    }
    let postResponse kind path args=postResponseWith None kind path args
    let post kind path args = task {
        let! status,reply=postResponse kind path args
        require (status=200) ("Native API "+path+" returned HTTP "+string status+": "+reply.GetRawText())
        return reply
    }
    let call kind path args = task {
        let! reply=post kind path args
        if text "status" reply<>"success" then invalidOp ("Native call failed "+path+": "+reply.GetRawText())
        return field "value" reply
    }
    let query path args=call "query" path args
    let mutation path args=call "mutation" path args
    let action path args=call "action" path args
    let empty=json "{}"
    let name (value:string)=RuntimeJson.element ["name",box value]
    let increment (value:string) (amount:float)=RuntimeJson.element ["name",box value;"amount",box amount]
    let read value=query "Counter:read" (name value)
    let absent value=task { let! actual=read value in equal JsonValueKind.Null actual.ValueKind (value+" must be absent") }
    let waitFor label (probe:unit->Task<bool>) = task {
        let deadline=DateTimeOffset.UtcNow.AddSeconds 30.
        let mutable ready=false
        while not ready && DateTimeOffset.UtcNow<deadline do
            let! result=probe()
            ready<-result
            if not ready then do! Task.Delay(100,token)
        require ready ("Timed out waiting for "+label)
    }
    try
        do! check "current backend startup" (fun () -> task {
            do! waitFor "backend startup" (fun () -> task {
                if backend.HasExited then invalidOp "The isolated backend exited during startup"
                try
                    use! response=client.GetAsync(Uri(origin,"/version"),token)
                    return response.IsSuccessStatusCode
                with :? HttpRequestException -> return false
            })
        })
        do! check "isolated auth environment is explicitly configured" (fun () -> task {
            use request=new HttpRequestMessage(HttpMethod.Post,Uri(origin,"/api/update_environment_variables"))
            request.Headers.Add("Authorization","Convex "+administrator)
            request.Content<-new StringContent(JsonSerializer.Serialize {|changes=[|
                {|name="CONVEX_OIDC_ISSUER";value=issuerName|}
                {|name="CONVEX_OIDC_AUDIENCE";value=audience|}|]|},Encoding.UTF8,"application/json")
            use! response=client.SendAsync(request,token)
            require response.IsSuccessStatusCode ("Isolated environment setup failed: "+string response.StatusCode)
        })
        let prepared=NativeDeployment.prepareDirectory (Path.Combine(package,"artifacts/native-capsules")) "convex"
        let journalRoot=Path.Combine(evidence,"deployment-journal")
        let mutable deployedToken:NativeDeploymentToken option=None
        do! check "native capsule activation and immutable receipt" (fun () -> task {
            use journal=new Convex.DotNet.DeploymentJournal.Journal(origin,NativeDeployment.graphHash prepared,stateDirectory=journalRoot)
            equal Convex.DotNet.DeploymentJournal.Fresh journal.Prior "New deployment journal"
            let! outcome=NativeDeployment.activateRecorded origin administrator prepared journal.OperationId
                            (fun phase -> journal.Write(phase,Convex.DotNet.DeploymentJournal.JournalOutcome.Sent))
                            journal.RecordPrepared token
            match outcome with
            | Confirmed _ ->
                journal.Write(Activating,Convex.DotNet.DeploymentJournal.JournalOutcome.Confirmed)
                deployedToken<-journal.PendingToken
            | Refused refusal -> invalidOp ("Native deployment refused: "+string refusal)
            | Uncertain phase -> invalidOp ("Native deployment is uncertain at "+string phase)
            let! receipt=NativeDeployment.readReceipt origin administrator (Option.get deployedToken) token
            match receipt with
            | HistoricalCommit value -> equal (Option.get deployedToken) value.Token "Owning activation receipt"
            | UnresolvedReceipt -> invalidOp "The confirmed activation has no owning immutable receipt"
        })
        do! check "a lost actual activation reply recovers only by owning receipt without redelivery" (fun () -> task {
            let proxyOrigin=Uri("http://127.0.0.1:"+string(freshPort()))
            use proxy=new HttpListener()
            proxy.Prefixes.Add(proxyOrigin.AbsoluteUri)
            proxy.Start()
            use proxyCancellation=CancellationTokenSource.CreateLinkedTokenSource token
            let cancellation=proxyCancellation.Token
            let requests=Collections.Generic.List<string>()
            let mutable ownerCommitted=false
            let proxyLoop=task {
                try
                    while proxy.IsListening do
                        let! incoming=proxy.GetContextAsync().WaitAsync(cancellation)
                        let path=incoming.Request.Url.AbsolutePath
                        require (path.StartsWith("/api/deploy2/",StringComparison.Ordinal)) "The receipt proxy only serves the owning deployment protocol"
                        requests.Add path
                        use body=new MemoryStream()
                        do! incoming.Request.InputStream.CopyToAsync(body,cancellation)
                        require (body.Length<192000000L) "The native push exceeded its request bound"
                        use request=new HttpRequestMessage(HttpMethod.Post,Uri(origin,path))
                        request.Content<-new ByteArrayContent(body.ToArray())
                        request.Content.Headers.ContentType<-Headers.MediaTypeHeaderValue("application/json")
                        use! response=client.SendAsync(request,cancellation)
                        let! bytes=response.Content.ReadAsByteArrayAsync(cancellation)
                        let replacement=
                            if path="/api/deploy2/finish_push" && response.IsSuccessStatusCode then
                                ownerCommitted<-true
                                Encoding.UTF8.GetBytes "owning-commit-reply-was-lost"
                            else bytes
                        incoming.Response.StatusCode<-int response.StatusCode
                        incoming.Response.ContentType<-"application/json"
                        incoming.Response.ContentLength64<-int64 replacement.Length
                        do! incoming.Response.OutputStream.WriteAsync(replacement.AsMemory(),cancellation)
                        incoming.Response.Close()
                with
                | :? HttpListenerException when not proxy.IsListening -> ()
                | :? ObjectDisposedException when not proxy.IsListening -> ()
                | :? OperationCanceledException when cancellation.IsCancellationRequested -> ()
            }
            try
                let stateDirectory=Path.Combine(evidence,"lost-reply-journal")
                let graphHash=NativeDeployment.graphHash prepared
                let mutable retained:NativeDeploymentToken option=None
                let send()=task {
                    use journal=new Convex.DotNet.DeploymentJournal.Journal(proxyOrigin,graphHash,stateDirectory=stateDirectory)
                    equal Convex.DotNet.DeploymentJournal.Fresh journal.Prior "Lost reply starts with a new operation"
                    let! outcome=NativeDeployment.activateRecorded proxyOrigin administrator prepared journal.OperationId
                                    (fun phase -> journal.Write(phase,Convex.DotNet.DeploymentJournal.JournalOutcome.Sent))
                                    journal.RecordPrepared token
                    match outcome with
                    | Uncertain Activating -> journal.Write(Activating,Convex.DotNet.DeploymentJournal.JournalOutcome.Uncertain)
                    | _ -> invalidOp "The unreadable real commit response must preserve uncertainty"
                    retained<-journal.PendingToken
                }
                do! send()
                require ownerCommitted "The owning backend never completed activation"
                let recover()=task {
                    use journal=new Convex.DotNet.DeploymentJournal.Journal(proxyOrigin,graphHash,stateDirectory=stateDirectory)
                    equal Convex.DotNet.DeploymentJournal.Uncertain journal.Prior "Uncertain activation is fenced on restart"
                    equal retained journal.PendingToken "The original receipt identity survives restart"
                    let expected=Option.get journal.PendingToken
                    let! wrong=NativeDeployment.readReceipt origin administrator {expected with StartPushSha256=String('0',64)} token
                    equal UnresolvedReceipt wrong "A mismatched receipt cannot settle delivery"
                    let! outcome=NativeDeployment.readReceipt origin administrator expected token
                    match outcome with
                    | HistoricalCommit receipt -> journal.ConfirmHistorical receipt
                    | UnresolvedReceipt -> invalidOp "The lost committed activation has no matching owning receipt"
                }
                do! recover()
                use confirmed=new Convex.DotNet.DeploymentJournal.Journal(proxyOrigin,graphHash,stateDirectory=stateDirectory)
                equal Convex.DotNet.DeploymentJournal.Recorded confirmed.Prior "Recovered delivery remains recorded on restart"
                equal 1 (requests |> Seq.filter(fun path -> path="/api/deploy2/start_push") |> Seq.length) "Preparation is not replayed"
                equal 1 (requests |> Seq.filter(fun path -> path="/api/deploy2/finish_push") |> Seq.length) "Activation is not replayed"
                let! historical=NativeDeployment.readReceipt origin administrator (Option.get deployedToken) token
                match historical with
                | HistoricalCommit receipt -> equal (Option.get deployedToken) receipt.Token "An earlier immutable receipt remains historical after a later activation"
                | _ -> invalidOp "A later deployment erased the earlier owning receipt"
            finally
                proxyCancellation.Cancel()
                proxy.Stop()
                proxyLoop.GetAwaiter().GetResult()
        })
        do! check "canonical int64 bytes float null and object values" (fun () -> task {
            let expected=json """{"integer":{"$integer":"/////////38="},"float":{"$float":"AAAAAAAA8H8="},"bytes":{"$bytes":"AAH/"},"null":null}"""
            let! actual=query "Counter:echo" (RuntimeJson.element ["value",box expected])
            require (JsonElement.DeepEquals(expected,actual)) "Canonical wire values changed"
        })
        do! check "native indexed read mutation and action" (fun () -> task {
            do! absent "basic"
            let! first=mutation "Counter:increment" (increment "basic" 2.)
            equal 2. ((field "value" first).GetDouble()) "Mutation value"
            let! second=action "Counter:incrementAction" (increment "basic" 3.)
            equal 5. ((field "value" second).GetDouble()) "Action mutation value"
            let! observed=read "basic"
            equal 5. ((field "value" observed).GetDouble()) "Indexed read value"
        })
        do! check "mutation error rolls back the original transaction" (fun () -> task {
            let! refusal=post "mutation" "Counter:fail" (name "rollback")
            equal "error" (text "status" refusal) "Mutation refusal"
            equal "refused" (text "reason" (field "errorData" refusal)) "Safe error reason"
            equal "rollback" (text "operation" (field "errorData" refusal)) "Safe error operation"
            do! absent "rollback"
        })
        do! check "nested mutation error is catchable and its child write rolls back" (fun () -> task {
            let! refusal=mutation "Counter:nestedRollback" (name "nested-rollback")
            equal "refused" (text "reason" refusal) "Nested safe error"
            let! value=read "nested-rollback"
            equal 1. ((field "value" value).GetDouble()) "Parent work after child rollback"
        })
        do! check "schema rejection preserves transaction atomicity" (fun () -> task {
            let! refused=post "mutation" "Counter:invalidDocument" (name "invalid")
            equal "error" (text "status" refused) "Invalid schema write"
            do! absent "invalid"
        })
        do! check "concurrent native OCC increments lose no writes" (fun () -> task {
            for _ in 1..6 do
                let! _=[|for _ in 1..2 -> mutation "Counter:increment" (increment "occ" 1.)|] |> Task.WhenAll
                ()
            let! actual=read "occ"
            equal 12. ((field "value" actual).GetDouble()) "OCC counter"
        })
        do! check "high contention preserves committed writes and refuses exhausted retries" (fun () -> task {
            let! replies=[|for _ in 1..12 -> postResponse "mutation" "Counter:increment" (increment "occ-contention" 1.)|] |> Task.WhenAll
            let mutable committed=0
            let mutable refused=0
            for status,reply in replies do
                if status=200 && text "status" reply="success" then committed<-committed+1
                elif status=503 && text "code" reply="OptimisticConcurrencyControlFailure" then refused<-refused+1
                else invalidOp ("Unexpected contention response: "+string status+" "+reply.GetRawText())
            require (committed>0) "Contention made no progress"
            equal 12 (committed+refused) "Every submitted mutation has an owning outcome"
            let! actual=read "occ-contention"
            equal (float committed) ((field "value" actual).GetDouble()) "Exactly the acknowledged mutations committed"
            printfn "OCC contention: %d committed; %d explicitly refused; no client resends" committed refused
        })
        do! check "native query dependencies invalidate a live reactive subscription" (fun () -> task {
            use socket=new ClientWebSocket()
            use deadline=CancellationTokenSource.CreateLinkedTokenSource token
            deadline.CancelAfter(TimeSpan.FromSeconds 20.)
            let cancellation=deadline.Token
            let address=UriBuilder(origin)
            address.Scheme<-"ws"
            address.Path<-"/api/1.46.0/sync"
            do! socket.ConnectAsync(address.Uri,cancellation)
            let send value=task {
                let bytes=JsonSerializer.SerializeToUtf8Bytes value
                do! socket.SendAsync(bytes.AsMemory(),WebSocketMessageType.Text,true,cancellation)
            }
            let receive()=task {
                use output=new MemoryStream()
                let buffer=Array.zeroCreate<byte> 8192
                let mutable complete=false
                while not complete do
                    let! part=socket.ReceiveAsync(buffer.AsMemory(),cancellation)
                    require (part.MessageType=WebSocketMessageType.Text && output.Length+int64 part.Count<1024L*1024L) "Reactive response is not bounded text"
                    output.Write(buffer,0,part.Count)
                    complete<-part.EndOfMessage
                return RuntimeJson.parse(Encoding.UTF8.GetString(output.ToArray()))
            }
            let waitCounter expected=task {
                let mutable matched=false
                while not matched do
                    let! message=receive()
                    match text "type" message with
                    | "Transition" ->
                        for update in (field "modifications" message).EnumerateArray() do
                            if text "type" update="QueryFailed" then invalidOp ("Reactive query failed: "+update.GetRawText())
                            if text "type" update="QueryUpdated" && (field "queryId" update).GetInt32()=0 then
                                let value=field "value" update
                                matched<-
                                    match expected with
                                    | None -> value.ValueKind=JsonValueKind.Null
                                    | Some expected -> value.ValueKind=JsonValueKind.Object && (field "value" value).GetDouble()=expected
                    | "FatalError" | "AuthError" -> invalidOp ("Reactive connection failed: "+message.GetRawText())
                    | _ -> ()
            }
            do! send {|``type``="Connect";sessionId=Guid.NewGuid().ToString("D");connectionCount=0;lastCloseReason="InitialConnect";clientTs=Environment.TickCount64|}
            do! send {|``type``="ModifyQuerySet";baseVersion=0;newVersion=1;modifications=[|{|``type``="Add";queryId=0;udfPath="Counter:read";args=[|name "reactive"|]|}|]|}
            do! waitCounter None
            let! _=mutation "Counter:increment" (increment "reactive" 7.)
            do! waitCounter (Some 7.)
            let! _=mutation "Counter:increment" (increment "reactive" 2.)
            do! waitCounter (Some 9.)
            do! socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"acceptance complete",cancellation)
        })
        do! check "native identity and unauthorized mutation refusal" (fun () -> task {
            let! identity=query "Counter:identity" empty
            equal JsonValueKind.Null identity.ValueKind "Anonymous identity"
            let! refusal=post "mutation" "Counter:authorizedIncrement" (increment "unauthorized" 1.)
            equal "error" (text "status" refusal) "Unauthorized write"
            do! absent "unauthorized"
        })
        do! check "native authentication verifies the configured OIDC issuer and signed identity" (fun () -> task {
            let credential=jwt audience 300L
            let! status,identity=postResponseWith (Some credential) "query" "Counter:identity" empty
            equal 200 status "Authenticated identity status"
            equal "success" (text "status" identity) "Authenticated query outcome"
            equal "native-user" ((field "value" identity).GetString()) "Verified native subject"
            let! status,written=postResponseWith (Some credential) "mutation" "Counter:authorizedIncrement" (increment "authorized" 6.)
            equal 200 status "Authorized mutation status"
            equal "success" (text "status" written) "Authorized mutation outcome"
            let! actual=read "authorized"
            equal 6. ((field "value" actual).GetDouble()) "Authenticated mutation committed"
        })
        do! check "native authentication rejects wrong audience expired and invalid signature tokens" (fun () -> task {
            let valid=jwt audience 300L
            let parts=valid.Split('.')
            let forged=parts[0]+"."+parts[1]+"."+base64Url(Array.zeroCreate<byte> 256)
            for credential in [jwt "wrong-audience" 300L;jwt audience -3600L;forged] do
                let! status,_=postResponseWith (Some credential) "mutation" "Counter:authorizedIncrement" (increment "invalid-auth" 1.)
                equal 401 status "Invalid authentication rejection"
            do! absent "invalid-auth"
        })
        do! check "durable aliases and handles retain native call contracts" (fun () -> task {
            let! written=mutation "AliasCounter:increment" (increment "alias" 4.)
            equal 4. ((field "value" written).GetDouble()) "Alias write"
            let! handle=query "AliasCounter:handle" empty
            let! actual=query "Counter:handleRead" (RuntimeJson.element ["handle",box (handle.GetString());"name",box "alias"])
            equal 4. ((field "value" actual).GetDouble()) "Durable function handle"
            let! metadata=query "AliasCounter:metadata" empty
            equal "AliasCounter:metadata" (text "Name" metadata) "Durable function metadata"
        })
        do! check "pending commit timestamps settle in the owning transaction" (fun () -> task {
            let! direct=mutation "Counter:commitWrite" (name "commit")
            let! stored=query "Counter:commitRead" (name "commit")
            equal (direct.GetRawText()) ((field "committedAt" stored).GetRawText()) "Committed timestamp"
            let! nested=mutation "Counter:commitNested" (name "nested-commit")
            let! nestedStored=query "Counter:commitRead" (name "nested-commit")
            equal (nested.GetRawText()) ((field "committedAt" nestedStored).GetRawText()) "Nested committed timestamp"
        })
        do! check "atomic scheduling cancellation and scheduling rollback" (fun () -> task {
            let! scheduled=mutation "Counter:schedule" (increment "scheduled" 2.)
            require (scheduled.ValueKind=JsonValueKind.String) "A scheduled function ID is required"
            do! waitFor "scheduled native mutation" (fun () -> task {
                let! value=read "scheduled"
                return value.ValueKind<>JsonValueKind.Null && (field "value" value).GetDouble()=2.
            })
            let! canceled=mutation "Counter:scheduleCancel" (increment "canceled" 3.)
            let! state=query "Counter:scheduledState" (RuntimeJson.element ["id",box (canceled.GetString())])
            equal "canceled" (state.GetString()) "Canceled scheduled function"
            do! absent "canceled"
            let! refused=post "mutation" "Counter:scheduleFail" (increment "schedule-rollback" 9.)
            equal "error" (text "status" refused) "Scheduling rollback refusal"
            let scheduledId=text "scheduledId" (field "errorData" refused)
            let! rolledBack=query "Counter:scheduledState" (RuntimeJson.element ["id",box scheduledId])
            equal JsonValueKind.Null rolledBack.ValueKind "Atomic scheduled record rollback"
            do! absent "schedule-rollback"
        })
        do! check "full text and vector search use native schema indexes" (fun () -> task {
            let source (name:string) (category:string) (description:string) (vector:float[])=RuntimeJson.element ["name",box name;"category",box category;"text",box description;"embedding",box vector]
            let! _=mutation "Counter:insertSearch" (source "selected" "native" "reactor native searchable" [|1.;0.;0.;0.|])
            let! _=mutation "Counter:insertSearch" (source "excluded" "other" "reactor native searchable" [|1.;0.;0.;0.|])
            do! waitFor "search index" (fun () -> task {
                let! found=query "Counter:search" (RuntimeJson.element ["category",box "native";"query",box "reactor"])
                return found.GetArrayLength()=1 && found[0].GetString()="selected"
            })
            let! hits=action "Counter:vectors" (RuntimeJson.element ["category",box "native";"vector",box [|1.;0.;0.;0.|]])
            equal 1 (hits.GetArrayLength()) "Filtered vector results"
        })
        do! check "native action storage keeps bytes metadata and deletion" (fun () -> task {
            let! value=action "Counter:storageRoundtrip" (json """{"value":{"$bytes":"AAH/"}}""")
            equal "AAH/" (text "$bytes" (field "bytes" value)) "Stored bytes"
            equal "application/octet-stream" (text "contentType" value) "Stored content type"
        })
        do! check "native HTTP body cloning headers and HEAD routing" (fun () -> task {
            use request=new HttpRequestMessage(HttpMethod.Post,Uri(site,"/native/echo"))
            request.Content<-new ByteArrayContent([|0uy;1uy;255uy|])
            request.Headers.Add("x-native-probe","owned")
            use! response=client.SendAsync(request,token)
            require response.IsSuccessStatusCode "Native HTTP echo failed"
            let! bytes=response.Content.ReadAsByteArrayAsync(token)
            require (bytes=[|0uy;1uy;255uy|]) "Native HTTP bytes changed"
            equal "owned" (response.Headers.GetValues("x-native-probe") |> Seq.exactlyOne) "HTTP header"
            use head=new HttpRequestMessage(HttpMethod.Head,Uri(site,"/native/prefix/child"))
            use! headResponse=client.SendAsync(head,token)
            require headResponse.IsSuccessStatusCode "Native HEAD routing failed"
            let! body=headResponse.Content.ReadAsByteArrayAsync(token)
            equal 0 body.Length "HEAD response body"
        })
        do! check "native bounded HTTP and storage streaming" (fun () -> task {
            let length=9*1024*1024
            use request=new HttpRequestMessage(HttpMethod.Get,Uri(site,"/native/stream"))
            request.Headers.Add("x-native-size",string length)
            use! response=client.SendAsync(request,token)
            require response.IsSuccessStatusCode "Native HTTP streaming failed"
            let! bytes=response.Content.ReadAsByteArrayAsync(token)
            equal length bytes.Length "HTTP stream length"
            for offset in [0;65535;65536;length-1] do equal (byte(offset%251)) bytes[offset] "HTTP stream byte"
            let! stored=action "Counter:storageStreamRoundtrip" (RuntimeJson.element ["size",box length])
            let encodedLength=text "$integer" (field "length" stored) |> Convert.FromBase64String |> fun value -> BitConverter.ToInt64(value,0)
            equal (int64 length) encodedLength "Stored stream length"
            equal (SHA256.HashData bytes |> Convert.ToBase64String) (text "sha256" stored) "Stored stream hash"
        })
        do! check "noncooperative mutation timeout rolls back and replacement worker recovers" (fun () -> task {
            let! status,refused=postResponse "mutation" "Counter:hangMutation" (name "interrupted")
            equal 500 status "Noncooperative mutation HTTP refusal"
            equal "InternalServerError" (text "code" refused) "Private execution deadline"
            require (File.ReadAllText(Path.Combine(evidence,"backend.log")).Contains("native user deadline exceeded",StringComparison.Ordinal)) "The owning native deadline did not retire execution"
            do! absent "interrupted"
            let! value=mutation "Counter:increment" (increment "after-interruption" 1.)
            equal 1. ((field "value" value).GetDouble()) "Fresh worker after retirement"
        })
        require (digest backendPath=backendDigest) "Backend bytes changed during acceptance"
        saveEvidence "passed"
        printfn "Native backend acceptance passed: %d checks. Evidence: %s" results.Count evidence
    finally
        if not backend.HasExited then backend.Kill(true)
        backend.WaitForExit()
        timeout.Cancel()
        identityServer.Stop()
        identityLoop.GetAwaiter().GetResult()
}
try run().GetAwaiter().GetResult()
with error ->
    eprintfn "Native backend acceptance failed: %s" error.Message
    saveEvidence "failed"
    exit 1
