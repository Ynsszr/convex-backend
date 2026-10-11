#r "../src/Convex.DotNet/bin/Debug/net10.0/Convex.FSharp.dll"
#r "../src/Convex.DotNet/bin/Debug/net10.0/Convex.DotNet.dll"

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Convex.DotNet
open Convex.DotNet.Execution

let package=Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let host=Path.Combine(package, "src/Convex.DotNet.Host/bin/Debug/net10.0/Convex.DotNet.Host.dll")
let assembly=Path.Combine(package, "tests/RuntimeFixture/bin/Debug/net10.0/RuntimeFixture.dll")
let digest=File.ReadAllBytes assembly |> SHA256.HashData |> Convert.ToHexStringLower
let start=ProcessStartInfo("dotnet")
start.ArgumentList.Add host
start.RedirectStandardInput<-true
start.RedirectStandardOutput<-true
start.RedirectStandardError<-true
start.UseShellExecute<-false
let worker=new Process(StartInfo=start)
if not(worker.Start()) then failwith "Native descriptor did not start"
let diagnostics=worker.StandardError.ReadToEndAsync()
let input=worker.StandardOutput.BaseStream
let output=worker.StandardInput.BaseStream
let awaitTask (operation: Threading.Tasks.Task<'T>) = operation.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
let write value=InvocationProtocol.write output value |> fun operation -> operation.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
let describe identity kind selected expectedReads =
    write (HostArguments.objectValue [
        "type",box "describe";"version",box 1;"invocationId",box identity
        "assemblyPath",box assembly;"assemblySha256",box digest;"assemblyDependencies",box (Array.empty<obj>)
        "moduleKind",box kind;"export",box selected
        "timeMs",box 1_800_000_000_000.;"environmentVariables",HostArguments.empty()
    ])
    let mutable requests=0
    let mutable result=None
    while result.IsNone do
        let response=awaitTask (InvocationProtocol.read input) |> Option.defaultWith(fun () -> failwith "Native descriptor disconnected")
        if InvocationProtocol.stringField "invocationId" response<>identity then failwith "Descriptor changed invocation identity"
        match InvocationProtocol.stringField "type" response with
        | "syscall" ->
            requests<-requests+1
            if expectedReads=0 then failwith "Selected schema/functions/HTTP imports evaluated unrelated environment-reading authentication"
            let value=
                match InvocationProtocol.stringField "name" response with
                | "dotnet/environmentVariable" ->
                    match InvocationProtocol.stringField "name" (response.GetProperty "args") with
                    | "CONVEX_OIDC_ISSUER" -> box "https://native-descriptor.invalid"
                    | "CONVEX_OIDC_AUDIENCE" -> box "descriptor-contract"
                    | _ -> failwith "Unexpected import environment request"
                | _ -> failwith "Descriptor attempted a non-import operation"
            let error=
                if identity="describe-auth-missing" then
                    HostArguments.objectValue ["code",box "AuthConfigMissingEnvironmentVariable";"message",box "The owning authentication environment variable is missing"]
                else null
            write (HostArguments.objectValue [
                "type",box "syscallResult";"version",box 1;"invocationId",box identity
                "requestId",box (response.GetProperty("requestId").GetInt32());"value",value;"error",error
            ])
        | "result" -> result<-Some(Ok(response.GetProperty("value").Clone()))
        | "error" -> result<-Some(Error(response.GetProperty("error").Clone()))
        | _ -> failwith "Unexpected descriptor frame"
    if requests<>expectedReads then failwith "Import environment reads were not scoped to this descriptor"
    result.Value
let successful identity kind selected expectedReads =
    match describe identity kind selected expectedReads with
    | Ok catalog -> catalog
    | Error error -> failwith(error.GetRawText())
let values name (catalog: JsonElement) = catalog.GetProperty(name: string).EnumerateArray() |> Seq.toArray
let selected name path catalog = values name catalog |> Array.find(fun item -> item.GetProperty("export").GetString()=path)
try
    let catalog=successful "describe-full" null null 2
    if catalog.GetProperty("version").GetInt32()<>1 then failwith "Unsupported deployment catalogue"
    let functions=values "functions" catalog
    let lookup path=functions |> Array.find(fun item -> item.GetProperty("functionPath").GetString()=path)
    if (lookup "Counter:increment").GetProperty("kind").GetString()<>"mutation" then failwith "Mutation declaration lost"
    if not((lookup "Counter:increment").TryGetProperty("arguments") |> fst) then failwith "Wrong owning argument metadata key"
    if (lookup "Counter:httpEcho").GetProperty("kind").GetString()<>"httpAction" then failwith "HTTP declaration lost"
    let schema=selected "schemas" "Counter:schema" catalog
    if schema.GetProperty("value").GetProperty("tables").[0].GetProperty("tableName").GetString()<>"runtimeCounters" then failwith "Schema identity changed"
    let auth=selected "authConfigs" "AuthConfiguration:authentication" catalog
    if auth.GetProperty("value").GetProperty("providers").[0].GetProperty("domain").GetString()<>"https://native-descriptor.invalid" then failwith "Owner import environment was not used"
    let router=selected "httpRouters" "Counter:httpRouter" catalog
    let routes=router.GetProperty("routes").EnumerateArray() |> Seq.toArray
    if routes.Length<>3 || not(routes |> Array.exists(fun route -> route.GetProperty("path").GetString()="/native/prefix/*")) then failwith "Routing definition changed"
    for route in routes do
        let handler=lookup (route.GetProperty("functionPath").GetString())
        if handler.GetProperty("kind").GetString()<>"httpAction" then failwith "Route did not bind to an exported HTTP action"
    if catalog.GetProperty("componentDefinitions").GetArrayLength()<>0 then failwith "Unsupported components were fabricated"
    let schemaOnly=successful "describe-schema" "schema" "Counter:schema" 0
    if values "schemas" schemaOnly |> Array.length <>1 || values "authConfigs" schemaOnly |> Array.length <>0 then failwith "Schema selection evaluated unrelated declarations"
    let authOnly=successful "describe-auth" "auth" "AuthConfiguration:authentication" 2
    if values "authConfigs" authOnly |> Array.length <>1 || values "schemas" authOnly |> Array.length <>0 then failwith "Authentication selection evaluated unrelated declarations"
    let httpOnly=successful "describe-http" "http" "Counter:httpRouter" 0
    if values "httpRouters" httpOnly |> Array.length <>1 || values "authConfigs" httpOnly |> Array.length <>0 then failwith "HTTP selection evaluated unrelated authentication"
    let functionsOnly=successful "describe-functions" "functions" null 0
    if values "functions" functionsOnly |> Array.length <functions.Length || values "authConfigs" functionsOnly |> Array.length <>0 then failwith "Functions selection changed its registration catalogue"
    for identity,kind,export in ["describe-unknown","unknown",null;"describe-missing","schema","Counter:missing";"describe-mismatch","auth","Counter:schema"] do
        match describe identity kind export 0 with
        | Error _ -> ()
        | Ok _ -> failwith "Invalid selected deployment declaration was admitted"
    match describe "describe-auth-missing" "auth" "AuthConfiguration:authentication" 1 with
    | Error error when InvocationProtocol.stringField "code" error="AuthConfigMissingEnvironmentVariable" -> ()
    | _ -> failwith "An owning import error lost its code across reflection and native execution"
    worker.StandardInput.Close()
    if not(worker.WaitForExit(10_000)) || worker.ExitCode<>0 then failwith (awaitTask diagnostics)
    printfn "PASS: selected native declarations, schema without environment reads, dynamic owner auth, exact HTTP route bindings and invalid-selector refusal"
finally
    if not worker.HasExited then worker.Kill(true)
    worker.Dispose()
