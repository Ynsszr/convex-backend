module Convex.DotNet.Host.Program

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Convex.Runtime
open Convex.DotNet.Execution

// This is the executable's frontend composition. The runtime library and its
// public contracts have no dependency on F# domain values, contexts or codecs.
let private frontend =
    Convex.FSharp.NativeExecution.FSharpExecutionFrontend() :> IExecutionFrontend

let private checkContract (definition: IRegisteredFunction) (contract: JsonElement) =
    if InvocationProtocol.stringField "visibility" contract<>ContractNames.visibility definition.Visibility then
        invalidOp "The native visibility differs from the deployed function contract"
    let require name (validator: JsonElement) =
        let actual=JsonNode.Parse(validator.GetRawText())
        let admitted=JsonNode.Parse(contract.GetProperty(name: string).GetRawText())
        if not(JsonNode.DeepEquals(actual, admitted)) then
            invalidOp ("The native " + name + " validator differs from the deployed function contract")
    let arguments=contract.GetProperty "arguments"
    if arguments.GetProperty("type").GetString()<>"any" then require "arguments" definition.Arguments
    if contract.GetProperty("returns").ValueKind<>JsonValueKind.Null then require "returns" definition.Returns

let private dependencyManifest (message: JsonElement) =
    let dependencies=message.GetProperty "assemblyDependencies"
    if dependencies.ValueKind<>JsonValueKind.Array || dependencies.GetArrayLength()>64 then
        invalidOp "A bounded deployment dependency manifest is required"
    dependencies.EnumerateArray()
    |> Seq.map(fun item -> InvocationProtocol.stringField "path" item, InvocationProtocol.stringField "sha256" item)
    |> Seq.toArray

let private optionalString (name: string) (message: JsonElement) =
    let mutable value=Unchecked.defaultof<JsonElement>
    if not(message.TryGetProperty(name, &value)) || value.ValueKind=JsonValueKind.Null then None
    elif value.ValueKind=JsonValueKind.String && not(String.IsNullOrWhiteSpace(value.GetString())) then Some(value.GetString())
    else invalidOp ("A nullable nonempty string is required for " + name)

let private load message kind =
    new DeploymentAssembly(
        InvocationProtocol.stringField "assemblyPath" message,
        InvocationProtocol.stringField "assemblySha256" message,
        dependencyManifest message,
        kind,
        frontendAssemblies=[|frontend.GetType().Assembly|])

let rec private owningError (error: exn) =
    match error with
    | :? Reflection.TargetInvocationException when not(isNull error.InnerException) -> owningError error.InnerException
    | :? TypeInitializationException when not(isNull error.InnerException) -> owningError error.InnerException
    | :? AggregateException as aggregate when aggregate.InnerExceptions.Count=1 -> owningError aggregate.InnerExceptions[0]
    | _ -> error

let private failure (error: exn) =
    let error=owningError error
    let fields =
        match box error with
        | :? IApplicationError as application ->
            ["code", box "ConvexError"; "message", box error.Message; "data", box application.EncodedData]
        | :? Convex.Runtime.HostOperationException as owner ->
            ["code", box owner.Code; "message", box owner.Message]
        | _ -> ["code", box "NativeExecutionError"; "message", box error.Message]
    RuntimeJson.objectValue fields

/// Fault diagnostics use the existing invocation log owner, never stderr or a
/// second delivery channel. They are bounded and cannot replace the actual error.
let private reportFault (host:IInvocationHost) (error:exn) =
    let error=owningError error
    match box error with
    | :? IApplicationError -> () // The deliberate message/data are already the result.
    | _ ->
        try
            let part maximum (value:string) =
                if isNull value then "" elif value.Length>maximum then value.Substring(0,maximum) else value
            let diagnostic=String.concat "\n" [part 256 (error.GetType().FullName);part 768 error.Message;part 2560 error.StackTrace]
            let mutable length=diagnostic.Length
            while Text.Encoding.UTF8.GetByteCount(diagnostic.AsSpan(0,length))>4093 do length<-length-1
            let message=diagnostic.Substring(0,length)+(if length<diagnostic.Length then "..." else "")
            host.Call("dotnet/log",RuntimeJson.element ["level",box "error";"messages",box [|message|]]) |> ignore
        with _ -> () // Log limits or a closed owner do not change invocation settlement.

