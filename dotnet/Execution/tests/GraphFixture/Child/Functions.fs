namespace GraphChild
open Convex.FSharp
open GraphContracts
module Counter =
    let read = Convex.query (fun (ctx:QueryCtx) () -> async {
        let! value=ctx.Db.Query(Tables.counters).Unique()
        return value |> Option.map _.value |> Option.defaultValue 0
    })
    let increment = Convex.mutation (fun (ctx:MutationCtx) (args:Amount) -> async {
        let! current=ctx.Db.Query(Tables.counters).UniqueDoc()
        let next=(current |> Option.map(fun doc -> doc.Data.value) |> Option.defaultValue 0)+args.amount
        match current with
        | None -> let! _=ctx.Db.Insert(Tables.counters,{value=next}) in ()
        | Some doc -> do! ctx.Db.Replace(Tables.counters,doc.Id,{value=next})
        return next
    })
module Declarations =
    let schema=Schema.define [TableSchema.defineForTable Tables.counters]
    let app=NativeComponentDefinitions.child "probe" [] [] [NativeComponentDefinitions.mount "inner" "../leaf"] (NativeComponentExport.Branch [
        "read",NativeComponentExport.Leaf(NativeComponentReference.Function "counter:read")
        "increment",NativeComponentExport.Leaf(NativeComponentReference.Function "counter:increment")
        "leaf",NativeComponentExport.Leaf(NativeComponentReference.Child("inner",[]))
    ])
