open System
open System.IO
open System.Diagnostics
open System.Security
open System.Text.Json
open System.Xml.Linq

// Advanced package-only fixture. The authored application stays independent of
// every SDK/backend project; this script is maintainer verification, not a build dependency.
let args=fsi.CommandLineArgs |> Array.skip 1
if args.Length<>3 then invalidArg "args" "Expected PACKAGE_FEED VERSION NEW_CONSUMER_DIRECTORY"
let feed=Path.GetFullPath args[0]
let version=args[1]
let target=Path.GetFullPath args[2]
let source=Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__,"../Execution/tests/GraphFixture"))
if Directory.Exists target || File.Exists target then invalidArg "target" "Select a new application directory."
Directory.CreateDirectory target |> ignore
let rec copy source destination =
    Directory.CreateDirectory destination |> ignore
    for file in Directory.GetFiles source do
        if [".fs";".fsproj";".json"] |> List.contains(Path.GetExtension file) then File.Copy(file,Path.Combine(destination,Path.GetFileName file))
    for directory in Directory.GetDirectories source do
        if not(["bin";"obj";"artifacts";".convex"] |> List.contains(Path.GetFileName directory)) then
            copy directory (Path.Combine(destination,Path.GetFileName directory))
copy source target
let dependencies=["Convex.FSharp";"Convex.Client"]
for project in Directory.GetFiles(target,"*.fsproj",SearchOption.AllDirectories) do
    let document=XDocument.Load project
    for reference in document.Descendants(XName.Get "ProjectReference") |> Seq.toArray do
        let relative=reference.Attribute(XName.Get "Include").Value
        let name=Path.GetFileNameWithoutExtension relative
        if List.contains name dependencies then
            reference.ReplaceWith(XElement(XName.Get "PackageReference",XAttribute(XName.Get "Include",name),XAttribute(XName.Get "Version",version)))
        else
            let full=Path.GetFullPath(relative,Path.GetDirectoryName project)
            if not(full.StartsWith(target+string Path.DirectorySeparatorChar,StringComparison.Ordinal)) then
                invalidOp "The fixture retained an external source project reference."
    document.Save project
File.WriteAllText(Path.Combine(target,"Directory.Build.props"),"<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><Deterministic>true</Deterministic></PropertyGroup></Project>")
File.WriteAllText(Path.Combine(target,"NuGet.Config"),
    "<configuration><packageSources><clear/><add key=\"convex\" value=\""+SecurityElement.Escape feed+
    "\"/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources><packageSourceMapping>"+
    "<packageSource key=\"convex\"><package pattern=\"Convex.*\"/></packageSource><packageSource key=\"nuget.org\"><package pattern=\"*\"/></packageSource>"+
    "</packageSourceMapping></configuration>")
let tools=Path.Combine(target,".config")
Directory.CreateDirectory tools |> ignore
File.WriteAllText(Path.Combine(tools,"dotnet-tools.json"),JsonSerializer.Serialize {|version=1;isRoot=true;tools=dict [ "convex.dotnet.cli",{|version=version;commands=[|"convex-dotnet"|];rollForward=false|}]|})
let run arguments =
    let info=ProcessStartInfo("dotnet",UseShellExecute=false,WorkingDirectory=target)
    info.Environment["DOTNET_CLI_HOME"]<-Path.Combine(target,".dotnet-home")
    info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]<-"true"
    for argument:string in arguments do info.ArgumentList.Add argument
    use child=Process.Start info
    if not(child.WaitForExit 180000) then child.Kill(true);child.WaitForExit();invalidOp "Advanced consumer build timed out."
    if child.ExitCode<>0 then invalidOp "Advanced package consumer build failed."
run ["tool";"restore";"--configfile";"NuGet.Config"]
run ["convex-dotnet";"build";"Root/convex.project.json"]
run ["build";"Client/Graph.Client.fsproj";"-c";"Release";"--nologo"]
File.WriteAllText(Path.Combine(target,"consumer-result.json"),JsonSerializer.Serialize {|version=version;packageOnly=true;componentGraph=true;multipleAssemblies=true;authoredCodecs=true;generatedClientBuild=true;serviceStarted=false|})
printfn "PASS independent advanced component graph and generated client: %s" target
