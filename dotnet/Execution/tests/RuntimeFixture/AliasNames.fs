module AliasNames

open Convex.FSharp

// The compatibility bundle is called AliasCounter.js. Native deployment binds
// those same durable addresses to this CLR module without rewriting handlers.
let read=Convex.query (fun (context:QueryCtx) (args:Counter.Name) ->
    context.RunQuery(FunctionRegistration.reference "Counter:read" Counter.read,args))
let increment=Convex.mutation (fun (context:MutationCtx) (args:Counter.Increment) ->
    context.RunMutation(FunctionRegistration.reference "Counter:increment" Counter.increment,args))

let handle=Convex.query (fun _ (_:unit) -> async {
    let! handle=Convex.createFunctionHandle(FunctionRegistration.reference "AliasCounter:read" read)
    return handle.Value
})

let metadata=Convex.query (fun (context:QueryCtx) (_:unit) -> context.Meta.GetFunctionMetadata())
