namespace GraphExtra
open Convex.FSharp
open GraphContracts
module Functions =
    let square = Convex.query (fun _ (args: Amount) -> async { return args.amount * args.amount })
    let code = Convex.queryWithContracts (Contract.arguments<unit>()) Codecs.code (fun _ () -> async { return Code "native" })
    let codes = Convex.queryWithContracts (Contract.arguments<unit>()) (Contract.array Codecs.code) (fun _ () -> async { return [|Code "one";Code "two"|] })

    let seed = Convex.mutation (fun (ctx:MutationCtx) () -> async {
        for value in 1..12 do
            let! _ = ctx.Db.Insert(Tables.rows,{name="row-"+string value;value=value})
            ()
        return 12
    })
    let page = Convex.query (fun (ctx:QueryCtx) (options:PaginationOptions) -> ctx.Db.Query(Tables.rows).Paginate options)

    let advancedPage = Convex.query (fun (ctx:QueryCtx) (options:AdvancedPaginationOptions) -> ctx.Db.Query(Tables.rows).PaginateAdvanced options)
    let append = Convex.mutation (fun (ctx:MutationCtx) (args:Amount) -> async {
        let! _ = ctx.Db.Insert(Tables.rows,{name="additional-"+string args.amount;value=args.amount})
        return args.amount
    })
