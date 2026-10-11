open System
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Text.Json.Nodes
open System.Security
open System.Xml.Linq

// Integration check against shipped packages, outside the source tree. No application checkout, backend service, or credentials.
let args=fsi.CommandLineArgs |> Array.skip 1
if args.Length<>3 then invalidArg "args" "Expected PACKAGE_FEED VERSION NEW_CONSUMER_DIRECTORY"
let feed=Path.GetFullPath args[0]
let version=args[1]
let root=Path.GetFullPath args[2]
if File.Exists root || Directory.Exists root then invalidArg "root" "Select a new consumer directory."
Directory.CreateDirectory root |> ignore
let run arguments expected =
    let start=ProcessStartInfo("dotnet",WorkingDirectory=root,UseShellExecute=false)
    // Template installation must not mutate the developer's global template registry.
    start.Environment["DOTNET_CLI_HOME"] <- Path.Combine(root,".dotnet-home")
    start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "true"
    start.Environment["DOTNET_NOLOGO"] <- "true"
    for argument:string in arguments do start.ArgumentList.Add argument
    use child=Process.Start start
    if not(child.WaitForExit(180000)) then child.Kill(true);child.WaitForExit();failwith "Consumer command timed out."
    if (child.ExitCode=0)<>expected then failwithf "Unexpected consumer command outcome: exit %d" child.ExitCode
let package=Path.Combine(feed,"Convex.Templates."+version+".nupkg")
run ["new";"install";package;"--force"] true
run ["new";"convex-fsharp";"-n";"PackageConsumer";"-o";"."] true
File.WriteAllText(Path.Combine(root,"NuGet.Config"),
    "<configuration><packageSources><clear/><add key=\"convex-preview\" value=\""+SecurityElement.Escape(feed)+
    "\"/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources><packageSourceMapping>"+
    "<packageSource key=\"convex-preview\"><package pattern=\"Convex.*\"/></packageSource>"+
    "<packageSource key=\"nuget.org\"><package pattern=\"*\"/></packageSource></packageSourceMapping></configuration>")
run ["tool";"restore";"--configfile";"NuGet.Config"] true
run ["convex-dotnet";"build";"convex.project.json"] true
run ["build";"Client/PackageConsumer.Client.fsproj";"-c";"Release";"--nologo"] true
let generated=Path.Combine(root,"artifacts/convex/NativeApi.fs")
let original=File.ReadAllText generated
run ["convex-dotnet";"codegen";"convex.project.json"] true
if File.ReadAllText generated<>original then failwith "Repeated generation changed the typed catalogue."
let manifest=Path.Combine(root,"artifacts/convex/project.json")
run ["convex-dotnet";"review-project";manifest] true
let capsule=Path.Combine(root,"artifacts/convex/messages.js")
let source=File.ReadAllText capsule
try
    File.WriteAllText(capsule,source+" ")
    run ["convex-dotnet";"review-project";manifest] false
finally File.WriteAllText(capsule,source)
// HTTP endpoints use native routes, not query/action client references.
let httpPath=Path.Combine(root,"artifacts/convex/http.js")
let checkHttp () =
    use http=JsonDocument.Parse(File.ReadAllText httpPath)
    if http.RootElement.GetProperty("moduleKind").GetString()<>"http" then failwith "HTTP route capsule is missing."
    let routes=http.RootElement.GetProperty("definition").GetProperty("routes").EnumerateArray() |> Seq.toArray
    if routes.Length<>1 || routes[0].GetProperty("method").GetString()<>"GET" ||
       routes[0].GetProperty("path").GetString()<>"/health" then
        failwith "Native route identity changed."
    let address=routes[0].GetProperty("functionPath").GetString()
    if not(Text.RegularExpressions.Regex.IsMatch(address,"^httpHandlers:h_[0-9a-f]{24}$")) then
        failwith "A CLR namespace leaked into the Convex HTTP module address."
    let aliases=http.RootElement.GetProperty("functionAliases").EnumerateArray() |> Seq.toArray
    if aliases.Length<>1 || aliases[0].GetProperty("functionPath").GetString()<>address ||
       aliases[0].GetProperty("entryPoint").GetString()<>"PackageConsumer.Backend.HttpEndpoints:health" then
        failwith "HTTP router does not bind its exact compiled handler."
    use handlers=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"artifacts/convex/httpHandlers.js")))
    if handlers.RootElement.GetProperty("moduleKind").GetString()<>"functions" ||
       handlers.RootElement.GetProperty("functionAliases").GetRawText()<>http.RootElement.GetProperty("functionAliases").GetRawText() then
        failwith "HTTP handler capsule and router bindings disagree."
checkHttp()
if original.Contains("HttpEndpoints",StringComparison.Ordinal) then failwith "HTTP routes leaked into the query client."
let projectPath=Path.Combine(root,"convex.project.json")
let projectSource=File.ReadAllText projectPath
let clientProgram=Path.Combine(root,"Client/Program.fs")
let clientSource=File.ReadAllText clientProgram
try
    let selected=JsonNode.Parse(projectSource).AsObject()
    selected["http"]<-JsonValue.Create("PackageConsumer.Backend.Messages:list")
    File.WriteAllText(projectPath,selected.ToJsonString())
    let priorHttp=File.ReadAllText httpPath
    run ["convex-dotnet";"codegen";"convex.project.json"] false
    if File.ReadAllText httpPath<>priorHttp then failwith "Failed generation replaced the last good output."
    selected["http"]<-JsonValue.Create("PackageConsumer.Backend.HttpEndpoints:router")
    selected["modules"]<-JsonArray()
    File.WriteAllText(projectPath,selected.ToJsonString())
    run ["convex-dotnet";"codegen";"convex.project.json"] true
    if File.Exists(Path.Combine(root,"artifacts/convex/messages.js")) then failwith "HTTP-only output retained removed function capsules."
    File.WriteAllText(clientProgram,"module PackageConsumer.Client\n[<EntryPoint>]\nlet main _ = 0\n")
    run ["build";"Client/PackageConsumer.Client.fsproj";"-c";"Release";"--nologo"] true
finally
    File.WriteAllText(projectPath,projectSource)
    File.WriteAllText(clientProgram,clientSource)
    run ["convex-dotnet";"codegen";"convex.project.json"] true
run ["build";"Client/PackageConsumer.Client.fsproj";"-c";"Release";"--nologo"] true
run ["build";"Web/PackageConsumer.Web.fsproj";"-c";"Release";"--nologo"] true
let client=XDocument.Load(Path.Combine(root,"Client/PackageConsumer.Client.fsproj"))
for reference in client.Descendants(XName.Get "ProjectReference") do
    let path=reference.Attribute(XName.Get "Include").Value
    if path.Contains("Backend",StringComparison.Ordinal) || Path.IsPathRooted path then
        failwith "Generated client leaked a backend/source-checkout reference."
File.WriteAllText(Path.Combine(root,"consumer-result.json"),JsonSerializer.Serialize({|version=version;packageOnly=true;
    template=true;backendBuild=true;clientBuild=true;webBuild=true;stableCodegen=true;tamperedCapsuleRefused=true;httpRoutesPackaged=true;httpOnlyClientBuild=true;wrongRouterRefused=true;serviceStarted=false|}))
printfn "PASS: independent package-only F# consumer, generated client, HTTP route packaging and capsule tamper refusal. %s" root
