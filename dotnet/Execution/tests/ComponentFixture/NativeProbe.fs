module NativeProbe

open Convex.FSharp

type CounterDocument={name:string;value:float}
type Name={name:string}
type Increment={name:string;amount:float}
let counters=Table.define<CounterDocument> "counters"
let schemaDefinition()=Schema.define [TableSchema.defineForTable counters |> fun table -> table.WithIndex("by_name",["name"])]
let schema=schemaDefinition()
let read=Convex.query(fun (ctx:QueryCtx) (args:Name) ->
    ctx.Db.Query(counters).WithIndex("by_name",fun range -> range.Eq("name",args.name)).Unique())
let increment=Convex.mutation(fun (ctx:MutationCtx) (args:Increment) -> async {
    let! row=ctx.Db.Query(counters).WithIndex("by_name",fun range -> range.Eq("name",args.name)).UniqueDoc()
    let value=(row |> Option.map(fun document -> document.Data.value) |> Option.defaultValue 0.)+args.amount
    match row with
    | Some current -> do! ctx.Db.Replace(current.Id,{name=args.name;value=value})
    | None -> let! _=ctx.Db.Insert(counters,{name=args.name;value=value}) in ()
    return {name=args.name;value=value}
})
let environment=Convex.query(fun _ (_:unit) -> async {return InvocationRuntime.environmentVariable "PROBE"})
