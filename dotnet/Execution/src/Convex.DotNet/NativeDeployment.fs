namespace Convex.DotNet.Deployment

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Convex.DotNet

type NativeModule = private {Path:string;Source:string;SourceSha256:string;IsSchema:bool}
type PreparedDeployment = private {Modules:NativeModule array;Graph:JsonElement}
/// Read-only source metadata does not carry activation authority.
type NativeModuleSnapshot internal (path,source,sourceSha256,isSchema) =
    member _.Path:string=path
    member _.Source:string=source
    member _.SourceSha256:string=sourceSha256
    member _.IsSchema:bool=isSchema
type NativeDeploymentSnapshot internal (modules,graph) =
    member _.Modules:NativeModuleSnapshot array=Array.copy modules
    member _.Graph:JsonElement=graph
type NativeProjectComponent = {DefinitionPath:string;CapsuleDirectory:string;Dependencies:string array}
type NativeProject = {
    Version:int;FunctionsDirectory:string;RootDirectory:string;RootDependencies:string array
    Components:NativeProjectComponent array
}
type DeploymentPhase = Preparing | WaitingForSchema | Activating
type DeploymentRefusal = {Phase:DeploymentPhase;Status:int;Code:string}
type ActivationReceipt = {ModuleHashes:(string*string) array;OwningResponse:JsonElement}
type NativeDeploymentToken = {OperationId:string;StartPushSha256:string}
type NativeDeploymentConfirmation = {Token:NativeDeploymentToken;CommitTimestamp:string}
type ReceiptReadOutcome =
    | HistoricalCommit of NativeDeploymentConfirmation
    | UnresolvedReceipt
type ActivationOutcome =
    | Confirmed of ActivationReceipt
    | Refused of DeploymentRefusal
    | Uncertain of phase:DeploymentPhase

/// Aggregate deployment/review deadlines match serial native import admission.
/// Worker/UDF budgets remain independent; callers cannot replace these owning bounds.
module NativeDeploymentTimeouts =
    let preparing = TimeSpan.FromSeconds 300.
    let schema = TimeSpan.FromSeconds 60.
    let activating = TimeSpan.FromSeconds 90.
    let reviewTotal = TimeSpan.FromSeconds 360.
    let activationTotal = TimeSpan.FromSeconds 480.

