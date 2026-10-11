namespace ConvexComponent.Backend
open Convex.FSharp
open ConvexComponent
module Counter =
    let read = Convex.query (fun (ctx:QueryCtx) () -> async {
        let! current = ctx.Db.Query(Tables.counters).Unique()
        return current |> Option.map _.Value |> Option.defaultValue 0
    })
    let increment = Convex.mutation (fun (ctx:MutationCtx) (args:Increment) -> async {
        let! current = ctx.Db.Query(Tables.counters).UniqueDoc()
        let next = args.Amount + (current |> Option.map (fun row -> row.Data.Value) |> Option.defaultValue 0)
        match current with
        | Some row -> do! ctx.Db.Replace(Tables.counters,row.Id,{Value=next})
        | None -> let! _ = ctx.Db.Insert(Tables.counters,{Value=next}) in ()
        return next
    })
