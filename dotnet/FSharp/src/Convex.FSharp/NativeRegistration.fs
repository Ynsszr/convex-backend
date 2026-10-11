namespace Convex.FSharp

open System

/// Checked native registration. Contexts come from the owning execution adapter;
/// all inputs/results retain the canonical Convex validators.
type NativeFunctionDefinition internal
    (kind: string, visibility: string, arguments: Validator, returns: Validator,
     handler: Func<obj, obj, obj>) =

    do
        if not (List.contains kind ["query"; "mutation"; "action"]) then
            invalidArg "kind" "Unsupported native function kind"
        if not (List.contains visibility ["public"; "internal"]) then
            invalidArg "visibility" "Unsupported native function visibility"
        if not (ValidatorCheck.isArgumentEnvelope arguments) then
            invalidArg "arguments" "Convex arguments require an object envelope"

    member _.Kind = kind
    member _.Visibility = visibility
    member _.Arguments = arguments
    member _.Returns = returns

    /// Executor-only inspection of the deferred entrypoint. Deployment IL is
    /// not permitted to call this accessor or Invoke directly; nested calls
    /// use the owning Convex context and its deployed function contract.
    member internal _.ExecutionHandler : Delegate = handler

    member internal _.Invoke(context: obj, rawArguments: obj) = task {
        let snapshot = RawValueCodec.normalize rawArguments
        ValidatorCheck.require arguments snapshot
        let pending = handler.Invoke(context, snapshot)
        let! result =
            match pending with
            | :? Threading.Tasks.Task<obj> as operation -> operation
            | _ -> invalidOp "A native function must return a .NET task"
        // Convex maps a top-level undefined function return to null. Field-level
        // absence is still handled exclusively by the canonical record codec.
        let value = if NativeValue.isUndefined result then null else result
        let encoded = RawValueCodec.normalize value
        ValidatorCheck.require returns encoded
        return encoded
    }


    interface Convex.Runtime.IRegisteredFunction with
        member _.Kind = Convex.Runtime.ContractNames.parseKind kind
        member _.Visibility = Convex.Runtime.ContractNames.parseVisibility visibility
        member _.Arguments = Convex.Runtime.RuntimeJson.parse(arguments.ToJson())
        member _.Returns = Convex.Runtime.RuntimeJson.parse(returns.ToJson())
        member _.ExecutionHandler = handler
        member this.Invoke(host, rawArguments, _) = task {
            let channel = NativeExecution.HostCapabilityAdapter(host) :> NativeExecution.ITransactionHostOperations
            use operations = NativeExecution.FunctionInvocation.enter channel
            let context =
                match kind with
                | "query" -> NativeExecution.NativeContexts.queryContext channel host.InvocationId visibility |> box
                | "mutation" -> NativeExecution.NativeContexts.mutationContext channel host.InvocationId visibility |> box
                | "action" -> NativeExecution.NativeActionContexts.actionContext channel host.InvocationId visibility |> box
                | _ -> invalidOp "A registered query, mutation or action is required"
            let! value = this.Invoke(context, ConvexWire.toNative(rawArguments.GetRawText()))
            return NativeExecution.HostValueCodec.element value
        }

/// The CLR catalogue can erase this type through IRegisteredFunction; authored
/// callers retain the selected kind, visibility and canonical contracts.
type FunctionRegistration<'Kind, 'Visibility, 'Args, 'Result> internal
    (kind, visibility, arguments: BoundaryContract<'Args>, returns: BoundaryContract<'Result>, handler: Func<obj, obj, obj>) =
    inherit NativeFunctionDefinition(kind, visibility, arguments.Validator, returns.Validator, handler)
    member _.ArgumentContract = arguments
    member _.ReturnContract = returns
    interface ICallableContract with
        member _.Arguments=arguments :> IBoundaryContract
        member _.Returns=returns :> IBoundaryContract

module FunctionRegistration =
    let reference name (definition: FunctionRegistration<'Kind, 'Visibility, 'Args, 'Result>) =
        FunctionReference.create<'Kind, 'Visibility, 'Args, 'Result>
            (Convex.Runtime.ContractNames.parseKind definition.Kind)
            (Convex.Runtime.ContractNames.parseVisibility definition.Visibility)
            name (FunctionReference.named name) definition.ArgumentContract definition.ReturnContract

    let erase (definition: #Convex.Runtime.IRegisteredFunction) : Convex.Runtime.IRegisteredFunction =
        definition :> Convex.Runtime.IRegisteredFunction

/// HTTP routing, request identity and stream lifetime belong to the backend.
/// This registration only supplies the portable action handler and response.
type NativeHttpFunctionDefinition internal (handler:Func<ActionCtx,IHttpRequest,Async<NativeHttpResponse>>) =
    new(handler:Func<ActionCtx,IHttpRequest,Async<HttpResponse>>) =
        NativeHttpFunctionDefinition(Func<ActionCtx,IHttpRequest,Async<NativeHttpResponse>>(fun context request -> async {
            let! value=handler.Invoke(context,request)
            return NativeHttpResponse.Buffered(Http.validate value)
        }))
    member _.Kind="httpAction"
    member _.Visibility="public"
    member internal _.ExecutionHandler : Delegate = handler
    member internal _.Invoke(context:ActionCtx,request:IHttpRequest) = task {
        let! response=handler.Invoke(context,request) |> Async.StartAsTask
        match response with
        | NativeHttpResponse.Buffered value -> return NativeHttpResponse.Buffered(Http.validate value)
        | NativeHttpResponse.Streamed value ->
            return NativeHttpResponse.Streamed(Http.stream value.Status value.Headers value.Body)
    }

    interface Convex.Runtime.IRegisteredFunction with
        member _.Kind = Convex.Runtime.FunctionKind.HttpAction
        member _.Visibility = Convex.Runtime.FunctionVisibility.Public
        member _.Arguments = Convex.Runtime.RuntimeJson.parse(Validator.Any.ToJson())
        member _.Returns = Convex.Runtime.RuntimeJson.nullValue
        member _.ExecutionHandler = handler
        member this.Invoke(host, _, requestMetadata) = task {
            let channel = NativeExecution.HostCapabilityAdapter(host) :> NativeExecution.ITransactionHostOperations
            use operations = NativeExecution.FunctionInvocation.enter channel
            let request = NativeExecution.NativeHttpRequests.create channel requestMetadata
            let context = NativeExecution.NativeActionContexts.httpActionContext channel host.InvocationId "public"
            let! response = this.Invoke(context, request)
            let! value = NativeExecution.NativeHttpRequests.sendResponse channel request.Method response |> Async.StartAsTask
            return NativeExecution.HostValueCodec.element value
        }

    interface Convex.Runtime.IHttpActionRegistration with
        member this.Function=this :> Convex.Runtime.IRegisteredFunction
