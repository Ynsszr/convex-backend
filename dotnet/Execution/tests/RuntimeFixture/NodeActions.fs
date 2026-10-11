module NodeActions

open Convex.FSharp

// This module contains only actions, so the compatibility target can use the
// owning Node classifier while native deployment selects the CoreCLR runtime.
let increment = Convex.action (fun (context: ActionCtx) (args: Counter.Increment) ->
    context.RunMutation(FunctionRegistration.reference "Counter:increment" Counter.increment,args))