/// Optional version one preparation is a trusted executable protocol. Old
/// requests retain their exact invocation capability and frame behavior.
let private preparationSelected (message:JsonElement) =
    let mutable selected=Unchecked.defaultof<JsonElement>
    if not(message.TryGetProperty("preparationProtocolVersion", &selected)) then false
    else
        if selected.ValueKind<>JsonValueKind.Number then
            invalidOp "Invalid native preparation protocol selection"
        let mutable version=0
        if not(selected.TryGetInt32(&version)) || (version<>0 && version<>1) then
            invalidOp "Unsupported native preparation protocol version"
        if version=1 && (InvocationProtocol.stringField "type" message<>"invoke" ||
                         not(List.contains (InvocationProtocol.stringField "kind" message) ["query";"mutation";"action"])) then
            invalidOp "Native preparation is available for query, mutation and action invocation"
        version=1

/// The lock protects preparation state only. Owning operations and frame writes
/// run outside it. A caught concurrent preflight refusal cannot open execution.
type private PreparationCapability(channel:IInvocationHost) =
    let gate=obj()
    let mutable executionOpened=false
    let mutable preparationSent=false
    let mutable refusedPreparationOperation=false
    let allowOperation () =
        lock gate (fun () ->
            if executionOpened then true else
            refusedPreparationOperation<-true
            false)
    let refused () = InvalidOperationException("Native preparation refuses invocation host operations")
    member _.OpenLegacy() = lock gate (fun () -> executionOpened<-true)
    member _.IsOpen = lock gate (fun () -> executionOpened)
    member _.Prepared(writeReady:unit -> Task<unit>) = task {
        lock gate (fun () ->
            if refusedPreparationOperation then invalidOp "Native preparation attempted an invocation host operation"
            if preparationSent then invalidOp "Native preparation can complete only once"
            preparationSent<-true)
        do! writeReady ()
        lock gate (fun () ->
            if refusedPreparationOperation then invalidOp "Native preparation attempted an invocation host operation"
            executionOpened<-true)
    }
    interface IInvocationHost with
        member _.InvocationId=channel.InvocationId
        member _.Call(name,arguments) =
            if not(allowOperation ()) then raise(refused())
            channel.Call(name,arguments)
        member _.CallAsync(name,arguments,asynchronous) =
            if allowOperation () then channel.CallAsync(name,arguments,asynchronous)
            else Task.FromException<JsonElement>(refused())

let private execute (input: Stream) (output: Stream) (message: JsonElement) (operation: IInvocationHost -> (unit -> Task<unit>) -> Task<obj>) = task {
    InvocationProtocol.checkVersion message
    let identity=InvocationProtocol.stringField "invocationId" message
    if String.IsNullOrWhiteSpace identity || identity.Length>256 then invalidOp "Invalid invocation identity"
    use channel=new InvocationChannel(input, output, identity)
    let response kind fields =
        RuntimeJson.objectValue (["type", box kind; "version", box 1; "invocationId", box identity] @ fields)
    let mutable selected=false
    let preparation=new PreparationCapability(channel :> IInvocationHost)
    let host=preparation :> IInvocationHost
    let prepared () = task {
        if selected then
            do! preparation.Prepared(fun () -> InvocationProtocol.write output (response "prepared" []))
    }
    try
        selected<-preparationSelected message
        if not selected then preparation.OpenLegacy()
        // Metadata discovery receives the same guarded capability as Invoke.
        // No handler or SDK environment callback can announce readiness.
        use scope=frontend.Enter(host)
        let! value=operation host prepared
        channel.Close()
        do! InvocationProtocol.write output (response "result" ["value", value])
    with error ->
        // Preflight failure returns its terminal error without invoking a log
        // syscall. After readiness the existing owning diagnostic is preserved.
        if InvocationProtocol.stringField "type" message="invoke" && preparation.IsOpen then
            reportFault host error
        channel.Close()
        do! InvocationProtocol.write output (response "error" ["error", failure error])
}

let private describe input output message =
    execute input output message (fun _ _ -> task {
        use deployment=load message "describe"
        return DeploymentCatalog.describeSelected deployment (optionalString "moduleKind" message) (optionalString "export" message)
    })

