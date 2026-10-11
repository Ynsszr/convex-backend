namespace Convex.DotNet.Execution

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

/// Prepare an immutable, credential-free selection for the owning fork's
/// restricted worker. Installed CLR files are pinned rather than modified.
module RuntimeDistribution =
    let private checkPath (path:string) =
        if not(Path.IsPathFullyQualified path) || path.Length>4096 || path |> Seq.exists Char.IsControl then
            invalidOp "Native runtime paths must be exact absolute selections"
        let full=Path.GetFullPath path
        let mutable current=full
        while not(String.IsNullOrEmpty current) do
            if File.Exists current || Directory.Exists current then
                if (File.GetAttributes current).HasFlag FileAttributes.ReparsePoint then
                    invalidOp "Native runtime selections cannot traverse links"
            current<-Path.GetDirectoryName current
        full

    let private digest limit path =
        let path=checkPath path
        let info=FileInfo path
        if not info.Exists || info.Length<=0L || info.Length>limit then invalidOp "Native runtime artifact exceeds its bound"
        use source=info.OpenRead()
        source |> SHA256.HashData |> Convert.ToHexStringLower

    let private privateDirectory path =
        Directory.CreateDirectory path |> ignore
        File.SetUnixFileMode(path,UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    let private freeze source destination limit executable =
        let before=digest limit source
        File.Copy(source,destination,false)
        File.SetUnixFileMode(destination,UnixFileMode.UserRead ||| (if executable then UnixFileMode.UserExecute else enum<UnixFileMode> 0))
        let after=digest limit destination
        if before<>after || digest limit source<>before then invalidOp "Native runtime source changed while freezing"
        {|path=destination;sha256=after|}

    let prepareSelection (outputParent:string) (program:string) (hostDirectory:string) (backend:string) (frameworkVersion:string) =
        if not(OperatingSystem.IsLinux()) then invalidOp "Restricted native runtime preparation requires Linux"
        if not(Text.RegularExpressions.Regex.IsMatch(frameworkVersion,@"^\d{1,5}\.\d{1,5}\.\d{1,5}$")) then
            invalidOp "An exact released CoreCLR version is required"
        let program=checkPath program
        let hostDirectory=checkPath hostDirectory
        let backend=checkPath backend
        let outputParent=checkPath outputParent
        let frameworkRoot=Path.GetDirectoryName program
        let frameworkFiles=ResizeArray<string>()
        let rec collect directory =
            checkPath directory |> ignore
            for path in Directory.GetFiles directory do
                checkPath path |> ignore
                frameworkFiles.Add path
                if frameworkFiles.Count>256 then invalidOp "Native framework closure exceeds its bound"
            for child in Directory.GetDirectories directory do collect child
        let core=Path.Combine(frameworkRoot,"shared/Microsoft.NETCore.App",frameworkVersion)
        collect core
        collect(Path.Combine(frameworkRoot,"host/fxr"))
        if not(File.Exists(Path.Combine(core,"System.Private.CoreLib.dll"))) then invalidOp "Native CoreCLR framework is incomplete"
        let frameworkArtifacts=frameworkFiles |> Seq.sort |> Seq.map(fun path -> {|path=path;sha256=digest (64L*1024L*1024L) path|}) |> Seq.toArray
        let hostFiles=Directory.GetFiles hostDirectory |> Array.filter(fun path ->
            path.EndsWith(".dll",StringComparison.Ordinal) || path.EndsWith(".deps.json",StringComparison.Ordinal) || path.EndsWith(".runtimeconfig.json",StringComparison.Ordinal)) |> Array.sort
        if hostFiles.Length=0 || hostFiles.Length>256 || not(hostFiles |> Array.exists(fun path -> Path.GetFileName path="Convex.DotNet.Host.dll")) then
            invalidOp "A bounded complete native Host closure is required"
        for name in ["Convex.Runtime.dll";"Convex.DotNet.dll";"Convex.FSharp.dll";"Convex.DotNet.Host.deps.json";"Convex.DotNet.Host.runtimeconfig.json"] do
            if not(hostFiles |> Array.exists(fun path -> Path.GetFileName path=name)) then invalidOp "Native Host dependency declarations are incomplete"
        let bubblewrap="/usr/bin/bwrap"
        let bubblewrapSha256=digest (64L*1024L*1024L) bubblewrap
        let programSha256=digest (64L*1024L*1024L) program
        if not(Directory.Exists outputParent) then privateDirectory outputParent
        let output=Path.Combine(outputParent,Guid.NewGuid().ToString("N"))
        privateDirectory output
        let frozenHost=Path.Combine(output,"host")
        let frozenBackend=Path.Combine(output,"backend")
        privateDirectory frozenHost
        privateDirectory frozenBackend
        let artifacts=hostFiles |> Array.map(fun source -> freeze source (Path.Combine(frozenHost,Path.GetFileName source)) (64L*1024L*1024L) false)
        let backendArtifact=freeze backend (Path.Combine(frozenBackend,"convex-local-backend")) (512L*1024L*1024L) true
        let host=Path.Combine(frozenHost,"Convex.DotNet.Host.dll")
        let worker={|
            program=program;programSha256=programSha256;arguments=[|host|];artifacts=artifacts
            framework={|version=frameworkVersion;artifacts=frameworkArtifacts|}
            profile="restricted-first-party";memoryMiB=256;invocationTimeoutMs=16000;preparationProtocolVersion=1;maxInvocations=500
        |}
        let manifestPath=Path.Combine(output,"runtime.json")
        File.WriteAllText(manifestPath,JsonSerializer.Serialize {|version=1;maxWorkers=4;worker=worker;functions=([||]:obj array)|})
        File.SetUnixFileMode(manifestPath,UnixFileMode.UserRead)
        let selection=Path.Combine(output,"selection.json")
        let result={|
            version=1;profile="restricted-first-party";manifestPath=manifestPath;manifestSha256=digest (1024L*1024L) manifestPath
            backendPath=backendArtifact.path;backendSha256=backendArtifact.sha256;dotnetPath=program;dotnetSha256=programSha256
            hostPath=host;frameworkVersion=frameworkVersion;bubblewrapPath=bubblewrap;bubblewrapSha256=bubblewrapSha256
            selectionPath=selection
        |}
        File.WriteAllText(selection,JsonSerializer.Serialize result)
        File.SetUnixFileMode(selection,UnixFileMode.UserRead)
        selection

    let prepare outputParent program hostDirectory backend frameworkVersion =
        prepareSelection outputParent program hostDirectory backend frameworkVersion |> File.ReadAllText |> Console.WriteLine

    /// A reviewed installation selects frozen project and CLI bytes. Its exact
    /// nine fields are accepted by Installation.Core, independently of the
    /// richer runtime-only provenance descriptor above.
    let installationSelectionFile runtimeSelection projectPath cliPath outputParent =
        let runtimeSelection=checkPath runtimeSelection
        let projectPath=checkPath projectPath
        let cliPath=checkPath cliPath
        let outputParent=checkPath outputParent
        let read path =
            let info=FileInfo path
            if not info.Exists || info.Length>65536L then invalidOp "Native selection input exceeds its bound"
            JsonDocument.Parse(File.ReadAllText path)
        use runtime=read runtimeSelection
        let text (value:JsonElement) name=value.GetProperty(name:string).GetString()
        if runtime.RootElement.GetProperty("version").GetInt32()<>1 || text runtime.RootElement "profile"<>"restricted-first-party" then invalidOp "A restricted version-one runtime selection is required"
        let manifestPath=text runtime.RootElement "manifestPath" |> checkPath
        let manifestSha256=text runtime.RootElement "manifestSha256"
        let backendPath=text runtime.RootElement "backendPath" |> checkPath
        let backendSha256=text runtime.RootElement "backendSha256"
        if digest (1024L*1024L) manifestPath<>manifestSha256 || digest (512L*1024L*1024L) backendPath<>backendSha256 then invalidOp "Reviewed native runtime changed"
        use project=read projectPath
        if project.RootElement.GetProperty("version").GetInt32()<>1 || text project.RootElement "rootDirectory"<>"." || project.RootElement.GetProperty("components").GetArrayLength()<>0 || project.RootElement.GetProperty("rootDependencies").GetArrayLength()<>0 then
            invalidOp "The native installation selection requires a standalone portable native project"
        let mutable capsulePins=Unchecked.defaultof<JsonElement>
        if not(project.RootElement.TryGetProperty("capsules",&capsulePins)) || capsulePins.ValueKind<>JsonValueKind.Array || capsulePins.GetArrayLength()=0 then invalidOp "The selected project must bind all native capsule sources"
        if File.Exists(Path.Combine(Path.GetDirectoryName projectPath,".incomplete")) then invalidOp "The native project build is incomplete"
        if not(Directory.Exists outputParent) then privateDirectory outputParent
        let target=Path.Combine(outputParent,Guid.NewGuid().ToString("N"))
        privateDirectory target
        let projectDirectory=Path.Combine(target,"project")
        let cliDirectory=Path.Combine(target,"cli")
        privateDirectory projectDirectory
        privateDirectory cliDirectory
        let sourceDirectory=Path.GetDirectoryName projectPath
        let modules=Directory.GetFiles(sourceDirectory,"*.js") |> Array.sort
        if modules.Length=0 || modules.Length>64 then invalidOp "A bounded native application project is required"
        let mutable aggregate=0L
        let frozenInputs=ResizeArray<string*string>()
        for source in modules do
            aggregate<-aggregate+FileInfo(source).Length
            if aggregate>96_000_000L then invalidOp "Native project exceeds its aggregate bound"
            let frozen=freeze source (Path.Combine(projectDirectory,Path.GetFileName source)) (16L*1024L*1024L) false
            frozenInputs.Add(source,frozen.sha256)
        for source,sha256 in frozenInputs do
            if digest (16L*1024L*1024L) source<>sha256 then invalidOp "Native project changed while freezing"
        let cliSource=Path.GetDirectoryName cliPath
        let files=Directory.GetFiles cliSource |> Array.filter(fun source -> source.EndsWith(".dll",StringComparison.Ordinal) || source.EndsWith(".deps.json",StringComparison.Ordinal) || source.EndsWith(".runtimeconfig.json",StringComparison.Ordinal)) |> Array.sort
        if files.Length=0 || files.Length>64 then invalidOp "Native CLI closure exceeds its bound"
        let mutable cliTotal=0L
        let cliArtifacts=files |> Array.map(fun source ->
            cliTotal<-cliTotal+FileInfo(source).Length
            if cliTotal>128L*1024L*1024L then invalidOp "Native CLI closure exceeds its aggregate bound"
            let frozen=freeze source (Path.Combine(cliDirectory,Path.GetFileName source)) (64L*1024L*1024L) false
            {|name=Path.GetFileName source;sha256=frozen.sha256|})
        let expected=capsulePins.EnumerateArray() |> Seq.map(fun pin -> text pin "path",text pin "sha256") |> Seq.toArray
        let actual=frozenInputs |> Seq.map(fun (source,sha256) -> "root/"+Path.GetFileName source,sha256) |> Seq.toArray
        if (Map.ofArray expected).Count<>expected.Length || Map.ofArray expected<>Map.ofArray actual then invalidOp "Native project inventory or capsule source hashes changed"
        let frozenCli=Path.Combine(cliDirectory,Path.GetFileName cliPath)
        let frozenProject=Path.Combine(projectDirectory,"project.json")
        let descriptor={|
            version=1;functionsDirectory="convex/";rootDirectory=".";rootDependencies=([||]:string array);components=([||]:obj array)
            capsules=capsulePins.Clone();cliArtifacts=cliArtifacts
        |}
        File.WriteAllText(frozenProject,JsonSerializer.Serialize descriptor)
        File.SetUnixFileMode(frozenProject,UnixFileMode.UserRead)
        let result={|
            manifestPath=manifestPath;manifestSha256=manifestSha256;backendPath=backendPath;backendSha256=backendSha256
            dotnetPath=text runtime.RootElement "dotnetPath";projectPath=frozenProject;projectSha256=digest (65536L) frozenProject
            cliPath=frozenCli;cliSha256=digest (64L*1024L*1024L) frozenCli
        |}
        let selection=Path.Combine(target,"installation-selection.json")
        File.WriteAllText(selection,JsonSerializer.Serialize result)
        File.SetUnixFileMode(selection,UnixFileMode.UserRead)
        selection

    let installationSelection runtimeSelection projectPath cliPath outputParent =
        installationSelectionFile runtimeSelection projectPath cliPath outputParent |> printfn "%s"
