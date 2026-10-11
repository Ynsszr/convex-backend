namespace Convex.FSharp

open System

/// Raw CLR contexts are confined to the frontend's native invocation boundary.
module internal ContextAdapters =
    let createQueryCtx (raw: obj) =
        match raw with :? QueryCtx as context -> context | _ -> invalidArg "context" "A query execution context is required"
    let createMutationCtx (raw: obj) =
        match raw with :? MutationCtx as context -> context | _ -> invalidArg "context" "A mutation execution context is required"
    let createActionCtx (raw: obj) =
        match raw with :? ActionCtx as context -> context | _ -> invalidArg "context" "An action execution context is required"

module internal AsyncInterop =
    let toTask (workflow: Async<'T>) = Async.StartAsTask workflow

/// F# builders preserve the operation, visibility, arguments and result through
/// the declaration. Only the runtime catalogue erases heterogeneous exports.
module Convex =
    let private register<'Kind, 'Visibility, 'Context, 'Args, 'Result>
        kind visibility (adapt: obj -> 'Context)
        (arguments: BoundaryContract<'Args>) (returns: BoundaryContract<'Result>)
        (handler: 'Context -> 'Args -> Async<'Result>) =
        if not(ValidatorCheck.isArgumentEnvelope arguments.Validator) then
            invalidArg "arguments" "Convex functions require an object argument envelope"
        let callback = Func<obj, obj, obj>(fun rawContext rawArguments ->
            let args=arguments.Decode rawArguments
            async {
                let! result=handler (adapt rawContext) args
                return returns.Encode result
            } |> AsyncInterop.toTask |> box)
        FunctionRegistration<'Kind, 'Visibility, 'Args, 'Result>(kind, visibility, arguments, returns, callback)

    let queryWithContracts (arguments: BoundaryContract<'Args>) (returns: BoundaryContract<'Result>)
        (handler: QueryCtx -> 'Args -> Async<'Result>) : FunctionRegistration<QueryKind, PublicVisibility, 'Args, 'Result> =
        register "query" "public" ContextAdapters.createQueryCtx arguments returns handler

    let query (handler: QueryCtx -> 'Args -> Async<'Result>) =
        queryWithContracts (Contract.arguments<'Args>()) (Contract.derived<'Result>()) handler

    let queryTask (handler: QueryCtx -> 'Args -> Threading.Tasks.Task<'Result>) =
        query (fun context args -> handler context args |> Async.AwaitTask)

    let queryWithContext (resolve: QueryCtx -> 'Args -> Async<'Scope>)
        (handler: 'Scope -> QueryCtx -> 'Args -> Async<'Result>) =
        query (fun context args -> async {
            let! scope=resolve context args
            return! handler scope context args
        })

    let internalQueryWithContracts (arguments: BoundaryContract<'Args>) (returns: BoundaryContract<'Result>)
        (handler: QueryCtx -> 'Args -> Async<'Result>) : FunctionRegistration<QueryKind, InternalVisibility, 'Args, 'Result> =
        register "query" "internal" ContextAdapters.createQueryCtx arguments returns handler

    let internalQuery (handler: QueryCtx -> 'Args -> Async<'Result>) =
        internalQueryWithContracts (Contract.arguments<'Args>()) (Contract.derived<'Result>()) handler

    let internalQueryTask (handler: QueryCtx -> 'Args -> Threading.Tasks.Task<'Result>) =
        internalQuery (fun context args -> handler context args |> Async.AwaitTask)

    let internalQueryWithContext (resolve: QueryCtx -> 'Args -> Async<'Scope>)
        (handler: 'Scope -> QueryCtx -> 'Args -> Async<'Result>) =
        internalQuery (fun context args -> async {
            let! scope=resolve context args
            return! handler scope context args
        })

    let mutationWithContracts (arguments: BoundaryContract<'Args>) (returns: BoundaryContract<'Result>)
        (handler: MutationCtx -> 'Args -> Async<'Result>) : FunctionRegistration<MutationKind, PublicVisibility, 'Args, 'Result> =
        register "mutation" "public" ContextAdapters.createMutationCtx arguments returns handler

    let mutation (handler: MutationCtx -> 'Args -> Async<'Result>) =
        mutationWithContracts (Contract.arguments<'Args>()) (Contract.derived<'Result>()) handler

    let mutationTask (handler: MutationCtx -> 'Args -> Threading.Tasks.Task<'Result>) =
        mutation (fun context args -> handler context args |> Async.AwaitTask)

    let mutationWithContext (resolve: MutationCtx -> 'Args -> Async<'Scope>)
        (handler: 'Scope -> MutationCtx -> 'Args -> Async<'Result>) =
        mutation (fun context args -> async {
            let! scope=resolve context args
            return! handler scope context args
        })

    let internalMutationWithContracts (arguments: BoundaryContract<'Args>) (returns: BoundaryContract<'Result>)
        (handler: MutationCtx -> 'Args -> Async<'Result>) : FunctionRegistration<MutationKind, InternalVisibility, 'Args, 'Result> =
        register "mutation" "internal" ContextAdapters.createMutationCtx arguments returns handler

    let internalMutation (handler: MutationCtx -> 'Args -> Async<'Result>) =
        internalMutationWithContracts (Contract.arguments<'Args>()) (Contract.derived<'Result>()) handler

    let internalMutationTask (handler: MutationCtx -> 'Args -> Threading.Tasks.Task<'Result>) =
        internalMutation (fun context args -> handler context args |> Async.AwaitTask)

    let internalMutationWithContext (resolve: MutationCtx -> 'Args -> Async<'Scope>)
        (handler: 'Scope -> MutationCtx -> 'Args -> Async<'Result>) =
        internalMutation (fun context args -> async {
            let! scope=resolve context args
            return! handler scope context args
        })

    let actionWithContracts (arguments: BoundaryContract<'Args>) (returns: BoundaryContract<'Result>)
        (handler: ActionCtx -> 'Args -> Async<'Result>) : FunctionRegistration<ActionKind, PublicVisibility, 'Args, 'Result> =
        register "action" "public" ContextAdapters.createActionCtx arguments returns handler

    let action (handler: ActionCtx -> 'Args -> Async<'Result>) =
        actionWithContracts (Contract.arguments<'Args>()) (Contract.derived<'Result>()) handler

    let actionTask (handler: ActionCtx -> 'Args -> Threading.Tasks.Task<'Result>) =
        action (fun context args -> handler context args |> Async.AwaitTask)

    let actionWithContext (resolve: ActionCtx -> 'Args -> Async<'Scope>)
        (handler: 'Scope -> ActionCtx -> 'Args -> Async<'Result>) =
        action (fun context args -> async {
            let! scope=resolve context args
            return! handler scope context args
        })

    let internalActionWithContracts (arguments: BoundaryContract<'Args>) (returns: BoundaryContract<'Result>)
        (handler: ActionCtx -> 'Args -> Async<'Result>) : FunctionRegistration<ActionKind, InternalVisibility, 'Args, 'Result> =
        register "action" "internal" ContextAdapters.createActionCtx arguments returns handler

    let internalAction (handler: ActionCtx -> 'Args -> Async<'Result>) =
        internalActionWithContracts (Contract.arguments<'Args>()) (Contract.derived<'Result>()) handler

    let internalActionTask (handler: ActionCtx -> 'Args -> Threading.Tasks.Task<'Result>) =
        internalAction (fun context args -> handler context args |> Async.AwaitTask)

    let internalActionWithContext (resolve: ActionCtx -> 'Args -> Async<'Scope>)
        (handler: 'Scope -> ActionCtx -> 'Args -> Async<'Result>) =
        internalAction (fun context args -> async {
            let! scope=resolve context args
            return! handler scope context args
        })

    let httpAction (handler: ActionCtx -> IHttpRequest -> Async<NativeHttpResponse>) =
        NativeHttpFunctionDefinition(Func<ActionCtx, IHttpRequest, Async<NativeHttpResponse>>(handler))

    let httpActionResponse (handler: ActionCtx -> IHttpRequest -> Async<HttpResponse>) =
        NativeHttpFunctionDefinition(Func<ActionCtx, IHttpRequest, Async<HttpResponse>>(handler))

    let httpActionRequest handler = httpAction handler
    let httpActionRequestResponse handler = httpActionResponse handler
    let httpActionTask (handler: ActionCtx -> IHttpRequest -> Threading.Tasks.Task<HttpResponse>) =
        httpActionResponse (fun context request -> handler context request |> Async.AwaitTask)

    /// A durable handle retains the originating kind, visibility and codecs.
    let createFunctionHandle (reference: FunctionReference<'Kind, 'Visibility, 'Args, 'Result>) = async {
        let target=reference.Address
        let field =
            match reference.Target with
            | FunctionTarget.Named _ -> "name"
            | FunctionTarget.Component _ -> "reference"
            | FunctionTarget.Handle _ -> "functionHandle"
        let arguments=NativeValue.createSimpleObject()
        NativeValue.setProperty arguments field (box target)
        NativeValue.setProperty arguments "version" (box "1.46.0")
        let! result=InvocationOperations.call "1.0/createFunctionHandle" arguments
        return FunctionReference.withHandle (Codec.decode<string> result) reference
    }

[<AutoOpen>]
module FunctionHandleExtensions =
    type QueryCtx with
        member _.CreateFunctionHandle(reference: FunctionReference<'Kind, 'Visibility, 'Args, 'Result>) =
            Convex.createFunctionHandle reference
    type MutationCtx with
        member _.CreateFunctionHandle(reference: FunctionReference<'Kind, 'Visibility, 'Args, 'Result>) =
            Convex.createFunctionHandle reference
    type ActionCtx with
        member _.CreateFunctionHandle(reference: FunctionReference<'Kind, 'Visibility, 'Args, 'Result>) =
            Convex.createFunctionHandle reference

type NativeComponentNamespace internal () =
    member _.Item with get(name: string) = Component.create name
    member _.Child(name: string) = Component.create name

module Components =
    let root () = NativeComponentNamespace()
    let generic () = root()
