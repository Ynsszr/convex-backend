namespace GraphRoot
open Convex.FSharp
open GraphContracts
module Functions =
    let private readRef attributes =
        FunctionReference.publicQuery "counter:read" (Contract.arguments<unit>()) (Contract.derived<int>())
        |> FunctionReference.atComponentExport "probe" attributes
    let private writeRef attributes =
        FunctionReference.publicMutation "counter:increment" (Contract.arguments<Amount>()) (Contract.derived<int>())
        |> FunctionReference.atComponentExport "probe" attributes
    let readProbe = Convex.query (fun (ctx:QueryCtx) () -> ctx.RunQuery(readRef ["read"],()))
    let writeProbe = Convex.mutation (fun (ctx:MutationCtx) (args:Amount) -> ctx.RunMutation(writeRef ["increment"],args))
    let readLeaf = Convex.query (fun (ctx:QueryCtx) () -> ctx.RunQuery(readRef ["leaf";"read"],()))
    let writeLeaf = Convex.mutation (fun (ctx:MutationCtx) (args:Amount) -> ctx.RunMutation(writeRef ["leaf";"increment"],args))
    let rollback = Convex.mutation (fun (ctx:MutationCtx) (args:Amount) -> async {
        let! _ = ctx.RunMutation(writeRef ["increment"],args)
        let! _ = ctx.RunMutation(writeRef ["leaf";"increment"],args)
        return (invalidOp "Rollback the complete transaction" : int)
    })
module Declarations =
    let schema = Schema.define [TableSchema.defineForTable Tables.rows]
    let app = NativeComponentDefinitions.app [] [NativeComponentDefinitions.mount "probe" "../probe"] NativeComponentDefinitions.emptyExports None
