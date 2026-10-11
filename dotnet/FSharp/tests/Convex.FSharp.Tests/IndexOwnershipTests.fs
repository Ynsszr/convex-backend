namespace Convex.FSharp.Tests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Convex.FSharp
open Convex.Runtime

module IndexOwnershipTests =
    type Item = {Name:string;Category:string;Embedding:float array}
    type ItemTable = class end
    let private table=Table.defineWithMarker<ItemTable,Item> "index_owner_items"
    let private proposal=table.Index("by_category_name",["Category";"Name"])
    let private searchProposal=table.SearchIndex("search_name","Name",["Category"])
    let private vectorProposal=table.VectorIndex("vector_embedding","Embedding",2,["Category"])
    let private definition=(TableSchema.defineForTable table).WithIndex(proposal).WithSearchIndex(searchProposal).WithVectorIndex(vectorProposal)
    let private declared=definition.BindIndex(table,proposal)
    let private search=definition.BindSearchIndex(table,searchProposal)
    let private vector=definition.BindVectorIndex(table,vectorProposal)
    let private bad action = Assert.Throws<ArgumentException>(Action action) |> ignore

    [<Fact>]
    let ``binding rejects unknown foreign mismatched kind and staged declarations`` () =
        bad(fun () -> definition.BindIndex(table,table.Index("missing",["Name"])) |> ignore)
        bad(fun () -> definition.BindIndex(table,{proposal with TableName="foreign_items"}) |> ignore)
        bad(fun () -> definition.BindIndex({table with Name="foreign_items"},proposal) |> ignore)
        bad(fun () -> definition.BindIndex(table,{proposal with Fields=["Name";"Category"]}) |> ignore)
        bad(fun () -> definition.BindIndex(table,table.Index("search_name",["Name"])) |> ignore)
        bad(fun () -> definition.BindSearchIndex(table,{searchProposal with TableName="foreign_items"}) |> ignore)
        bad(fun () -> definition.BindSearchIndex(table,{searchProposal with IndexName="missing"}) |> ignore)
        bad(fun () -> definition.BindSearchIndex(table,{searchProposal with IndexName=proposal.IndexName}) |> ignore)
        bad(fun () -> definition.BindSearchIndex(table,{searchProposal with SearchField="Category"}) |> ignore)
        bad(fun () -> definition.BindSearchIndex(table,{searchProposal with FilterFields=["Name"]}) |> ignore)
        bad(fun () -> definition.BindVectorIndex(table,{vectorProposal with Dimensions=3}) |> ignore)
        bad(fun () -> definition.BindVectorIndex(table,{vectorProposal with TableName="foreign_items"}) |> ignore)
        bad(fun () -> definition.BindVectorIndex(table,{vectorProposal with IndexName="missing"}) |> ignore)
        bad(fun () -> definition.BindVectorIndex(table,{vectorProposal with IndexName=proposal.IndexName}) |> ignore)
        bad(fun () -> definition.BindVectorIndex(table,{vectorProposal with VectorField="Name"}) |> ignore)
        bad(fun () -> definition.BindVectorIndex(table,{vectorProposal with FilterFields=["Name"]}) |> ignore)
        let staged=(TableSchema.defineForTable table).WithIndex(proposal,staged=true).WithSearchIndex(searchProposal,staged=true).WithVectorIndex(vectorProposal,staged=true)
        bad(fun () -> staged.BindIndex(table,proposal) |> ignore)
        bad(fun () -> staged.BindSearchIndex(table,searchProposal) |> ignore)
        bad(fun () -> staged.BindVectorIndex(table,vectorProposal) |> ignore)
        let ambiguous={definition with Indexes=definition.Indexes @ definition.Indexes}
        bad(fun () -> ambiguous.BindIndex(table,proposal) |> ignore)

    let private dispatch handler =
        let mutable calls=0
        let host=NativeFixtures.host(fun _ _ _ -> calls<-calls+1;Task.FromResult(RuntimeJson.nullValue))
        let definition=Convex.query(fun ctx () -> handler ctx) |> FunctionRegistration.erase
        let failure=Assert.ThrowsAny<Exception>(fun () -> NativeFixtures.invoke definition host NativeFixtures.emptyArguments |> ignore)
        Assert.Equal(0,calls)
        failure

    [<Fact>]
    let ``manual raw query without a table owner cannot consume a checked witness`` () =
        let raw =
            { new ITableQueryRaw with
                member _.FullTableScanRaw() = failwith "Unexpected raw call"
                member _.CreateIteratorRaw() = failwith "Unexpected raw call"
                member _.WithIndex(_,?rangeOps) = failwith "Unexpected raw call"
                member _.WithSearchIndexRaw(_,_,_,_) = failwith "Unexpected raw call"
                member _.Order(_) = failwith "Unexpected raw call"
                member _.FilterRaw(_) = failwith "Unexpected raw call"
                member _.CollectRaw() = failwith "Unexpected raw call"
                member _.TakeRaw(_) = failwith "Unexpected raw call"
                member _.FirstRaw() = failwith "Unexpected raw call"
                member _.UniqueRaw() = failwith "Unexpected raw call"
                member _.PaginateRaw(_,_) = failwith "Unexpected raw call"
                member _.PaginateAdvancedRaw(_) = failwith "Unexpected raw call" }
        let query=TableQuery<Item,ItemTable>(raw,Codec.decodeDocument<Item>) :> ITableQuery<Item,ItemTable>
        bad(fun () -> query.WithIndex(declared) |> ignore)
        bad(fun () -> query.WithSearchIndex(search,"text") |> ignore)

    [<Fact>]
    let ``checked ranges and table ownership refuse before native calls`` () =
        dispatch(fun ctx -> ctx.Db.Query<Item,ItemTable>("foreign_items").WithIndex(declared).Take(1)) |> ignore
        dispatch(fun ctx -> ctx.Db.Query(table).WithIndex(declared,fun r -> r.Eq("Name","skipped")).Take(1)) |> ignore
        dispatch(fun ctx -> ctx.Db.Query(table).WithIndex(declared,fun r -> r.Eq("Absent","x")).Take(1)) |> ignore
        dispatch(fun ctx -> ctx.Db.Query(table).WithIndex(declared,fun r -> r.Eq("Category","x").Eq("Category","x")).Take(1)) |> ignore
        dispatch(fun ctx -> ctx.Db.Query(table).WithIndex(declared,fun r -> r.Eq("Category","x").Gt("Category","x")).Take(1)) |> ignore
        dispatch(fun ctx -> ctx.Db.Query(table).WithIndex(declared,fun r -> r.Gt("Category","x").Lt("Name","x")).Take(1)) |> ignore
        dispatch(fun ctx -> ctx.Db.Query(table).WithIndex(declared,fun r -> r.Gt("Category","x").Gte("Category","x")).Take(1)) |> ignore
        dispatch(fun ctx -> ctx.Db.Query(table).WithSearchIndex(search,"text",fun f -> f.Eq("Name","outside")).Take(1)) |> ignore
        dispatch(fun ctx -> ctx.Db.Query(table).WithSearchIndex(search,"text",fun f -> {f with SearchField="Category"}).Take(1)) |> ignore
        dispatch(fun ctx -> ctx.Db.Query(table).WithSearchIndex(search,"text",fun f -> {f with QueryText="changed"}).Take(1)) |> ignore

    [<Fact>]
    let ``checked native query preserves canonical owner payload and cleanup`` () =
        let mutable rangeCallbacks=0
        let mutable queryCalls=0
        let mutable cleanups=0
        let host=NativeFixtures.host(fun name args _ ->
            match name with
            | "1.0/queryStream" ->
                queryCalls<-queryCalls+1
                let source=args.GetProperty("query").GetProperty("source")
                Assert.Equal("IndexRange",source.GetProperty("type").GetString())
                Assert.Equal("index_owner_items.by_category_name",source.GetProperty("indexName").GetString())
                Assert.Equal(2,source.GetProperty("range").GetArrayLength())
                Task.FromResult(RuntimeJson.parse """{"queryId":"owned-query"}""")
            | "1.0/queryStreamNext" -> Task.FromResult(RuntimeJson.parse """{"done":false,"value":{"Name":"one","Category":"allowed","Embedding":[1,2]}}""")
            | "1.0/queryCleanup" -> cleanups<-cleanups+1;Task.FromResult(RuntimeJson.nullValue)
            | other -> Task.FromException<JsonElement>(InvalidOperationException(other)))
        let query=Convex.query(fun ctx () ->
            ctx.Db.Table(table).Query().Order(QueryOrder.Asc).WithIndex(declared,fun r ->
                rangeCallbacks<-rangeCallbacks+1
                r.Eq("Category","allowed").Gte("Name","one")).Take(1)) |> FunctionRegistration.erase
        let result=NativeFixtures.invoke query host NativeFixtures.emptyArguments
        Assert.Equal("one",result[0].GetProperty("Name").GetString())
        Assert.Equal(1,queryCalls)
        Assert.Equal(1,rangeCallbacks)
        Assert.Equal(1,cleanups)

    [<Fact>]
    let ``range membership follows native equality permutation and implicit id rules`` () =
        CheckedIndexQuery.range ["Category";"Name"] (IndexRangeBuilder.Empty.Eq("Name","one").Eq("Category","allowed").Ops)
        CheckedIndexQuery.range ["Category";"Name"] (IndexRangeBuilder.Empty.Eq("Category","allowed").Eq("Name","one").Gt("_id","opaque").Ops)
        bad(fun () -> CheckedIndexQuery.range ["Category";"Name"] (IndexRangeBuilder.Empty.Eq("Category","allowed").Eq("Name","one").Gt("_creationTime",1.).Ops))

    [<Fact>]
    let ``checked native search uses canonical field filters and single callback`` () =
        let mutable callbacks=0
        let host=NativeFixtures.host(fun name args _ ->
            match name with
            | "1.0/queryStream" ->
                let source=args.GetProperty("query").GetProperty("source")
                Assert.Equal("index_owner_items.search_name",source.GetProperty("indexName").GetString())
                let filters=source.GetProperty("filters")
                Assert.Equal("Name",filters[0].GetProperty("fieldPath").GetString())
                Assert.Equal("Category",filters[1].GetProperty("fieldPath").GetString())
                Task.FromResult(RuntimeJson.parse """{"queryId":"owned-search"}""")
            | "1.0/queryStreamNext" -> Task.FromResult(RuntimeJson.parse """{"done":true}""")
            | _ -> Task.FromException<JsonElement>(InvalidOperationException(name)))
        let query=Convex.query(fun ctx () ->
            ctx.Db.Query(table).WithSearchIndex(search,"text",fun f -> callbacks<-callbacks+1;f.Eq("Category","allowed")).Take(1)) |> FunctionRegistration.erase
        let result=NativeFixtures.invoke query host NativeFixtures.emptyArguments
        Assert.Equal(0,result.GetArrayLength())
        Assert.Equal(1,callbacks)

    [<Fact>]
    let ``checked vector uses canonical fields dimensions and captured native expression`` () =
        let mutable calls=0
        let mutable callbacks=0
        let host=NativeFixtures.host(fun name args _ ->
            calls<-calls+1
            Assert.Equal("1.0/actions/vectorSearch",name)
            let query=args.GetProperty("query")
            Assert.Equal("index_owner_items.vector_embedding",query.GetProperty("indexName").GetString())
            let alternatives=query.GetProperty("expressions").GetProperty("$or")
            let expression=alternatives[0].GetProperty("$eq")
            Assert.Equal("Category",expression[0].GetProperty("$field").GetString())
            Assert.Equal("allowed",expression[1].GetProperty("$literal").GetString())
            Task.FromResult(RuntimeJson.parse """{"results":[]}"""))
        let action=Convex.action(fun ctx () ->
            ctx.VectorSearch(vector,[|1.;2.|],filter=(fun f -> callbacks<-callbacks+1;f.Or [f.Eq("Category","allowed")]))) |> FunctionRegistration.erase
        NativeFixtures.invoke action host NativeFixtures.emptyArguments |> ignore
        Assert.Equal(1,calls)
        Assert.Equal(1,callbacks)
        let rejected filter values =
            let mutable calls=0
            let host=NativeFixtures.host(fun _ _ _ -> calls<-calls+1;Task.FromResult(RuntimeJson.nullValue))
            let action=Convex.action(fun ctx () -> ctx.VectorSearch(vector,values,filter=filter)) |> FunctionRegistration.erase
            Assert.ThrowsAny<Exception>(fun () -> NativeFixtures.invoke action host NativeFixtures.emptyArguments |> ignore) |> ignore
            Assert.Equal(0,calls)
        rejected (fun f -> f.Eq("Name","outside")) [|1.;2.|]
        rejected (fun f -> f.Eq("Category","allowed")) [|1.;2.;3.|]
        rejected (fun _ -> NativeExpressions.binary "$gt" (NativeExpressions.field "Category") (box "x")) [|1.;2.|]
