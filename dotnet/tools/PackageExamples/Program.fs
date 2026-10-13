// Build-owned producer for native examples and handler-free client contracts.
open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Convex.Runtime
open Convex.FSharp
open Convex.DotNet.Execution

let root=Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__,"../../.."))
let digest (bytes:byte[])=SHA256.HashData bytes |> Convert.ToHexStringLower
let write (target:string) (name:string) (content:string)=File.WriteAllText(Path.Combine(target,name),content,UTF8Encoding(false))
type Example = {
    Directory: string
    Assembly: string
    Namespace: string
    Load: unit -> SchemaDefinition * NativeComponentDefinition * (string*string*(string*IRegisteredFunction) array) array
}
let examples = [|
    {Directory="fleet-management";Assembly="Fleet";Namespace="FleetManagement"
     Load=(fun () -> FleetManagement.NativeDeclarations.schema,FleetManagement.NativeDeclarations.app,FleetManagement.NativeDeclarations.modules)}
    {Directory="order-processing";Assembly="OrderProcessing";Namespace="OrderProcessing"
     Load=(fun () -> OrderProcessing.NativeDeclarations.schema,OrderProcessing.NativeDeclarations.app,OrderProcessing.NativeDeclarations.modules)}
|]

let package (example:Example) =
    let ownerAssembly=Assembly.Load(AssemblyName example.Assembly)
    let assemblyPath=ownerAssembly.Location
    let bin=Path.GetDirectoryName assemblyPath
    let contractName=example.Assembly+".Contracts"
    let dependencies=[|Path.Combine(bin,contractName+".dll")|]
    let selector name=example.Namespace+".NativeDeclarations:"+name
    NativeAssemblyReview.review (File.ReadAllBytes assemblyPath) (dependencies |> Array.map File.ReadAllBytes)
        [|selector "modules";selector "schema";selector "app"|] |> ignore
    use tables=TableRegistry.enterScope()
    use environment=InvocationRuntime.enter {
        Now=(fun () -> invalidOp "Example packaging has no invocation clock")
        Random=(fun () -> invalidOp "Example packaging has no invocation randomness")
        EnvironmentVariable=(fun _ -> invalidOp "Example packaging has no deployment environment")
    }
    let schema,app,modules=example.Load()
    schema.ValidateDeclarations()
    let functions=ResizeArray<NativeClientFunction>()
    let capsules=ResizeArray<string*string>()
    for durable,ownerModule,registrations in modules do
        let owner=ownerAssembly.GetType(ownerModule,true,false)
        let properties=owner.GetProperties(BindingFlags.Public ||| BindingFlags.Static)
                       |> Array.filter(fun property -> typeof<IRegisteredFunction>.IsAssignableFrom property.PropertyType && property.GetIndexParameters().Length=0)
        let aliases=registrations |> Array.map(fun(name,definition) ->
            let matches=properties |> Array.filter(fun property -> Object.ReferenceEquals(definition,property.GetValue(null)))
            if matches.Length<>1 then invalidOp ("A native example export needs one compiled owner: "+durable+":"+name)
            let address=durable+":"+name
            functions.Add(NativeClientCatalog.functionOfRegistration address definition)
            {functionPath=address;entryPoint=owner.FullName+":"+matches[0].Name})
        capsules.Add(durable+".js",DeploymentCapsule.createWithAliases assemblyPath dependencies NativeModuleKind.Functions None None aliases)
    use schemaJson=JsonDocument.Parse(schema.Export())
    capsules.Add("schema.js",DeploymentCapsule.create assemblyPath dependencies NativeModuleKind.Schema (Some(selector "schema")) (Some schemaJson.RootElement))
    use appJson=JsonDocument.Parse(app.Export())
    capsules.Add("convex.config.js",DeploymentCapsule.create assemblyPath dependencies NativeModuleKind.Component (Some(selector "app")) (Some appJson.RootElement))
    let code=NativeClientCatalog.render ("Convex.Examples."+example.Assembly) (Set.singleton contractName) [] app [||] (functions.ToArray())
    let target=Path.Combine(root,"artifacts/native-examples",example.Directory)
    let contractTarget=Path.Combine(target,"contracts")
    Directory.CreateDirectory contractTarget |> ignore
    write target ".incomplete" "Native example packaging is in progress.\n"
    for file in Directory.GetFiles(target,"*.js") do File.Delete file
    for name,source in capsules do write target name source
    let pins=capsules |> Seq.map(fun(name,source) -> {|path="root/"+name;sha256=digest(Encoding.UTF8.GetBytes source)|}) |> Seq.toArray
    let project={|
        version=1
        functionsDirectory="convex/"
        rootDirectory="."
        rootDependencies=([||]:string array)
        components=([||]:obj array)
        capsules=pins
    |}
    write target "project.json" (JsonSerializer.Serialize(project)+"\n")
    write contractTarget "NativeApi.fs" code
    write contractTarget "native-schema.json" (schema.Export()+"\n")
    let provenance={|
        version=2
        executor="convex-dotnet"
        language="fsharp"
        assemblySha256=digest(File.ReadAllBytes assemblyPath)
        contractAssemblySha256=digest(File.ReadAllBytes dependencies[0])
        canonicalDeclarationsSha256=digest(File.ReadAllBytes typeof<IBoundaryContract>.Assembly.Location)
        runtimeContractsSha256=digest(File.ReadAllBytes typeof<IRegisteredFunction>.Assembly.Location)
        capsules=pins
    |}
    write contractTarget "native-provenance.json" (JsonSerializer.Serialize(provenance)+"\n")
    File.Delete(Path.Combine(target,".incomplete"))
    printfn "Packaged native example %s and its handler-free client contracts." example.Directory

if Environment.GetCommandLineArgs().Length<>1 then invalidArg "arguments" "The native example producer takes no arguments"
for example in examples do package example