/// Native deployment uses the fork's owning deploy2 transaction/activation
/// protocol. It neither imports application assemblies locally nor constructs
/// database transactions from several public API calls.
module NativeDeployment =
    let snapshot (prepared:PreparedDeployment) : NativeDeploymentSnapshot =
        NativeDeploymentSnapshot(
            prepared.Modules |> Array.map(fun value -> NativeModuleSnapshot(value.Path,value.Source,value.SourceSha256,value.IsSchema)),
            prepared.Graph.Clone())

    // The original deploy2 owner limits decompressed push bodies to 200 MB.
    // Reserve headroom while retaining each module's independent metadata owner.
    let private maximumRequest=192_000_000
    let private maximumResponse=4*1024*1024
    let private utf8=UTF8Encoding(false,true)
    let private json bytes =
        use document=JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
        document.RootElement.Clone()
    // Deployment carries opaque source text, not Convex values or worker IPC
    // frames. Its larger bound must not inherit the 16 MiB invocation limit.
    let private encode (value:obj)=JsonSerializer.SerializeToUtf8Bytes(value,JsonSerializerOptions(MaxDepth=128))
    let private objectValue=Convex.Runtime.RuntimeJson.objectValue
    let private moduleJson moduleValue=objectValue [
        "path",box moduleValue.Path;"source",box moduleValue.Source;"sourceMap",null;"environment",box "dotNet"
    ]

    /// Freeze the complete upstream compiler's deployment graph, including
    /// component definitions and dependencies. This client does not invent or
    /// flatten component definitions and does not execute uploaded source.
    let prepareGraph (graph:JsonElement) =
        if graph.ValueKind<>JsonValueKind.Object then invalidArg "graph" "A compiled owning deployment graph is required"
        let required=Set.ofList ["functions";"appDefinition";"componentDefinitions";"nodeDependencies";"dryRun"]
        let allowed=required+Set.ofList ["nodeVersion";"forCodegen"]
        let fields=graph.EnumerateObject() |> Seq.map(fun field -> field.Name) |> Seq.toArray
        let actual=Set.ofArray fields
        if actual.Count<>fields.Length || not(required.IsSubsetOf actual && actual.IsSubsetOf allowed) then invalidArg "graph" "The graph must contain the owning start-push fields, without credentials"
        let mutable codegen=Unchecked.defaultof<JsonElement>
        if graph.GetProperty("dryRun").GetBoolean() || (graph.TryGetProperty("forCodegen",&codegen) && codegen.GetBoolean()) then
            invalidArg "graph" "An activation graph cannot be a dry run or codegen request"
        let functions=graph.GetProperty("functions").GetString()
        if String.IsNullOrWhiteSpace functions || functions.Length>256 || functions.Contains("..") || Path.IsPathFullyQualified functions then
            invalidArg "graph" "A bounded relative functions directory is required"
        let modules=ResizeArray<NativeModule>()
        let mutable total=0L
        let collect prefix functionsField (definition:JsonElement) =
            let one (moduleValue:JsonElement) =
                let path=moduleValue.GetProperty("path").GetString()
                let source=moduleValue.GetProperty("source").GetString()
                if String.IsNullOrWhiteSpace path || path.Length>1024 || path.Contains("..") || Path.IsPathFullyQualified path ||
                   path |> Seq.exists Char.IsControl then invalidArg "graph" "A deployment module has an invalid owning path"
                let bytes=utf8.GetBytes source
                total<-total+int64 bytes.Length
                if bytes.Length=0 || bytes.Length>Execution.InvocationProtocol.maximumFrame || total>int64 maximumRequest/2L then
                    invalidArg "graph" "Compiled module sources exceed their deployment bounds"
                let environment=moduleValue.GetProperty("environment").GetString()
                if not(List.contains environment ["isolate";"node";"dotNet"]) then invalidArg "graph" "Unknown compiled module environment"
                if environment="dotNet" then
                    let capsule=json bytes
                    if capsule.GetProperty("format").GetString()<>"convex-dotnet-capsule" || capsule.GetProperty("version").GetInt32()<>1 then
                        invalidArg "graph" "A native module requires a version-one capsule"
                modules.Add {Path=prefix+path;Source=source;SourceSha256=SHA256.HashData(bytes) |> Convert.ToHexStringLower;IsSchema=path="schema.js"}
                if modules.Count>1024 then invalidArg "graph" "A compiled graph has too many modules"
            let declaration=definition.GetProperty "definition"
            if declaration.ValueKind<>JsonValueKind.Null then one declaration
            let schema=definition.GetProperty("schema")
            if schema.ValueKind<>JsonValueKind.Null then one schema
            for moduleValue in definition.GetProperty(functionsField:string).EnumerateArray() do one moduleValue
        collect "root/" "changedModules" (graph.GetProperty "appDefinition")
        for definition in graph.GetProperty("componentDefinitions").EnumerateArray() do
            let path=definition.GetProperty("definitionPath").GetString()
            if String.IsNullOrWhiteSpace path || path.Length>2048 || path |> Seq.exists Char.IsControl then
                invalidArg "graph" "A component has an invalid owning definition path"
            collect ("component("+path+")/") "functions" definition
        if modules |> Seq.map(fun item -> item.Path) |> Seq.distinct |> Seq.length <> modules.Count then
            invalidArg "graph" "Duplicate module addresses in a deployment definition are not permitted"
        if (encode (box graph)).Length>maximumRequest-8192 then invalidArg "graph" "Compiled deployment graph exceeds its encoded bound"
        {Modules=modules.ToArray();Graph=graph.Clone()}

    let prepareGraphFile (path:string) =
        let info=FileInfo path
        if not(Path.IsPathFullyQualified path) || not info.Exists || info.LinkTarget<>null || info.Length<=0L || info.Length>int64 maximumRequest then
            invalidArg "path" "A bounded regular compiled graph file is required"
        use input=info.OpenRead()
        use frozen=new MemoryStream()
        let chunk=Array.zeroCreate<byte> 65536
        let mutable reading=true
        while reading do
            let count=input.Read(chunk,0,chunk.Length)
            if count=0 then reading<-false
            else
                if frozen.Length+int64 count>int64 maximumRequest then invalidArg "path" "Compiled graph exceeded its byte bound"
                frozen.Write(chunk,0,count)
        prepareGraph(json(frozen.ToArray()))

    let private freezeDirectory (directory:string) allowComponents =
        if not(Path.IsPathFullyQualified directory) || not(Directory.Exists directory) then
            invalidArg "directory" "An absolute native capsule directory is required"
        let rec capsuleFiles path =
            let current=DirectoryInfo path
            if current.LinkTarget<>null then invalidArg "directory" "Capsule directories cannot be symbolic links"
            seq {
                yield! Directory.GetFiles(path,"*.js")
                for child in Directory.GetDirectories path do yield! capsuleFiles child
            }
        let files=capsuleFiles directory |> Seq.truncate 129 |> Seq.sort |> Seq.toArray
        if files.Length=0 || files.Length>128 then invalidArg "directory" "A native deployment requires 1 to 128 capsule modules"
        let mutable aggregate=0L
        let modules=files |> Array.map(fun file ->
            let info=FileInfo file
            if info.LinkTarget<>null || info.Length<=0L || info.Length>int64 Execution.InvocationProtocol.maximumFrame then
                invalidArg "directory" "Capsules must be bounded regular files"
            use input=info.OpenRead()
            use frozen=new MemoryStream()
            let chunk=Array.zeroCreate<byte> 65536
            let mutable reading=true
            while reading do
                let count=input.Read(chunk,0,chunk.Length)
                if count=0 then reading<-false
                else
                    if frozen.Length+int64 count>int64 Execution.InvocationProtocol.maximumFrame then
                        invalidArg "directory" "Capsule source exceeded its file bound"
                    frozen.Write(chunk,0,count)
            let bytes=frozen.ToArray()
            aggregate<-aggregate+int64 bytes.Length
            if aggregate>int64 maximumRequest/2L then invalidArg "directory" "Native capsule source exceeds its aggregate bound"
            let capsule=json bytes
            if capsule.GetProperty("format").GetString()<>"convex-dotnet-capsule" || capsule.GetProperty("version").GetInt32()<>1 then
                invalidArg "directory" "Only version-one native capsules can be deployed by this client"
            let kind=capsule.GetProperty("moduleKind").GetString()
            if not(List.contains kind ["functions";"schema";"auth";"http";"crons"]) && not(allowComponents && kind="component") then
                invalidArg "directory" "This root deployment client cannot publish component definitions"
            let path=Path.GetRelativePath(directory,file).Replace(Path.DirectorySeparatorChar,'/')
            let schema=kind="schema"
            if schema<> (path="schema.js") then invalidArg "directory" "A schema capsule must have the owning schema.js address"
            if (kind="component")<>(path="convex.config.js") then invalidArg "directory" "A component declaration uses the owning convex.config.js address"
            {Path=path;Source=utf8.GetString bytes;SourceSha256=SHA256.HashData(bytes) |> Convert.ToHexStringLower;IsSchema=schema})
        if modules |> Array.filter(fun item -> item.IsSchema) |> Array.length >1 then invalidArg "directory" "A root deployment has one schema"
        modules

    let prepareDirectory (directory:string) (functionsDirectory:string) =
        let modules=freezeDirectory directory false
        let schema=modules |> Array.tryFind(fun item -> item.IsSchema) |> Option.map moduleJson |> Option.defaultValue null
        objectValue [
            "functions",box functionsDirectory
            "appDefinition",objectValue [
                "definition",null;"dependencies",box (Array.empty<obj>);"schema",schema
                "changedModules",box (modules |> Array.filter(fun item -> not item.IsSchema) |> Array.map moduleJson)
                "unchangedModuleHashes",box (Array.empty<obj>);"udfServerVersion",box "1.46.0"
            ]
            "componentDefinitions",box (Array.empty<obj>);"nodeDependencies",box (Array.empty<obj>);"nodeVersion",null
            "dryRun",box false;"forCodegen",box false
        ] |> encode |> json |> prepareGraph

    /// Construct a complete native graph in the original owning start-push
    /// format. Import evaluation, mount validation and instantiation stay in
    /// Convex; this compiler never executes application declarations locally.
    let prepareProject (project:NativeProject) =
        if project.Version<>1 || isNull project.Components || project.Components.Length>32 then
            invalidArg "project" "A version-one bounded native project is required"
        let validPath (path:string) =
            not(String.IsNullOrWhiteSpace path) && path.Length<=2048 && not(Path.IsPathFullyQualified path) &&
            not(path.Contains '\\') && not(path |> Seq.exists Char.IsControl)
        let definitions=Collections.Generic.Dictionary<string,NativeProjectComponent>(StringComparer.Ordinal)
        for entry in project.Components do
            if not(validPath entry.DefinitionPath) || not(definitions.TryAdd(entry.DefinitionPath,entry)) then
                invalidArg "project" "Native component definition paths must be unique and relative"
        let dependencies (values:string array) =
            if isNull values || values.Length>32 || (Array.distinct values).Length<>values.Length ||
               values |> Array.exists(fun path -> not(validPath path) || not(definitions.ContainsKey path)) then
                invalidArg "project" "A native dependency must identify one supplied component definition"
            values |> Array.map box
        dependencies project.RootDependencies |> ignore
        let visiting=Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let visited=Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let rec visit path =
            if not(visited.Contains path) then
                if not(visiting.Add path) then invalidArg "project" "A native component dependency graph cannot contain a cycle"
                let entry=definitions[path]
                dependencies entry.Dependencies |> ignore
                for dependency in entry.Dependencies do visit dependency
                visiting.Remove path |> ignore
                visited.Add path |> ignore
        for entry in project.Components do visit entry.DefinitionPath
        let freeze directory requireDefinition =
            let modules=freezeDirectory directory true
            let definition=modules |> Array.tryFind(fun value -> value.Path="convex.config.js")
            if requireDefinition && definition.IsNone then invalidArg "project" "A native component needs its owning declaration capsule"
            let schema=modules |> Array.tryFind(fun value -> value.IsSchema)
            let functions=modules |> Array.filter(fun value -> not value.IsSchema && value.Path<>"convex.config.js")
            definition |> Option.map moduleJson |> Option.defaultValue null,
            schema |> Option.map moduleJson |> Option.defaultValue null,
            functions |> Array.map moduleJson
        let rootDefinition,rootSchema,rootModules=freeze project.RootDirectory (project.RootDependencies.Length>0)
        let components=project.Components |> Array.map(fun entry ->
            let definition,schema,functions=freeze entry.CapsuleDirectory true
            objectValue [
                "definitionPath",box entry.DefinitionPath;"definition",definition;"schema",schema
                "dependencies",box (dependencies entry.Dependencies);"functions",box functions;"udfServerVersion",box "1.46.0"
            ])
        objectValue [
            "functions",box project.FunctionsDirectory
            "appDefinition",objectValue [
                "definition",rootDefinition;"schema",rootSchema;"dependencies",box (dependencies project.RootDependencies)
                "changedModules",box rootModules;"unchangedModuleHashes",box (Array.empty<obj>);"udfServerVersion",box "1.46.0"
            ]
            "componentDefinitions",box components;"nodeDependencies",box (Array.empty<obj>);"nodeVersion",null
            "dryRun",box false;"forCodegen",box false
        ] |> encode |> json |> prepareGraph

    let prepareProjectFile (path:string) =
        let info=FileInfo path
        if not(Path.IsPathFullyQualified path) || not info.Exists || info.LinkTarget<>null || info.Length<=0L || info.Length>65536L then
            invalidArg "path" "A bounded regular native project manifest is required"
        if File.Exists(Path.Combine(info.DirectoryName,".incomplete")) then invalidArg "path" "A native project build is incomplete"
        use input=info.OpenRead()
        use frozen=new MemoryStream()
        let buffer=Array.zeroCreate<byte> 4096
        let mutable reading=true
        while reading do
            let count=input.Read(buffer,0,buffer.Length)
            if count=0 then reading<-false else
            if frozen.Length+int64 count>65536L then invalidArg "path" "Native project manifest exceeded its bound"
            frozen.Write(buffer,0,count)
        let manifest=json(frozen.ToArray())
        let fields (expected:string array) (value:JsonElement) =
            if value.ValueKind<>JsonValueKind.Object then invalidArg "path" "A native project object is required"
            let names=value.EnumerateObject() |> Seq.map(fun field -> field.Name) |> Seq.toArray
            if names.Length<>expected.Length || Set.ofArray names<>Set.ofArray expected then invalidArg "path" "Native project fields must be exact and credential-free"
        let text name (value:JsonElement) = value.GetProperty(name:string).GetString()
        let texts name (value:JsonElement) = value.GetProperty(name:string).EnumerateArray() |> Seq.map(fun item -> item.GetString()) |> Seq.toArray
        // A saved project is relocatable. Absolute selections remain supported;
        // relative capsule paths belong to the manifest's directory.
        let directory name value =
            let selected=text name value
            if String.IsNullOrWhiteSpace selected || selected.Length>4096 || selected |> Seq.exists Char.IsControl then
                invalidArg "path" "A bounded capsule directory selection is required"
            Path.GetFullPath(selected,Path.GetDirectoryName info.FullName)
        let mutable capsulePins=Unchecked.defaultof<JsonElement>
        let mutable cliArtifacts=Unchecked.defaultof<JsonElement>
        let pinned=manifest.TryGetProperty("capsules",&capsulePins)
        let cliPinned=manifest.TryGetProperty("cliArtifacts",&cliArtifacts)
        if cliPinned && not pinned then invalidArg "path" "CLI artifact provenance requires pinned native capsules"
        fields [|"version";"functionsDirectory";"rootDirectory";"rootDependencies";"components";if pinned then "capsules";if cliPinned then "cliArtifacts"|] manifest
        let componentValues=manifest.GetProperty("components")
        if componentValues.GetArrayLength()>32 then invalidArg "path" "Too many native components"
        let components=componentValues.EnumerateArray() |> Seq.map(fun value ->
            fields [|"definitionPath";"capsuleDirectory";"dependencies"|] value
            {DefinitionPath=text "definitionPath" value;CapsuleDirectory=directory "capsuleDirectory" value;Dependencies=texts "dependencies" value}) |> Seq.toArray
        let prepared=prepareProject {Version=manifest.GetProperty("version").GetInt32();FunctionsDirectory=text "functionsDirectory" manifest
                                     RootDirectory=directory "rootDirectory" manifest;RootDependencies=texts "rootDependencies" manifest;Components=components}
        let validDigest (value:string)=not(isNull value) && System.Text.RegularExpressions.Regex.IsMatch(value,"^[a-f0-9]{64}$")
        if pinned then
            if capsulePins.ValueKind<>JsonValueKind.Array || capsulePins.GetArrayLength()=0 || capsulePins.GetArrayLength()>1024 then invalidArg "path" "Bounded native capsule pins are required"
            let pins=capsulePins.EnumerateArray() |> Seq.map(fun value ->
                fields [|"path";"sha256"|] value
                let path=text "path" value
                let digest=text "sha256" value
                if String.IsNullOrEmpty path || not(validDigest digest) then invalidArg "path" "A capsule pin requires an exact owning path and SHA256"
                path,digest) |> Seq.toArray
            let expected=Map.ofArray pins
            let actual=prepared.Modules |> Array.map(fun value -> value.Path,value.SourceSha256) |> Map.ofArray
            if expected.Count<>pins.Length || expected<>actual then invalidArg "path" "Native capsule inventory or source hashes changed"
        if cliPinned then
            if cliArtifacts.ValueKind<>JsonValueKind.Array || cliArtifacts.GetArrayLength()=0 || cliArtifacts.GetArrayLength()>64 then invalidArg "path" "A bounded native CLI closure is required"
            let names=Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            for value in cliArtifacts.EnumerateArray() do
                fields [|"name";"sha256"|] value
                let name=text "name" value
                if String.IsNullOrEmpty name || name.Length>256 || Path.GetFileName name<>name || name.Contains('\\') || (name |> Seq.exists Char.IsControl) || not(names.Add name) || not(validDigest(text "sha256" value)) || not(name.EndsWith(".dll",StringComparison.Ordinal) || name.EndsWith(".deps.json",StringComparison.Ordinal) || name.EndsWith(".runtimeconfig.json",StringComparison.Ordinal)) then
                    invalidArg "path" "Native CLI artifacts require unique bounded basenames and SHA256"
        prepared

    let review (prepared:PreparedDeployment) =
        prepared.Modules |> Array.map(fun item -> item.Path,item.SourceSha256)

    let graphHash (prepared:PreparedDeployment) = encode(box prepared.Graph) |> SHA256.HashData |> Convert.ToHexStringLower

    let private endpoint (origin:Uri) (path:string) =
        if not origin.IsAbsoluteUri || origin.UserInfo<>"" || origin.AbsolutePath<>"/" || origin.Query<>"" || origin.Fragment<>"" ||
           (origin.Scheme<>"https" && not(origin.Scheme="http" && origin.IsLoopback)) then
            invalidArg "origin" "Use a pinned HTTPS origin or isolated loopback HTTP origin"
        Uri(origin,path)

    let validateOrigin origin=endpoint origin "/api/deploy2/start_push" |> ignore

    let validHex length (value:string) =
        not(isNull value) && value.Length=length && (value |> Seq.forall(fun c -> (c>='0' && c<='9') || (c>='a' && c<='f')))

    let private token expectedOperationId (value:JsonElement) =
        let fields=value.EnumerateObject() |> Seq.map(fun field -> field.Name) |> Seq.toArray
        if fields.Length<>2 || Set.ofArray fields<>Set.ofList ["operationId";"startPushSha256"] then
            invalidOp "An exact owning deployment receipt is required"
        let operationId=value.GetProperty("operationId").GetString()
        let digest=value.GetProperty("startPushSha256").GetString()
        if operationId<>expectedOperationId || not(validHex 32 operationId && validHex 64 digest) then
            invalidOp "Owning deployment receipt differs from the retained operation"
        {OperationId=operationId;StartPushSha256=digest}

    let private activateOwner (clock:TimeProvider) (origin:Uri) (adminKey:string) (prepared:PreparedDeployment) operationId
        (beforeTransition:DeploymentPhase -> unit) (recordPrepared:NativeDeploymentToken -> unit)
        (cancellation:CancellationToken) : Task<ActivationOutcome> = task {
        if String.IsNullOrWhiteSpace adminKey || adminKey.Length>8192 then invalidArg "adminKey" "A bounded deployment administrator credential is required"
        operationId |> Option.iter(fun value -> if not(validHex 32 value) then invalidArg "operationId" "A retained deployment operation identity is required")
        endpoint origin "/api/deploy2/start_push" |> ignore
        use handler=new SocketsHttpHandler(AllowAutoRedirect=false,UseCookies=false)
        use client=new HttpClient(handler,Timeout=Timeout.InfiniteTimeSpan)
        use totalTimer=new CancellationTokenSource(NativeDeploymentTimeouts.activationTotal,clock)
        use deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation,totalTimer.Token)
        let mutable phase=DeploymentPhase.Preparing
        let post (transitionCancellation:CancellationToken) path value = task {
            let bytes=encode value
            if bytes.Length>maximumRequest then invalidOp "Encoded deployment exceeds its request bound"
            use request=new HttpRequestMessage(HttpMethod.Post,endpoint origin path)
            request.Content<-new ByteArrayContent(bytes)
            request.Content.Headers.ContentType<-Headers.MediaTypeHeaderValue("application/json")
            // One send per state transition. A lost activation reply is not
            // permission to replay the owning commit.
            use! response=client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,transitionCancellation)
            use! body=response.Content.ReadAsStreamAsync(transitionCancellation)
            use buffer=new MemoryStream()
            let chunk=Array.zeroCreate<byte> 65536
            let mutable reading=true
            while reading do
                let! count=body.ReadAsync(Memory<byte>(chunk),transitionCancellation)
                if count=0 then reading<-false
                else
                    if buffer.Length+int64 count>int64 maximumResponse then raise(IOException("Owning deployment response exceeds its bound"))
                    buffer.Write(chunk,0,count)
            if not response.IsSuccessStatusCode then
                let status=int response.StatusCode
                let mutable code="DeploymentRefused"
                try
                    let error=json(buffer.ToArray())
                    let mutable value=Unchecked.defaultof<JsonElement>
                    if error.TryGetProperty("code",&value) && value.ValueKind=JsonValueKind.String then
                        let candidate=value.GetString()
                        if candidate.Length<=128 && not(candidate |> Seq.exists Char.IsControl) then code<-candidate
                with :? JsonException -> ()
                return Error {Phase=phase;Status=status;Code=code}
            else return Ok(json(buffer.ToArray()))
        }
        try
            beforeTransition phase
            let fields=prepared.Graph.EnumerateObject() |> Seq.map(fun field -> field.Name,box field.Value) |> Seq.toList
            let request=match operationId with None -> fields | Some value -> ("nativeDeploymentId",box value)::fields
            use preparationTimer=new CancellationTokenSource(NativeDeploymentTimeouts.preparing,clock)
            use preparationDeadline=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token,preparationTimer.Token)
            let! started=post preparationDeadline.Token "/api/deploy2/start_push" (objectValue (("adminKey",box adminKey)::request))
            match started with
            | Error refusal -> return if refusal.Status<500 then Refused refusal else Uncertain phase
            | Ok started ->
                // The owner hashes its complete prepared snapshot. Persist its
                // token before any subsequent state transition or commit send.
                operationId |> Option.iter(fun value -> started.GetProperty("nativeReceipt") |> token value |> recordPrepared)
                phase<-DeploymentPhase.WaitingForSchema
                beforeTransition phase
                use schemaTimer=new CancellationTokenSource(NativeDeploymentTimeouts.schema,clock)
                use schemaDeadline=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token,schemaTimer.Token)
                let mutable ready=false
                let mutable refused=None
                let mutable attempts=0
                while not ready && refused.IsNone && attempts<60 do
                    attempts<-attempts+1
                    let! status=post schemaDeadline.Token "/api/deploy2/wait_for_schema" (objectValue [
                        "adminKey",box adminKey;"schemaChange",box (started.GetProperty("schemaChange"));"timeoutMs",box 1000
                    ])
                    match status with
                    | Error error -> refused<-Some error
                    | Ok status ->
                        match status.GetProperty("type").GetString() with
                        | "complete" -> ready<-true
                        | "inProgress" -> ()
                        | _ -> refused<-Some {Phase=phase;Status=400;Code="SchemaNotReady"}
                match refused with
                | Some refusal -> return if refusal.Status<500 then Refused refusal else Uncertain phase
                | None when not ready -> return Uncertain phase
                | None ->
                    phase<-DeploymentPhase.Activating
                    beforeTransition phase
                    use activationTimer=new CancellationTokenSource(NativeDeploymentTimeouts.activating,clock)
                    use activationDeadline=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token,activationTimer.Token)
                    let! activated=post activationDeadline.Token "/api/deploy2/finish_push" (objectValue [
                        "adminKey",box adminKey;"startPush",box started;"dryRun",box false;"message",box "native .NET deployment"
                    ])
                    match activated with
                    | Error refusal -> return if refusal.Status<500 then Refused refusal else Uncertain phase
                    | Ok response -> return Confirmed {ModuleHashes=review prepared;OwningResponse=response}
        with
        | :? HttpRequestException | :? OperationCanceledException | :? JsonException | :? IOException
        | :? InvalidOperationException | :? Collections.Generic.KeyNotFoundException -> return Uncertain phase
    }

    let activateObserved origin adminKey prepared beforeTransition cancellation =
        activateOwner TimeProvider.System origin adminKey prepared None beforeTransition ignore cancellation

    let activateRecorded origin adminKey prepared operationId beforeTransition recordPrepared cancellation =
        activateOwner TimeProvider.System origin adminKey prepared (Some operationId) beforeTransition recordPrepared cancellation

    let activate origin adminKey prepared cancellation = activateObserved origin adminKey prepared ignore cancellation

    /// A matching immutable owning receipt proves this historical commit. It
    /// does not assert that the same deployment is currently active. Absence,
    /// mismatches and unreadable replies leave the operation unresolved.
    let readReceipt (origin:Uri) (adminKey:string) (expected:NativeDeploymentToken) (cancellation:CancellationToken) : Task<ReceiptReadOutcome> = task {
        if String.IsNullOrWhiteSpace adminKey || adminKey.Length>8192 ||
           not(validHex 32 expected.OperationId && validHex 64 expected.StartPushSha256) then
            invalidArg "receipt" "A bounded credential and retained owning receipt token are required"
        let address=endpoint origin "/api/deploy2/native_receipt"
        use handler=new SocketsHttpHandler(AllowAutoRedirect=false,UseCookies=false)
        use client=new HttpClient(handler,Timeout=Timeout.InfiniteTimeSpan)
        use deadline=CancellationTokenSource.CreateLinkedTokenSource cancellation
        deadline.CancelAfter(TimeSpan.FromSeconds 30.)
        try
            use request=new HttpRequestMessage(HttpMethod.Post,address)
            request.Content<-new ByteArrayContent(encode(objectValue ["adminKey",box adminKey;"operationId",box expected.OperationId]))
            request.Content.Headers.ContentType<-Headers.MediaTypeHeaderValue("application/json")
            use! response=client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token)
            if not response.IsSuccessStatusCode then return UnresolvedReceipt else
            use! body=response.Content.ReadAsStreamAsync(deadline.Token)
            use buffer=new MemoryStream()
            let chunk=Array.zeroCreate<byte> 4096
            let mutable reading=true
            while reading do
                let! count=body.ReadAsync(Memory<byte>(chunk),deadline.Token)
                if count=0 then reading<-false
                else
                    if buffer.Length+int64 count>65536L then raise(IOException("Owning receipt exceeds its bound"))
                    buffer.Write(chunk,0,count)
            let value=json(buffer.ToArray())
            if value.ValueKind=JsonValueKind.Null then return UnresolvedReceipt else
            let fields=value.EnumerateObject() |> Seq.map(fun field -> field.Name) |> Seq.toArray
            if fields.Length<>4 || Set.ofArray fields<>Set.ofList ["operationId";"startPushSha256";"commitTimestamp";"outcome"] then
                return UnresolvedReceipt else
            let operationId=value.GetProperty("operationId").GetString()
            let digest=value.GetProperty("startPushSha256").GetString()
            let timestamp=value.GetProperty("commitTimestamp").GetString()
            if operationId<>expected.OperationId || digest<>expected.StartPushSha256 || value.GetProperty("outcome").GetString()<>"committed" ||
               isNull timestamp || timestamp.Length=0 || timestamp.Length>32 || not(timestamp |> Seq.forall(fun c -> c>='0' && c<='9')) then
                return UnresolvedReceipt
            else return HistoricalCommit {Token=expected;CommitTimestamp=timestamp}
        with
        | :? HttpRequestException | :? OperationCanceledException | :? JsonException | :? IOException
        | :? InvalidOperationException | :? Collections.Generic.KeyNotFoundException -> return UnresolvedReceipt
    }
