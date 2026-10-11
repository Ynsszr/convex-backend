open System
open System.IO
open System.Diagnostics
open System.Security.Cryptography
open System.Text.Json
open System.Xml.Linq

let root=Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__,"../.."))
let args=fsi.CommandLineArgs |> Array.skip 1
if args.Length<>3 then invalidArg "arguments" "Expected PACKAGE_FEED COMPLETE_RUNTIME NEW_OUTPUT_DIRECTORY"
let feed=Path.GetFullPath args[0]
let runtime=Path.GetFullPath args[1]
let output=Path.GetFullPath args[2]
let version=XDocument.Load(Path.Combine(root,"Directory.Build.props")).Descendants(XName.Get "Version") |> Seq.exactlyOne |> _.Value
if File.Exists output || Directory.Exists output then invalidArg "output" "Choose a new immutable release directory."
let digest path =
    use stream=File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexStringLower
let invoke arguments =
    let info=ProcessStartInfo("dotnet",UseShellExecute=false)
    for argument:string in arguments do info.ArgumentList.Add argument
    use child=Process.Start info
    if not(child.WaitForExit(120000)) then child.Kill(true);child.WaitForExit();invalidOp "Runtime validation timed out."
    if child.ExitCode<>0 then invalidOp "Runtime validation failed."
invoke [Path.Combine(runtime,"cli/Convex.DotNet.Cli.dll");"runtime";"inspect";runtime]
use descriptor=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runtime,"runtime.json")))
if descriptor.RootElement.GetProperty("version").GetString()<>version then invalidOp "Package and runtime release versions must agree."
let staging=output+".staging-"+Guid.NewGuid().ToString("N")
Directory.CreateDirectory staging |> ignore
let copy source relative =
    let target=Path.Combine(staging,relative)
    Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
    File.Copy(source,target,false)
    if OperatingSystem.IsLinux() then File.SetUnixFileMode(target,File.GetUnixFileMode source)
try
    for id in ["Convex.Runtime";"Convex.FSharp";"Convex.Client";"Convex.DotNet";"Convex.DotNet.Cli";"Convex.Templates"] do
        let name=id+"."+version+".nupkg"
        copy (Path.Combine(feed,name)) ("packages/"+name)
    for file in Directory.GetFiles(runtime,"*",SearchOption.AllDirectories) do
        copy file ("runtime/"+Path.GetRelativePath(runtime,file))
    copy (Path.Combine(root,"dotnet/tools/Install.fsx")) "Install.fsx"
    for name in ["DEVELOPMENT.md";"PUBLISHING.md";"RELEASE_SCOPE.md";"CAPABILITIES.md";"CHANGELOG.md"] do copy (Path.Combine(root,"dotnet",name)) ("docs/"+name)
    File.WriteAllText(Path.Combine(staging,"README.md"),"# Convex for F# "+version+"\n\nInstall: `dotnet fsi Install.fsx`. The installed CLI creates and develops applications without a source checkout. Read docs/RELEASE_SCOPE.md for current scope and limits. This is a community native runtime, not managed Convex Cloud support.\n")
    let files=Directory.GetFiles(staging,"*",SearchOption.AllDirectories) |> Array.sort |> Array.map(fun file->
        {|path=Path.GetRelativePath(staging,file).Replace('\\','/');sha256=digest file|})
    File.WriteAllText(Path.Combine(staging,"release.json"),JsonSerializer.Serialize({|format="convex-fsharp-release";version=version;files=files|},JsonSerializerOptions(WriteIndented=true)))
    Directory.Move(staging,output)
    printfn "RELEASE %s" output
finally if Directory.Exists staging then Directory.Delete(staging,true)
