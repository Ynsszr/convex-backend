namespace Convex.DotNet.Development

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Reflection
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open Convex.DotNet.Execution
open Convex.DotNet.Deployment

/// The local development command owns only its application's private local state.
/// Runtime binaries and SDK packages remain installed dependencies, not source projects.
module LocalDevelopment =
    type Options = { Project: string; Runtime: string; Port: int; SitePort: int; Once: bool }
    let options (arguments: string array) =
        if arguments.Length=0 then invalidArg "arguments" "Expected convex.project.json."
        let initial = {
            Project=Path.GetFullPath arguments[0]
            Runtime=(match Environment.GetEnvironmentVariable "CONVEX_FSHARP_RUNTIME" with null | "" -> ReleaseLocation.runtime() | value -> value)
            Port=3210;SitePort=3211;Once=false
        }
        let port (value:string) =
            match Int32.TryParse value with
            | true, number when number>0 && number<=65535 -> number
            | _ -> invalidArg "port" "A port between 1 and 65535 is required."
        let rec parse seen current values =
            match values with
            | [] -> current
            | name::_ when Set.contains name seen -> invalidArg "arguments" ("Duplicate development option: "+name)
            | "--once"::rest -> parse (seen.Add "--once") {current with Once=true} rest
            | "--runtime"::value::rest -> parse (seen.Add "--runtime") {current with Runtime=value} rest
            | "--port"::value::rest -> parse (seen.Add "--port") {current with Port=port value} rest
            | "--site-port"::value::rest -> parse (seen.Add "--site-port") {current with SitePort=port value} rest
            | _ -> invalidArg "arguments" "Supported dev options: --runtime PATH, --port PORT, --site-port PORT, --once."
        let result=parse Set.empty initial (arguments |> Array.skip 1 |> Array.toList)
        if String.IsNullOrWhiteSpace result.Runtime then
            invalidArg "runtime" "Install a native Convex runtime and set CONVEX_FSHARP_RUNTIME, or pass --runtime PATH."
        if result.Port=result.SitePort then invalidArg "port" "API and site ports must be different."
        {result with Runtime=Path.GetFullPath result.Runtime}

    let private privateDirectory path =
        let mutable current=Path.GetFullPath path
        while not(String.IsNullOrEmpty current) do
            let info:FileSystemInfo = if Directory.Exists current then DirectoryInfo current else FileInfo current
            if info.LinkTarget<>null || (info.Exists && info.Attributes.HasFlag FileAttributes.ReparsePoint) then
                invalidOp "Local development state cannot traverse symbolic links."
            current<-Path.GetDirectoryName current
        if File.Exists path then invalidOp "Local development state requires a directory."
        Directory.CreateDirectory(path,UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute) |> ignore
        if (File.GetUnixFileMode path &&& (UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite))<>enum<UnixFileMode>0 then
            invalidOp "Local development directories cannot be writable by other users."

    let private writePrivate path value =
        use file=new FileStream(path,FileStreamOptions(Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,
            UnixCreateMode=Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)))
        JsonSerializer.SerializeToUtf8Bytes value |> file.Write
        file.Flush(true)

    let private child (cancellation:CancellationToken) (timeout:TimeSpan) (info:ProcessStartInfo) =
        use childProcess=Process.Start info
        let output=childProcess.StandardOutput.ReadToEndAsync()
        let errors=childProcess.StandardError.ReadToEndAsync()
        use deadline=CancellationTokenSource.CreateLinkedTokenSource cancellation
        deadline.CancelAfter timeout
        try
            childProcess.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult()
            let text=output.GetAwaiter().GetResult()
            let error=errors.GetAwaiter().GetResult()
            if text.Length>4*1024*1024 || error.Length>4*1024*1024 then invalidOp "Development command output exceeded its bound."
            childProcess.ExitCode,text,error
        finally
            if not childProcess.HasExited then childProcess.Kill(true);childProcess.WaitForExit()

    let private startInfo directory executable arguments =
        let info=ProcessStartInfo(executable,UseShellExecute=false,WorkingDirectory=directory,RedirectStandardOutput=true,RedirectStandardError=true)
        for value:string in arguments do info.ArgumentList.Add value
        info

    let private identity directory =
        let file=Path.Combine(directory,"identity.json")
        if not(File.Exists file) then
            writePrivate file {|version=1;instance="fsharp-dev-"+Guid.NewGuid().ToString("N").Substring(0,16);
                               secret=RandomNumberGenerator.GetBytes(32) |> Convert.ToHexStringLower|}
        let info=FileInfo file
        if info.LinkTarget<>null || info.Length>4096L ||
           (File.GetUnixFileMode file &&& (UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite))<>enum<UnixFileMode>0 then
            invalidOp "Local deployment identity must be a private regular file."
        use value=JsonDocument.Parse(File.ReadAllBytes file)
        let text name=value.RootElement.GetProperty(name:string).GetString()
        let name,secret=text "instance",text "secret"
        if value.RootElement.GetProperty("version").GetInt32()<>1 ||
           not(Text.RegularExpressions.Regex.IsMatch(name,"^fsharp-dev-[a-f0-9]{16}$")) ||
           not(NativeDeployment.validHex 64 secret) then invalidOp "Invalid local deployment identity."
        name,secret

    let run (settings:Options) =
        let bundle=RuntimeBundle.read settings.Runtime
        RuntimeBundle.requireCurrentPlatform bundle
        let runtime=RuntimeEnvironment.GetRuntimeDirectory()
        let dotnet=Path.GetFullPath(Path.Combine(runtime,"../../../dotnet"))
        let cli=Assembly.GetExecutingAssembly().Location
        let root=Path.GetDirectoryName settings.Project
        if not(File.Exists settings.Project) then invalidArg "project" "Native project manifest does not exist."
        let directory=Path.Combine(root,".convex","dev")
        privateDirectory directory
        let lockPath=Path.Combine(directory,"owner.lock")
        if FileInfo(lockPath).LinkTarget<>null then invalidOp "Local development lock cannot be a link."
        use lease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)
        let name,secret=identity directory
        let database=Path.Combine(directory,"data.sqlite3")
        let journalDirectory=Path.Combine(directory,"deployment")
        if Directory.Exists journalDirectory && not(File.Exists database) then
            invalidOp "Local database is missing while deployment history exists. Select a fresh application state directory."
        use stopped=new CancellationTokenSource()
        let cancel=ConsoleCancelEventHandler(fun _ event -> event.Cancel<-true;stopped.Cancel())
        Console.CancelKeyPress.AddHandler cancel
        use removeConsoleHandler = { new IDisposable with member _.Dispose() = Console.CancelKeyPress.RemoveHandler cancel }
        use terminate=PosixSignalRegistration.Create(PosixSignal.SIGTERM,fun context -> context.Cancel<-true;stopped.Cancel())
        let selection=RuntimeDistribution.prepareSelection (Path.Combine(directory,"runtime")) dotnet
                          (RuntimeBundle.hostDirectory bundle) (RuntimeBundle.backendPath bundle) (Environment.Version.ToString())
        use removeSelection = { new IDisposable with
                                    member _.Dispose() =
                                        let directory=Path.GetDirectoryName selection
                                        if Directory.Exists directory then Directory.Delete(directory,true) }
        use descriptor=JsonDocument.Parse(File.ReadAllText selection)
        let field key=descriptor.RootElement.GetProperty(key:string).GetString()
        let backend=field "backendPath"
        let keyInfo=startInfo directory backend ["keygen";"admin-key";"--instance-name";name;"--instance-secret";secret]
        let code,key,_=child stopped.Token (TimeSpan.FromSeconds 15.) keyInfo
        if code<>0 || String.IsNullOrWhiteSpace key then invalidOp "Local deployment key generation failed."
        let administrator=key.Trim()
        let origin=Uri(sprintf "http://127.0.0.1:%d" settings.Port)
        let site=Uri(sprintf "http://127.0.0.1:%d" settings.SitePort)
        let info=startInfo directory backend ["--interface";"127.0.0.1";"--port";string settings.Port;"--site-proxy-port";string settings.SitePort;
                      "--instance-name";name;"--instance-secret";secret;"--convex-origin";origin.AbsoluteUri.TrimEnd('/');
                      "--convex-site";site.AbsoluteUri.TrimEnd('/');"--local-storage";Path.Combine(directory,"storage");"--disable-beacon";database]
        info.Environment["CONVEX_DOTNET_MANIFEST"]<-field "manifestPath"
        info.Environment["DISABLE_BEACON"]<-"true"
        use server=new Process(StartInfo=info,EnableRaisingEvents=true)
        server.Exited.Add(fun _ -> stopped.Cancel())
        let logPath=Path.Combine(directory,"backend.log")
        if FileInfo(logPath).LinkTarget<>null then invalidOp "Local backend log cannot be a link."
        use log=new StreamWriter(new FileStream(logPath,FileStreamOptions(Mode=FileMode.Create,Access=FileAccess.Write,Share=FileShare.Read,
                   UnixCreateMode=Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite))))
        let logGate=obj()
        let mutable logSize=0
        let receive (_:obj) (event:DataReceivedEventArgs) =
            if not(isNull event.Data) then
                lock logGate (fun()->
                    if logSize<2*1024*1024 then
                        let line=event.Data.Replace(secret,"[redacted]").Replace(administrator,"[redacted]")
                        logSize<-logSize+line.Length
                        log.WriteLine line;log.Flush())
        server.OutputDataReceived.AddHandler(DataReceivedEventHandler receive)
        server.ErrorDataReceived.AddHandler(DataReceivedEventHandler receive)
        try
            try
                if not(server.Start()) then invalidOp "Could not start local Convex."
                server.BeginOutputReadLine();server.BeginErrorReadLine()
                use http=new HttpClient(Timeout=TimeSpan.FromSeconds 1.)
                let until=DateTime.UtcNow.AddSeconds 30.
                let mutable ready=false
                while not ready && DateTime.UtcNow<until && not stopped.IsCancellationRequested do
                    if server.HasExited then invalidOp "Local Convex stopped during startup. Inspect .convex/dev/backend.log."
                    try
                        use response=http.GetAsync(Uri(origin,"/version"),stopped.Token).GetAwaiter().GetResult()
                        ready<-response.IsSuccessStatusCode
                    with :? HttpRequestException | :? OperationCanceledException -> ()
                    if not ready then stopped.Token.WaitHandle.WaitOne(100) |> ignore
                if not ready then invalidOp "Local Convex did not become ready."
                let connection=Path.Combine(directory,"connection.json")
                if File.Exists connection then
                    if FileInfo(connection).LinkTarget<>null then invalidOp "Local connection descriptor cannot be a link."
                    File.Delete connection
                writePrivate connection {|version=1;backendUrl=origin.AbsoluteUri.TrimEnd('/');siteUrl=site.AbsoluteUri.TrimEnd('/');runtimeVersion=RuntimeBundle.version bundle|}
                printfn "Convex API: %s" origin.AbsoluteUri
                printfn "Convex HTTP routes: %s" site.AbsoluteUri
                printfn "Watching %s (Ctrl+C stops this local backend)." settings.Project
                let sync () =
                    stopped.Token.ThrowIfCancellationRequested()
                    if server.HasExited then invalidOp "The local Convex backend stopped."
                    let build=startInfo root dotnet [cli;"build";settings.Project]
                    let code,output,error=child stopped.Token (TimeSpan.FromMinutes 5.) build
                    if code<>0 then
                        // MSBuild/F# compiler diagnostics normally use stdout;
                        // retain those errors rather than showing only stderr.
                        for diagnostics in [output;error] do
                            if not(String.IsNullOrWhiteSpace diagnostics) then
                                eprintfn "%s" (diagnostics.Replace(secret,"[redacted]").Replace(administrator,"[redacted]"))
                        invalidOp "Application compilation failed; the last deployed application is unchanged."
                    printf "%s" output
                    use project=JsonDocument.Parse(File.ReadAllText settings.Project)
                    let generated=Path.GetFullPath(project.RootElement.GetProperty("output").GetString(),root)
                    let prepared=NativeDeployment.prepareProjectFile(Path.Combine(generated,"project.json"))
                    let hash=NativeDeployment.graphHash prepared
                    use journal=new Convex.DotNet.DeploymentJournal.Journal(origin,hash,stateDirectory=journalDirectory)
                    match journal.Prior with
                    | Convex.DotNet.DeploymentJournal.Recorded -> printfn "Already deployed: %s" hash
                    | Convex.DotNet.DeploymentJournal.Refused -> invalidOp "This application revision was refused; change source before redeploying."
                    | Convex.DotNet.DeploymentJournal.Uncertain -> invalidOp "A prior local deployment is uncertain; reconcile its existing receipt before another deployment."
                    | Convex.DotNet.DeploymentJournal.Fresh ->
                        match NativeDeployment.activateRecorded origin administrator prepared journal.OperationId
                                (fun phase -> journal.Write(phase,Convex.DotNet.DeploymentJournal.JournalOutcome.Sent)) journal.RecordPrepared stopped.Token
                                |> fun task -> task.GetAwaiter().GetResult() with
                        | Confirmed _ ->
                            journal.Write(DeploymentPhase.Activating,Convex.DotNet.DeploymentJournal.JournalOutcome.Confirmed)
                            printfn "Deployed %s" hash
                        | Refused refusal ->
                            journal.Write(refusal.Phase,Convex.DotNet.DeploymentJournal.JournalOutcome.Refused)
                            invalidOp ("Local deployment refused: "+refusal.Code)
                        | Uncertain phase ->
                            journal.Write(phase,Convex.DotNet.DeploymentJournal.JournalOutcome.Uncertain)
                            invalidOp "Local deployment outcome is uncertain; no automatic replay was attempted."
                if settings.Once then sync()
                else ProjectTool.watchWith settings.Project sync stopped.Token
                0
            with :? OperationCanceledException when stopped.IsCancellationRequested -> 0
        finally
            stopped.Cancel()
            Console.CancelKeyPress.RemoveHandler cancel
            try
                if not server.HasExited then server.Kill(true)
                server.WaitForExit()
            with :? InvalidOperationException -> ()

