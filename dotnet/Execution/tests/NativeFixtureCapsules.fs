module Convex.DotNet.TestFixtures.NativeFixtureCapsules

open System
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Security.Cryptography
open System.Text.Json
open Convex.FSharp
open Convex.DotNet.Execution

// Read build metadata without loading or executing the fixture's declarations.
let validateConfiguration expected (assembly:string) =
    if expected<>"Debug" && expected<>"Release" then invalidArg "expected" "An exact Debug or Release fixture configuration is required"
    use input=File.OpenRead assembly
    use image=new PEReader(input)
    let reader=image.GetMetadataReader()
    let configurations =
        [| for handle in reader.GetAssemblyDefinition().GetCustomAttributes() do
               let attribute=reader.GetCustomAttribute handle
               if attribute.Constructor.Kind=HandleKind.MemberReference then
                   let constructor=reader.GetMemberReference(MemberReferenceHandle.op_Explicit attribute.Constructor)
                   if constructor.Parent.Kind=HandleKind.TypeReference then
                       let owner=reader.GetTypeReference(TypeReferenceHandle.op_Explicit constructor.Parent)
                       if reader.GetString(owner.Namespace)="System.Reflection" && reader.GetString(owner.Name)="AssemblyConfigurationAttribute" then
                           let mutable value=reader.GetBlobReader attribute.Value
                           if value.ReadUInt16()<>1us then invalidOp "Malformed fixture build configuration"
                           yield value.ReadSerializedString() |]
    if configurations<>[|expected|] then invalidOp ("RuntimeFixture configuration does not match native acceptance: expected "+expected)

let packageFixture configuration (assembly:string) (directory:string) =
    let assembly=Path.GetFullPath assembly
    let directory=Path.GetFullPath directory
    validateConfiguration configuration assembly
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

