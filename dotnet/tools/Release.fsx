open System
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Xml.Linq
open System.Security.Cryptography

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let version =
    XDocument.Load(Path.Combine(root, "Directory.Build.props")).Descendants(XName.Get "Version")
    |> Seq.exactlyOne |> fun value -> value.Value
let digest path =
    use stream=File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexStringLower
let run (executable:string) (arguments:string list) =
    let options=ProcessStartInfo(executable,UseShellExecute=false,WorkingDirectory=root)
    for argument in arguments do options.ArgumentList.Add argument
    use child=Process.Start options
    if not(child.WaitForExit(300000)) then
        child.Kill(true);child.WaitForExit()
        failwithf "%s exceeded its five-minute bound" executable
    if child.ExitCode<>0 then failwithf "%s exited with %d" executable child.ExitCode
let project relative = Path.Combine(root,relative)
let packages = [
    "dotnet/Templates/Convex.Templates.fsproj"
    "dotnet/Runtime/src/Convex.Runtime/Convex.Runtime.fsproj"
    "dotnet/FSharp/src/Convex.FSharp/Convex.FSharp.fsproj"
    "dotnet/Client/src/Convex.Client/Convex.Client.fsproj"
    "dotnet/Execution/src/Convex.DotNet/Convex.DotNet.fsproj"
    "dotnet/Execution/cli/Convex.DotNet.Cli.fsproj"
]
let pack (directory:string) =
    let directory=Path.GetFullPath directory
    Directory.CreateDirectory directory |> ignore
    let staging=Path.Combine(directory,".pack-"+Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory staging |> ignore
    try
        for selected in packages do run "dotnet" ["pack";project selected;"-c";"Release";"-o";staging;"--nologo"]
        let files=Directory.GetFiles(staging,"*.nupkg")
        if files.Length<>packages.Length then invalidOp "Not all expected packages were produced."
        // Never silently replace a package version already consumed by applications.
        for source in files do
            let target=Path.Combine(directory,Path.GetFileName source)
            if File.Exists target && digest source<>digest target then
                invalidOp ("Package version already exists with different bytes; increment Version before releasing: "+Path.GetFileName target)
        for source in files do
            let target=Path.Combine(directory,Path.GetFileName source)
            if not(File.Exists target) then File.Move(source,target)
            printfn "PACKAGE %s SHA256 %s" target (digest target)
    finally Directory.Delete(staging,true)

let ensureOutput rid (directory:string) =
    if rid<>"linux-x64" && rid<>"linux-arm64" then invalidArg "rid" "Native workers currently target Linux."
    let directory=Path.GetFullPath directory
    if Directory.Exists directory || File.Exists directory then invalidArg "output" "Select a new output directory."
    let staging=directory+".staging-"+Guid.NewGuid().ToString("N")
    Directory.CreateDirectory staging |> ignore
    File.WriteAllText(Path.Combine(staging,".incomplete"),"Runtime publication has not completed.\n")
    directory,staging
let publishManaged rid output =
    for selected,directory in [
        "dotnet/Execution/src/Convex.DotNet.Host/Convex.DotNet.Host.fsproj","host"
        "dotnet/Execution/cli/Convex.DotNet.Cli.fsproj","cli"
    ] do
        run "dotnet" ["publish";project selected;"-c";"Release";"-r";rid;"--self-contained";"false";"-o";Path.Combine(output,directory);"--nologo"]
    let notices=Path.Combine(output,"licenses")
    Directory.CreateDirectory notices |> ignore
    for relative in ["LICENSE.md";"dotnet/FSharp/LICENSE";"dotnet/FSharp/NOTICE";"dotnet/FSharp/PROVENANCE.md"] do
        File.Copy(project relative,Path.Combine(notices,relative.Replace('/','-')))
let finish format rid output backend =
    let files=Directory.GetFiles(output,"*",SearchOption.AllDirectories)
              |> Array.filter(fun path -> Path.GetFileName path<>".incomplete") |> Array.sort
              |> Array.map(fun path -> {|path=Path.GetRelativePath(output,path).Replace('\\','/');sha256=digest path|})
    let descriptor={|
        format=format;version=version;rid=rid;backend=backend
        host="host/Convex.DotNet.Host.dll";cli="cli/Convex.DotNet.Cli.dll"
        frameworkDependent=true;files=files
        requirements=[|".NET 10 CoreCLR";"bubblewrap";"Linux"|]
    |}
    File.WriteAllText(Path.Combine(output,"runtime.json"),JsonSerializer.Serialize(descriptor,JsonSerializerOptions(WriteIndented=true)))
    File.Delete(Path.Combine(output,".incomplete"))
    if format="convex-native-runtime" then
        // Use the same validator as consumers before exposing the requested directory.
        run "dotnet" [Path.Combine(output,"cli/Convex.DotNet.Cli.dll");"runtime";"inspect";output]
let publishRuntime rid directory produce =
    let destination,staging=ensureOutput rid directory
    try
        produce staging
        Directory.Move(staging,destination)
        printfn "ARTIFACT %s" destination
    finally
        if Directory.Exists staging then Directory.Delete(staging,true)
let managed rid directory =
    publishRuntime rid directory (fun output ->
        publishManaged rid output
        finish "convex-native-managed-runtime" rid output (null:string))
let runtime rid backend directory =
    let backend=Path.GetFullPath backend
    if not(File.Exists backend) then invalidArg "backend" "Select a built fork backend executable."
    publishRuntime rid directory (fun output ->
        Directory.CreateDirectory(Path.Combine(output,"backend")) |> ignore
        let destination=Path.Combine(output,"backend/convex-local-backend")
        File.Copy(backend,destination)
        File.SetUnixFileMode(destination,File.GetUnixFileMode backend)
        publishManaged rid output
        finish "convex-native-runtime" rid output "backend/convex-local-backend")

match fsi.CommandLineArgs |> Array.skip 1 |> Array.toList with
| ["packages";output] -> pack output
| ["managed-runtime";rid;output] -> managed rid output
| ["runtime";rid;backend;output] -> runtime rid backend output
| _ ->
    eprintfn "Release.fsx packages <feed> | managed-runtime <linux-x64|linux-arm64> <new-output> | runtime <rid> <built-backend> <new-output>"
    exit 1
