namespace Convex.DotNet.Development

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open Convex.Runtime
open Convex.FSharp
open Convex.DotNet.Execution
open Convex.DotNet.Deployment

/// One packaging implementation for root applications, nested reusable components,
/// referenced handler assemblies, and shared canonical or authored codecs.
module GraphPackaging =
    type private Binary = { Node: ApplicationGraph.Node; Assembly: string; Inputs: (string*string) array }
    type private Packaged = { Binary: Binary; Declaration: NativeComponentDefinition; Functions: NativeClientFunction array; Files: (string*string) array }
    let private text = ApplicationGraph.text
    let private optional = ApplicationGraph.optional
    let private digest path =
        use stream=File.OpenRead path
        SHA256.HashData stream |> Convert.ToHexStringLower
    let private json value = JsonSerializer.Serialize(value,JsonSerializerOptions(WriteIndented=true))
    let private noLinks path =
        let mutable current=Path.GetFullPath path
        while not(String.IsNullOrEmpty current) do
            let entry:FileSystemInfo=if Directory.Exists current then DirectoryInfo current else FileInfo current
            if entry.LinkTarget<>null then invalidOp "Native project paths cannot traverse symbolic links."
            current<-Path.GetDirectoryName current
    let private bundle node path =
        let path=Path.GetFullPath path
        if not(File.Exists path) then invalidOp "Build every backend/component project before packaging."
        let inputs=Directory.GetFiles(Path.GetDirectoryName path,"*.dll")
                   |> Array.filter(fun file -> not(List.contains (Path.GetFileNameWithoutExtension file) ["Convex.Runtime";"Convex.FSharp";"Convex.DotNet";"Convex.Client";"FSharp.Core"]))
                   |> Array.sort |> Array.map(fun file -> noLinks file;file,digest file)
        if inputs.Length>65 then invalidOp "Native application assembly closure exceeds its bound."
        {Node=node;Assembly=path;Inputs=inputs}
    let private selector primary (config:JsonElement) name =
        let value=config.GetProperty(name:string)
        if value.ValueKind=JsonValueKind.String then primary,value.GetString()
        else text "assembly" value,text "export" value
    let private simpleAssembly value =
        not(String.IsNullOrWhiteSpace value) && Regex.IsMatch(value,@"^[A-Za-z_][A-Za-z0-9_.-]*$")

    let package (graph:ApplicationGraph.Graph) (target: ApplicationGraph.Node -> string) =
        let root=graph.Root.Root
        let output=Path.GetFullPath(text "output" graph.Root.Configuration,root)
        if output=root || not(output.StartsWith(root+string Path.DirectorySeparatorChar,StringComparison.Ordinal)) then
            invalidOp "Generated output must be a child of the application directory."
        noLinks output
        let nodes=ApplicationGraph.nodes graph |> Array.map(fun node -> bundle node (target node))
        let originals=nodes |> Array.collect _.Inputs |> Array.distinct
        let inputs=Collections.Generic.Dictionary<string,string*string>(StringComparer.Ordinal)
        for file,pin in originals do
            let name=Path.GetFileNameWithoutExtension file
            match inputs.TryGetValue name with
            | true,(_,other) when other<>pin -> invalidOp ("Conflicting versions of shared native assembly: "+name)
            | true,_ -> ()
            | _ -> inputs.Add(name,(file,pin))
        let primary=nodes[0].Assembly
        let primaryName=Path.GetFileNameWithoutExtension primary
        let closure=inputs |> Seq.filter(fun pair -> pair.Key<>primaryName) |> Seq.map _.Value |> Seq.toArray
        use tables=TableRegistry.enterScope()
        use environment=InvocationRuntime.enter {
            Now=(fun () -> invalidOp "Packaging has no invocation clock.")
            Random=(fun () -> invalidOp "Packaging has no invocation randomness.")
            EnvironmentVariable=(fun _ -> invalidOp "Packaging has no deployment environment.") }
        use deployment=new DeploymentAssembly(primary,digest primary,closure,"describe",frontendAssemblies=[|typeof<SchemaDefinition>.Assembly|])
        let contracts=nodes |> Array.collect(fun node ->
            optional "contracts" node.Node.Configuration |> Option.map(fun values -> values.EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray) |> Option.defaultValue [||]) |> Set.ofArray
        let handlerOwners=nodes |> Array.collect(fun node ->
            [| yield Path.GetFileNameWithoutExtension node.Assembly
               for config in node.Node.Configuration.GetProperty("modules").EnumerateArray() do
                   yield optional "assembly" config |> Option.map _.GetString() |> Option.defaultValue(Path.GetFileNameWithoutExtension node.Assembly) |]) |> Set.ofArray
        if not((Set.intersect handlerOwners contracts).IsEmpty) then invalidOp "Client contracts must not name backend handler assemblies."
        let codecOwners=[ for owner in contracts do
                             if not(inputs.ContainsKey owner) then invalidOp ("Contract assembly is not in the compiled dependency closure: "+owner)
                             deployment.DeclarationsIn(owner, fun _ kind ->
                                 if typeof<IRegisteredFunction>.IsAssignableFrom kind then invalidOp "Client contract assemblies cannot export backend handlers."
                                 false) |> ignore
                             for identity,value in deployment.ValuesIn(owner,(fun _ kind -> typeof<IBoundaryContract>.IsAssignableFrom kind),validateCapturedState=true) do
                                 let split=identity.LastIndexOf(':')
                                 let expression=Declarations.qualifiedName(identity.Substring(0,split))+"."+Declarations.identifier(identity.Substring(split+1))
                                 yield value :?> IBoundaryContract,expression ]
        let packaged=nodes |> Array.map(fun binary ->
            let config=binary.Node.Configuration
            let defaultOwner=Path.GetFileNameWithoutExtension binary.Assembly
            let owned= binary.Inputs |> Array.map(fun(file,_) -> Path.GetFileNameWithoutExtension file,file) |> Map.ofArray
            let ownerInputs owner =
                if not(simpleAssembly owner) || not(owned.ContainsKey owner) then invalidOp ("Handler assembly is not referenced by this project: "+owner)
                owned[owner],(binary.Inputs |> Array.map fst |> Array.filter(fun file -> Path.GetFileNameWithoutExtension file<>owner))
            let declaration kind name =
                let owner,selected=selector defaultOwner config name
                ownerInputs owner |> ignore
                let result=deployment.DeclarationsIn(owner,fun identity _ -> identity=selected)
                match result with
                | [|_,(:? IDeclaration as value)|] when value.Kind=kind -> owner,selected,value
                | _ -> invalidOp ("Missing declaration or wrong declaration kind: "+selected)
            let appOwner,appSelector,app=declaration DeclarationKind.Component "component"
            let app=match app with :? NativeComponentDefinition as value -> value | _ -> invalidOp "A native F# component declaration is required."
            match binary.Node.DefinitionPath,app.Kind with
            | None,NativeComponentKind.App | Some _,NativeComponentKind.Child _ -> ()
            | _ -> invalidOp "Root projects require an app declaration and component projects require a child declaration."
            for child in app.Children do
                if not(Array.contains child.DefinitionPath binary.Node.Dependencies) then
                    invalidOp ("Mounted component is missing from the manifest graph: "+child.DefinitionPath)
            let files=ResizeArray<string*string>()
            let clients=ResizeArray<NativeClientFunction>()
            let names=Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            let add name contents =
                if not(names.Add name) then invalidOp ("Duplicate generated module: "+name)
                files.Add(name,contents)
            let capsule file owner kind export definition aliases =
                let assembly,dependencies=ownerInputs owner
                add file (DeploymentCapsule.createWithAliases assembly dependencies kind export definition aliases)
            for moduleConfig in config.GetProperty("modules").EnumerateArray() do
                let modulePath=text "path" moduleConfig
                let owner=optional "assembly" moduleConfig |> Option.map _.GetString() |> Option.defaultValue defaultOwner
                ownerInputs owner |> ignore
                let typeName=text "type" moduleConfig
                let registrations=deployment.DeclarationsIn(owner,fun identity kind ->
                    identity.StartsWith(typeName+":",StringComparison.Ordinal) && typeof<IRegisteredFunction>.IsAssignableFrom kind)
                if registrations.Length=0 then invalidOp ("No registered functions in "+typeName)
                if (optional "http" config).IsNone && registrations |> Array.exists(fun(_,value) -> (value :?> IRegisteredFunction).Kind=FunctionKind.HttpAction) then
                    invalidOp "HTTP handlers require an explicit native router."
                let aliases=registrations |> Array.choose(fun(identity,value) ->
                    let definition=value :?> IRegisteredFunction
                    if definition.Kind=FunctionKind.HttpAction then None else
                    let name=identity.Substring(identity.LastIndexOf(':')+1)
                    let alias=optional "aliases" moduleConfig |> Option.bind(optional name) |> Option.map _.GetString() |> Option.defaultValue name
                    if not(Regex.IsMatch(alias,@"^[A-Za-z_][A-Za-z0-9_]*$")) then invalidOp "Invalid function alias."
                    let address=modulePath+":"+alias
                    clients.Add(NativeClientCatalog.functionOfRegistration address definition)
                    Some {functionPath=address;entryPoint=identity})
                if aliases.Length>0 then capsule (modulePath+".js") owner NativeModuleKind.Functions None None aliases
            let schemaOwner,schemaSelector,schema=declaration DeclarationKind.Schema "schema"
            capsule "schema.js" schemaOwner NativeModuleKind.Schema (Some schemaSelector) (Some(schema.Export())) [||]
            capsule "convex.config.js" appOwner NativeModuleKind.Component (Some appSelector) (Some((app :> IDeclaration).Export())) [||]
            // Authentication is evaluated by the deployment owner against its
            // selected environment. Do not freeze environment values at codegen.
            match optional "auth" config with
            | Some _ ->
                let owner,selected=selector defaultOwner config "auth"
                ownerInputs owner |> ignore
                let mutable exists=false
                deployment.DeclarationsIn(owner,fun identity kind ->
                    if identity=selected && typeof<IDeclaration>.IsAssignableFrom kind then exists<-true
                    false) |> ignore
                if not exists then invalidOp "The selected authentication declaration is not exported."
                capsule "auth.config.js" owner NativeModuleKind.Auth (Some selected) None [||]
            | _ -> ()
            match optional "crons" config with
            | Some _ ->
                let owner,selected,crons=declaration DeclarationKind.CronJobs "crons"
                capsule "crons.js" owner NativeModuleKind.Crons (Some selected) (Some(crons.Export())) [||]
            | _ -> ()
            match optional "http" config with
            | Some _ ->
                let owner,selected=selector defaultOwner config "http"
                let assembly,dependencies=ownerInputs owner
                use routerAssembly=new DeploymentAssembly(assembly,digest assembly,dependencies |> Array.map(fun p -> p,digest p),"describe",frontendAssemblies=[|typeof<SchemaDefinition>.Assembly|])
                let bytes=DeploymentCatalog.describeSelected routerAssembly (Some "http") (Some selected) |> InvocationProtocol.encode
                use catalog=JsonDocument.Parse(ReadOnlyMemory<byte> bytes)
                let router=catalog.RootElement.GetProperty("httpRouters").EnumerateArray() |> Seq.exactlyOne
                let routes=router.GetProperty("routes").EnumerateArray() |> Seq.toArray
                let aliases=routes |> Array.map(text "functionPath") |> Array.distinct |> Array.sort
                            |> Array.map(fun entry ->
                                let pin=SHA256.HashData(Text.Encoding.UTF8.GetBytes entry) |> Convert.ToHexStringLower
                                {functionPath="httpHandlers:h_"+pin.Substring(0,24);entryPoint=entry})
                let addresses=aliases |> Array.map(fun value -> value.entryPoint,value.functionPath) |> Map.ofArray
                let definition=JsonSerializer.SerializeToElement {|routes=routes |> Array.map(fun route ->
                    {|method=text "method" route;path=text "path" route;functionPath=addresses[text "functionPath" route]|})|}
                if aliases.Length>0 then capsule "httpHandlers.js" owner NativeModuleKind.Functions None None aliases
                capsule "http.js" owner NativeModuleKind.Http (Some selected) (Some definition) aliases
            | _ -> ()
            {Binary=binary;Declaration=app;Functions=clients.ToArray();Files=files.ToArray()})
        let components=packaged |> Array.skip 1 |> Array.map(fun node ->
            {DefinitionPath=node.Binary.Node.DefinitionPath.Value;Declaration=node.Declaration;Functions=node.Functions})
        let namespaceName=optional "clientNamespace" graph.Root.Configuration |> Option.map _.GetString() |> Option.defaultValue "Convex.Generated"
        if not(Regex.IsMatch(namespaceName,@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")) then invalidOp "Invalid generated namespace."
        let code=NativeClientCatalog.render namespaceName contracts codecOwners packaged[0].Declaration components packaged[0].Functions
        for file,pin in originals do if digest file<>pin then invalidOp "Compiled input changed during packaging."
        if Directory.Exists output && not(File.Exists(Path.Combine(output,".convex-generated"))) then invalidOp "Refusing to replace an output directory not owned by convex-dotnet."
        Directory.CreateDirectory(Path.GetDirectoryName output) |> ignore
        let staging=output+".staging-"+Guid.NewGuid().ToString("N")
        Directory.CreateDirectory staging |> ignore
        try
            let rootDirectory=if graph.Components.Length=0 then "." else "root"
            let directories=packaged |> Array.mapi(fun i _ -> if i=0 then rootDirectory else "components/"+string i)
            let pins=ResizeArray<obj>()
            for i in 0..packaged.Length-1 do
                for name,source in packaged[i].Files do
                    let file=Path.Combine(staging,directories[i],name)
                    Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
                    File.WriteAllText(file,source)
                    let prefix=if i=0 then "root/" else "component("+packaged[i].Binary.Node.DefinitionPath.Value+")/"
                    pins.Add(box {|path=prefix+name;sha256=digest file|})
            let children=packaged |> Array.mapi(fun i node -> i,node) |> Array.skip 1 |> Array.map(fun(i,node) ->
                {|definitionPath=node.Binary.Node.DefinitionPath.Value;capsuleDirectory=directories[i];dependencies=node.Binary.Node.Dependencies|})
            File.WriteAllText(Path.Combine(staging,"NativeApi.fs"),code)
            File.WriteAllText(Path.Combine(staging,"project.json"),json {|version=1;functionsDirectory="convex/";rootDirectory=rootDirectory;
                rootDependencies=graph.Root.Dependencies;components=children;capsules=pins.ToArray()|})
            File.WriteAllText(Path.Combine(staging,"provenance.json"),json {|version=2;
                manifests=ApplicationGraph.nodes graph |> Array.map(fun node -> {|name=Path.GetRelativePath(root,node.Manifest);sha256=digest node.Manifest|});
                inputs=inputs |> Seq.map(fun item -> {|name=item.Key;sha256=snd item.Value|}) |> Seq.toArray|})
            File.WriteAllText(Path.Combine(staging,".convex-generated"),"convex-dotnet project output\n")
            NativeDeployment.prepareProjectFile(Path.Combine(staging,"project.json")) |> ignore
            if Directory.Exists output then
                let previous=output+".previous-"+Guid.NewGuid().ToString("N")
                Directory.Move(output,previous)
                try Directory.Move(staging,output)
                with _ -> Directory.Move(previous,output);reraise()
                Directory.Delete(previous,true)
            else Directory.Move(staging,output)
            printfn "Packaged %d functions across %d components: %s" (packaged |> Array.sumBy(fun item -> item.Functions.Length)) components.Length output
        finally if Directory.Exists staging then Directory.Delete(staging,true)
