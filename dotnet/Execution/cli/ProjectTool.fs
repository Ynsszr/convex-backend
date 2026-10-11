namespace Convex.DotNet.Development

open System
open System.IO
open System.Diagnostics
open System.Reflection
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open Convex.Runtime
open Convex.FSharp
open Convex.DotNet.Execution

/// Application-owned configuration; implementation and dependencies belong to the CLI package.
module ProjectTool =
    let private json (value:obj) = JsonSerializer.Serialize(value, JsonSerializerOptions(WriteIndented=true))
    let private text name (value:JsonElement) = value.GetProperty(name:string).GetString()
    let private validModulePath value =
        not(String.IsNullOrWhiteSpace value) && Regex.IsMatch(value,@"^[A-Za-z_][A-Za-z0-9_]*(/[A-Za-z_][A-Za-z0-9_]*)*$")
    let private optional name (value:JsonElement) =
        match value.TryGetProperty(name:string) with
        | true, item when item.ValueKind<>JsonValueKind.Null -> Some item
        | _ -> None
    let private hash path =
        use stream=File.OpenRead path
        SHA256.HashData stream |> Convert.ToHexStringLower
    let private run directory executable (arguments:string list) capture =
        let options=ProcessStartInfo(executable,UseShellExecute=false,WorkingDirectory=directory,RedirectStandardOutput=capture)
        for argument in arguments do options.ArgumentList.Add argument
        use child=Process.Start options
        let output=if capture then child.StandardOutput.ReadToEndAsync() else Threading.Tasks.Task.FromResult ""
        use cancel= new CancellationTokenSource(TimeSpan.FromMinutes 5.)
        try child.WaitForExitAsync(cancel.Token).GetAwaiter().GetResult()
        with :? OperationCanceledException ->
            if not child.HasExited then child.Kill(true)
            child.WaitForExit()
            invalidOp "Development child exceeded its five-minute bound."
        if child.ExitCode<>0 then invalidOp (sprintf "%s exited with %d" executable child.ExitCode)
        output.GetAwaiter().GetResult().Trim()

    let private load path = ApplicationGraph.load path
    let private resolve root value = Path.GetFullPath(value,root)
    let private targetPath (node:ApplicationGraph.Node) =
        run node.Root "dotnet" ["msbuild";ApplicationGraph.project node;"-nologo";"-getProperty:TargetPath";"-p:Configuration="+ApplicationGraph.configuration node] true
        |> Path.GetFullPath
    let build path =
        let graph=ApplicationGraph.read path
        ApplicationGraph.nodes graph
        |> Array.distinctBy(fun node -> ApplicationGraph.project node,ApplicationGraph.configuration node)
        |> Array.iter(fun node -> run node.Root "dotnet" ["build";ApplicationGraph.project node;"--configuration";ApplicationGraph.configuration node;"--nologo";"-v:minimal"] false |> ignore)
    let package path = GraphPackaging.package (ApplicationGraph.read path) targetPath
    let prepare path = build path;package path

    /// Ask MSBuild for the evaluated source graph, including linked source files
    /// and property-based ProjectReferences. Never guess it from solution folders.
    let private sourceFiles (graph:ApplicationGraph.Graph) =
        let visited=Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let files=Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let rec visit project configuration =
            if visited.Add project then
                if visited.Count>128 then invalidOp "Development watch supports at most 128 referenced projects."
                files.Add project |> ignore
                use document=JsonDocument.Parse(run (Path.GetDirectoryName project) "dotnet"
                    ["msbuild";project;"-nologo";"-getItem:ProjectReference,Compile";"-p:Configuration="+configuration] true)
                let items=document.RootElement.GetProperty "Items"
                for file in items.GetProperty("Compile").EnumerateArray() do
                    let path=file.GetProperty("FullPath").GetString()
                    if not(path.Split(Path.DirectorySeparatorChar) |> Array.exists(fun part -> part="obj" || part="bin")) then files.Add path |> ignore
                let mutable parent=Path.GetDirectoryName project
                while not(String.IsNullOrEmpty parent) do
                    for name in ["Directory.Build.props";"Directory.Build.targets";"Directory.Packages.props";"global.json"] do
                        let path=Path.Combine(parent,name)
                        if File.Exists path then files.Add path |> ignore
                    parent<-Path.GetDirectoryName parent
                for reference in items.GetProperty("ProjectReference").EnumerateArray() do
                    visit (reference.GetProperty("FullPath").GetString()) configuration
        for node in ApplicationGraph.nodes graph do
            files.Add node.Manifest |> ignore
            visit (ApplicationGraph.project node) (ApplicationGraph.configuration node)
        files |> Seq.toArray

    let watchWith path (operation:unit -> unit) (cancellation:CancellationToken) =
        let path=Path.GetFullPath path
        let mutable watched=[|path|]
        let mutable directoryRoots=[|Path.GetDirectoryName path|]
        let refresh () =
            let graph=ApplicationGraph.read path
            watched<-sourceFiles graph
            directoryRoots<-ApplicationGraph.nodes graph |> Array.map _.Root |> Array.distinct
        let snapshot () =
            let rec scan (directory:string) = seq {
                for file in Directory.GetFiles directory do
                    if [".fs";".fsi";".fsproj";".props";".targets";".json"] |> List.contains(Path.GetExtension file) then yield file
                for child in Directory.GetDirectories directory do
                    let info=DirectoryInfo child
                    if not(info.Attributes.HasFlag FileAttributes.ReparsePoint) &&
                       not([".git";"bin";"obj";"artifacts";"node_modules";".convex";".dotnet-home"] |> List.contains info.Name) then yield! scan child
            }
            Seq.append watched (directoryRoots |> Seq.collect scan)
            |> Seq.distinct |> Seq.truncate 10001 |> Seq.sort
            |> Seq.map(fun path -> let info=FileInfo path in path,(if info.Exists then info.Length else -1L),info.LastWriteTimeUtc.Ticks)
            |> Seq.toArray
        refresh()
        let mutable previous=[||]
        while not cancellation.IsCancellationRequested do
            let current=snapshot()
            if current.Length>10000 then invalidOp "Development watch supports at most 10,000 source files."
            if current<>previous then
                previous<-current
                try
                    refresh()
                    // Capture the inputs the build is about to consume. Taking
                    // this snapshot after the build hides edits made in flight.
                    previous<-snapshot()
                    if previous.Length>10000 then invalidOp "Development watch supports at most 10,000 source files."
                    operation()
                with error -> eprintfn "Development build failed: %s" error.Message
            cancellation.WaitHandle.WaitOne(1000) |> ignore

    let watch path =
        use stopped=new CancellationTokenSource()
        let handler=ConsoleCancelEventHandler(fun _ event -> event.Cancel<-true;stopped.Cancel())
        Console.CancelKeyPress.AddHandler handler
        try watchWith path (fun () -> prepare path) stopped.Token
        finally Console.CancelKeyPress.RemoveHandler handler

    let runtimeInspect distribution =
        let bundle = RuntimeBundle.read distribution
        printfn "%s" (json {| format="convex-native-runtime"; version=RuntimeBundle.version bundle
                              rid=RuntimeBundle.runtimeIdentifier bundle; backend=RuntimeBundle.backendPath bundle
                              inventoryVerified=true; backendExecuted=false |})

    let private prepareRuntime distribution output =
        let bundle = RuntimeBundle.read distribution
        RuntimeBundle.requireCurrentPlatform bundle
        let runtime = Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
        let dotnet = Path.GetFullPath(Path.Combine(runtime, "../../../dotnet"))
        RuntimeDistribution.prepareSelection (Path.GetFullPath output) dotnet
            (RuntimeBundle.hostDirectory bundle) (RuntimeBundle.backendPath bundle) (Environment.Version.ToString())

    let runtimePrepare distribution output = prepareRuntime distribution output |> printfn "%s"

    /// Own the launcher lifetime; never install services or disable the worker sandbox.
    let runtimeRun distribution output (arguments:string array) =
        let output=Path.GetFullPath output
        let selection=prepareRuntime distribution (Path.Combine(output,"runtime-selections"))
        use document=JsonDocument.Parse(File.ReadAllText selection)
        let descriptor=document.RootElement
        let info=ProcessStartInfo(text "backendPath" descriptor,UseShellExecute=false,WorkingDirectory=output)
        info.Environment["CONVEX_DOTNET_MANIFEST"]<-text "manifestPath" descriptor
        info.Environment["DISABLE_BEACON"]<-"true"
        // Local development is loopback by default. Public binding must be explicitly requested.
        if not(arguments |> Array.exists(fun item -> item="--interface" || item="-i" || item.StartsWith("--interface="))) then
            info.ArgumentList.Add "--interface";info.ArgumentList.Add "127.0.0.1"
        for argument in arguments do info.ArgumentList.Add argument
        use child=Process.Start info
        let stop=ConsoleCancelEventHandler(fun _ event ->
            event.Cancel<-true
            if not child.HasExited then child.Kill(true))
        Console.CancelKeyPress.AddHandler stop
        try child.WaitForExit();child.ExitCode
        finally
            Console.CancelKeyPress.RemoveHandler stop
            if not child.HasExited then child.Kill(true);child.WaitForExit()