let private initializeComponent input output message =
    execute input output message (fun _ _ -> task {
        use deployment=load message "describe"
        let selected=InvocationProtocol.stringField "export" message
        let definitionPath=InvocationProtocol.stringField "definitionPath" message
        if String.IsNullOrWhiteSpace definitionPath || definitionPath.Length>2048 then
            invalidOp "The owning component definition path is required"
        let definition =
            match deployment.Declarations(fun identity _ -> identity=selected) with
            | [|_, (:? IComponentDefinition as definition)|] -> definition
            | _ -> invalidOp "The selected native declaration is not a component definition"
        if definition.IsApplication then invalidOp "The pinned initializer lifecycle belongs to child component definitions"
        let actual=JsonNode.Parse((definition :> IDeclaration).Export().GetRawText())
        let admitted=JsonNode.Parse(message.GetProperty("componentDefinition").GetRawText())
        if not(JsonNode.DeepEquals(actual, admitted)) then
            invalidOp "The component initializer definition differs from its owning admission"
        let arguments=message.GetProperty "args"
        if arguments.ValueKind<>JsonValueKind.Object then invalidOp "A component initializer requires an argument object"
        return definition.Initialize(InvocationProtocol.stringField "componentName" message, arguments) |> box
    })

let private invoke input output message =
    execute input output message (fun host prepared -> task {
        let kind=InvocationProtocol.stringField "kind" message
        use deployment=load message kind
        let entryPoint =
            optionalString "entryPoint" message
            |> Option.defaultWith(fun () -> InvocationProtocol.stringField "functionPath" message)
        let definition =
            match deployment.Resolve entryPoint with
            | NativeExport.Function definition | NativeExport.HttpAction definition -> definition
        if ContractNames.functionKind definition.Kind<>kind then
            invalidOp "The deployment function kind differs from its backend admission"
        let contract=message.GetProperty "functionContract"
        if definition.Kind=FunctionKind.HttpAction then
            if InvocationProtocol.stringField "visibility" contract<>ContractNames.visibility definition.Visibility then
                invalidOp "The native HTTP registration differs from its backend admission"
        else checkContract definition contract
        let arguments =
            if definition.Kind=FunctionKind.HttpAction then RuntimeJson.nullValue
            else message.GetProperty "args"
        let request =
            if definition.Kind=FunctionKind.HttpAction then message.GetProperty "httpRequest"
            else RuntimeJson.nullValue
        // Resolve has already admitted the exact getter and handler captures;
        // kind, deployed contract and request fields are checked before readiness.
        do! prepared ()
        let! result=definition.Invoke(host, arguments, request)
        return box result
    })

[<EntryPoint>]
let main arguments =
    let input=Console.OpenStandardInput()
    let output=Console.OpenStandardOutput()
    Console.SetOut(Console.Error)
    try
        match arguments with
        | [|"--describe"; path; digest|] ->
            let declarationHost =
                { new IInvocationHost with
                    member _.InvocationId="offline-description"
                    member _.CallAsync(_, _, _) = Task.FromException<JsonElement>(InvalidOperationException("Offline description has no host operations"))
                    member _.Call(name, _) =
                        if name="dotnet/environmentVariable" then RuntimeJson.nullValue
                        else invalidOp "Import clock and randomness require backend-owned describe" }
            use scope=frontend.Enter declarationHost
            use deployment=new DeploymentAssembly(path, digest, [||], "describe", frontendAssemblies=[|frontend.GetType().Assembly|])
            let catalog =
                deployment.Catalog()
                |> Array.map(fun (path, export) ->
                    let definition = match export with NativeExport.Function value | NativeExport.HttpAction value -> value
                    RuntimeJson.objectValue [
                        "functionPath", box path
                        "kind", box (ContractNames.functionKind definition.Kind)
                        "visibility", box (ContractNames.visibility definition.Visibility)
                        "args", box definition.Arguments
                        "returns", box definition.Returns
                    ])
            let bytes=InvocationProtocol.encode(RuntimeJson.objectValue ["version", box 1; "functions", box catalog])
            output.Write bytes
            0
        | [||] ->
            let mutable running=true
            while running do
                match InvocationProtocol.read input |> fun value -> value.GetAwaiter().GetResult() with
                | None -> running<-false
                | Some message ->
                    let pending =
                        match InvocationProtocol.stringField "type" message with
                        | "invoke" -> invoke input output message
                        | "describe" -> describe input output message
                        | "initializeComponent" -> initializeComponent input output message
                        | _ -> invalidOp "Unsupported native executor frame"
                    pending.GetAwaiter().GetResult()
            0
        | _ -> invalidArg "arguments" "Usage: Convex.DotNet.Host [--describe ASSEMBLY SHA256]"
    with error ->
        Console.Error.WriteLine("Native executor stopped: " + error.Message)
        1
