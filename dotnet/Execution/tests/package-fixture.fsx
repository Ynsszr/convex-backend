#if CONVEX_RELEASE
#r "../../Runtime/src/Convex.Runtime/bin/Release/net10.0/Convex.Runtime.dll"
#r "../../FSharp/src/Convex.FSharp/bin/Release/net10.0/Convex.FSharp.dll"
#r "../src/Convex.DotNet/bin/Release/net10.0/Convex.DotNet.dll"
#else
#r "../../Runtime/src/Convex.Runtime/bin/Debug/net10.0/Convex.Runtime.dll"
#r "../../FSharp/src/Convex.FSharp/bin/Debug/net10.0/Convex.FSharp.dll"
#r "../src/Convex.DotNet/bin/Debug/net10.0/Convex.DotNet.dll"
#endif

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Convex.FSharp
open Convex.DotNet.Execution

let configuration =
#if CONVEX_RELEASE
    "Release"
#else
    "Debug"
#endif

let packageFixture () =
    let package=Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
    let assembly=Path.Combine(package,"tests/RuntimeFixture/bin",configuration,"net10.0/RuntimeFixture.dll")
    let digest=File.ReadAllBytes assembly |> SHA256.HashData |> Convert.ToHexStringLower
    use tables=TableRegistry.enterScope()
    use environment=InvocationRuntime.enter {
        Now=(fun () -> invalidOp "Fixture definitions do not require an offline clock")
        Random=(fun () -> invalidOp "Fixture definitions do not require offline randomness")
        EnvironmentVariable=(fun _ -> None)
    }
    use deployment=new DeploymentAssembly(assembly,digest,[||],"describe",frontendAssemblies=[|typeof<NativeFunctionDefinition>.Assembly|])
    let bytes=DeploymentCatalog.describe deployment |> InvocationProtocol.encode
    use catalog=JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
    let select collection export =
        catalog.RootElement.GetProperty(collection: string).EnumerateArray()
        |> Seq.find(fun value -> value.GetProperty("export").GetString()=export)
    let directory=Path.Combine(package,"artifacts/native-capsules")
    Directory.CreateDirectory directory |> ignore
    let write file kind export definition =
        let capsule=DeploymentCapsule.create assembly [||] kind export definition
        File.WriteAllText(Path.Combine(directory,file),capsule)
    write "Counter.js" NativeModuleKind.Functions None None
    write "NodeActions.js" NativeModuleKind.Functions None None
    let aliases=[|
        {functionPath="AliasCounter:read";entryPoint="AliasNames:read"}
        {functionPath="AliasCounter:increment";entryPoint="AliasNames:increment"}
        {functionPath="AliasCounter:handle";entryPoint="AliasNames:handle"}
        {functionPath="AliasCounter:metadata";entryPoint="AliasNames:metadata"}
    |]
    File.WriteAllText(Path.Combine(directory,"AliasCounter.js"),DeploymentCapsule.createWithAliases assembly [||] NativeModuleKind.Functions None None aliases)
    let schema=select "schemas" "Counter:schema"
    write "schema.js" NativeModuleKind.Schema (Some "Counter:schema") (Some(schema.GetProperty "value"))
    // Authentication is reevaluated against the owning environment snapshot;
    // its dynamic provider configuration is not frozen into the capsule.
    write "auth.config.js" NativeModuleKind.Auth (Some "AuthConfiguration:authentication") None
    let crons=select "crons" "Counter:crons"
    write "crons.js" NativeModuleKind.Crons (Some "Counter:crons") (Some(crons.GetProperty "value"))
    let router=select "httpRouters" "Counter:httpRouter"
    // Routing bindings remain part of the hash-bound uploaded source capsule.
    use definition=JsonDocument.Parse("{\"routes\":"+router.GetProperty("routes").GetRawText()+"}")
    write "http.js" NativeModuleKind.Http (Some "Counter:httpRouter") (Some definition.RootElement)
    printfn "Native-only deploy fixture capsules: %s" directory

packageFixture()
