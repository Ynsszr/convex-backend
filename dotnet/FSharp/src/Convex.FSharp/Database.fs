namespace Convex.FSharp

open System

[<RequireQualifiedAccess>]
type QueryOrder =
    | Asc
    | Desc

type PaginationOptions =
    {
        NumItems: int
        Cursor: string option
    }

type PaginationResult<'Doc> =
    {
        Page: 'Doc list
        IsDone: bool
        ContinueCursor: string
    }

[<RequireQualifiedAccess>]
type PaginationPageStatus =
    | SplitRecommended
    | SplitRequired

type AdvancedPaginationOptions =
    {
        NumItems: int
        Cursor: string option
        EndCursor: string option
        MaximumRowsRead: int option
        MaximumBytesRead: int option
        QueryId: float option
    }
    static member Create(numItems: int, ?cursor: string) =
        { NumItems = numItems; Cursor = cursor; EndCursor = None; MaximumRowsRead = None; MaximumBytesRead = None; QueryId = None }
    static member FromBasic(options: PaginationOptions) =
        { NumItems = options.NumItems; Cursor = options.Cursor; EndCursor = None; MaximumRowsRead = None; MaximumBytesRead = None; QueryId = None }

type AdvancedPaginationResult<'Doc> =
    {
        Page: 'Doc list
        IsDone: bool
        ContinueCursor: string
        SplitCursor: string option
        PageStatus: PaginationPageStatus option
    }
    member this.RequiresSplit = this.PageStatus = Some PaginationPageStatus.SplitRequired

module Pagination =
    let optionsValidator =
        V.objectWithOptional [
            "numItems", Validator.Number, false
            "cursor", V.nullable Validator.String, false
            "endCursor", V.nullable Validator.String, true
            "maximumRowsRead", Validator.Number, true
            "maximumBytesRead", Validator.Number, true
            "id", Validator.Number, true
        ]

    let resultValidator (document: Validator) =
        V.objectWithOptional [
            "page", Validator.Array document, false
            "isDone", Validator.Boolean, false
            "continueCursor", Validator.String, false
            "splitCursor", V.nullable Validator.String, true
            "pageStatus", V.nullable (V.union [Validator.LiteralString "SplitRecommended"; Validator.LiteralString "SplitRequired"]), true
        ]

    let validateOptions (options: AdvancedPaginationOptions) =
        if options.NumItems < 0 then invalidArg "options" "Pagination page size cannot be negative"
        for value in [options.MaximumRowsRead; options.MaximumBytesRead] do
            if value |> Option.exists (fun limit -> limit < 0) then
                invalidArg "options" "Pagination read budgets cannot be negative"

    let encodeOptions (options: AdvancedPaginationOptions) =
        validateOptions options
        let result = NativeValue.createSimpleObject ()
        NativeValue.setProperty result "numItems" (box options.NumItems)
        NativeValue.setProperty result "cursor" (options.Cursor |> Option.map box |> Option.defaultValue null)
        options.EndCursor |> Option.iter (fun cursor -> NativeValue.setProperty result "endCursor" (box cursor))
        options.MaximumRowsRead |> Option.iter (fun limit -> NativeValue.setProperty result "maximumRowsRead" (box limit))
        options.MaximumBytesRead |> Option.iter (fun limit -> NativeValue.setProperty result "maximumBytesRead" (box limit))
        options.QueryId |> Option.iter (fun identity -> NativeValue.setProperty result "id" (box identity))
        result

    let private optionalString raw name =
        let value = NativeValue.getProperty raw name
        if obj.ReferenceEquals(value, null) || NativeValue.isUndefined value then None
        elif NativeValue.jsTypeOf value = "string" then Some(unbox<string> value)
        else invalidOp ("Convex pagination " + name + " is malformed")

    let decodeOptions (raw: obj) : AdvancedPaginationOptions =
        if not (NativeValue.isPlainObject raw) then invalidArg "raw" "Pagination options must be an object"
        for name in NativeValue.objectKeys raw do
            if not (List.contains name ["numItems"; "cursor"; "endCursor"; "maximumRowsRead"; "maximumBytesRead"; "id"]) then
                invalidArg "raw" ("Unknown pagination option: " + name)
        if not (NativeValue.hasProperty raw "numItems") || not (NativeValue.hasProperty raw "cursor") then
            invalidArg "raw" "Pagination requires numItems and cursor"
        let optionalInt name =
            let value = NativeValue.getProperty raw name
            if NativeValue.isUndefined value then None else Some(Codec.decode<int> value)
        let options =
            { NumItems = Codec.decode<int> (NativeValue.getProperty raw "numItems")
              Cursor = optionalString raw "cursor"; EndCursor = optionalString raw "endCursor"
              MaximumRowsRead = optionalInt "maximumRowsRead"; MaximumBytesRead = optionalInt "maximumBytesRead"
              QueryId = let value=NativeValue.getProperty raw "id" in if NativeValue.isUndefined value then None else Some(Codec.decode<float> value) }
        validateOptions options
        options

    let decodeWith (decoder: obj -> 'Doc) (raw: obj) : AdvancedPaginationResult<'Doc> =
        if not (NativeValue.isPlainObject raw) then invalidOp "Convex pagination did not return an object"
        let page = NativeValue.getProperty raw "page"
        let doneValue = NativeValue.getProperty raw "isDone"
        let cursor = NativeValue.getProperty raw "continueCursor"
        if not (NativeValue.isArray page) then invalidOp "Convex pagination did not return a page array"
        if NativeValue.jsTypeOf doneValue <> "boolean" || NativeValue.jsTypeOf cursor <> "string" then
            invalidOp "Convex pagination metadata is malformed"
        let status =
            match optionalString raw "pageStatus" with
            | None -> None
            | Some "SplitRecommended" -> Some PaginationPageStatus.SplitRecommended
            | Some "SplitRequired" -> Some PaginationPageStatus.SplitRequired
            | Some _ -> invalidOp "Convex pagination pageStatus is unknown"
        let splitCursor = optionalString raw "splitCursor"
        { Page = [for item in unbox<obj[]> page -> decoder item]
          IsDone = unbox<bool> doneValue; ContinueCursor = unbox<string> cursor
          SplitCursor = splitCursor; PageStatus = status }

    let encodeWith (encoder: 'Doc -> obj) (page: AdvancedPaginationResult<'Doc>) =
        let result = NativeValue.createSimpleObject ()
        NativeValue.setProperty result "page" (box (page.Page |> List.map encoder |> List.toArray))
        NativeValue.setProperty result "isDone" (box page.IsDone)
        NativeValue.setProperty result "continueCursor" (box page.ContinueCursor)
        page.SplitCursor |> Option.iter (fun cursor -> NativeValue.setProperty result "splitCursor" (box cursor))
        page.PageStatus |> Option.iter (fun status ->
            let value = match status with PaginationPageStatus.SplitRecommended -> "SplitRecommended" | PaginationPageStatus.SplitRequired -> "SplitRequired"
            NativeValue.setProperty result "pageStatus" (box value))
        result

    let inline encode<'Doc> (page: AdvancedPaginationResult<'Doc>) = encodeWith Codec.encode<'Doc> page

    let map (mapping: 'Doc -> 'Result) (page: AdvancedPaginationResult<'Doc>) : AdvancedPaginationResult<'Result> =
        { Page = List.map mapping page.Page; IsDone = page.IsDone; ContinueCursor = page.ContinueCursor
          SplitCursor = page.SplitCursor; PageStatus = page.PageStatus }

    let splitRanges (options: AdvancedPaginationOptions) (page: AdvancedPaginationResult<'Doc>) =
        validateOptions options
        match page.SplitCursor with
        | None when page.PageStatus.IsSome -> invalidArg "page" "A split status requires a split cursor"
        | None -> None
        | Some cursor ->
            if Some cursor = options.Cursor || cursor = page.ContinueCursor then
                invalidArg "page" "A split cursor must make progress inside the page range"
            Some ({ options with EndCursor = Some cursor },
                  { options with Cursor = Some cursor; EndCursor = Some page.ContinueCursor })

[<RequireQualifiedAccess>]
type IndexRangeOp =
    | Eq of field: string * value: obj
    | Gt of field: string * value: obj
    | Gte of field: string * value: obj
    | Lt of field: string * value: obj
    | Lte of field: string * value: obj

type IndexRangeBuilder =
    {
        Ops: IndexRangeOp list
    }
    static member Empty = { Ops = [] }
    member this.EqMissing(field:string) = { Ops = this.Ops @ [ IndexRangeOp.Eq(field, NativeValue.undefinedValue()) ] }
    member inline this.Eq(field: string, value: 'T) = { Ops = this.Ops @ [ IndexRangeOp.Eq(field, Codec.encode<'T> value) ] }
    member inline this.Gt(field: string, value: 'T) = { Ops = this.Ops @ [ IndexRangeOp.Gt(field, Codec.encode<'T> value) ] }
    member inline this.Gte(field: string, value: 'T) = { Ops = this.Ops @ [ IndexRangeOp.Gte(field, Codec.encode<'T> value) ] }
    member inline this.Lt(field: string, value: 'T) = { Ops = this.Ops @ [ IndexRangeOp.Lt(field, Codec.encode<'T> value) ] }
    member inline this.Lte(field: string, value: 'T) = { Ops = this.Ops @ [ IndexRangeOp.Lte(field, Codec.encode<'T> value) ] }

/// A typed wrapper distinguishes expressions from literal object values. Its
/// serialized shape is the official SDK expression AST owned by the backend.
type NativeFilterExpression internal (value: obj) =
    member _.Value = value

module NativeExpressions =
    let private objectValue name value =
        let result=NativeValue.createSimpleObject ()
        NativeValue.setProperty result name value
        result
    let serialize (value: obj) =
        match value with
        | :? NativeFilterExpression as expression -> expression.Value
        | _ -> objectValue "$literal" value
    let expression name value : obj = NativeFilterExpression(objectValue name value) |> box
    let field (name: string) =
        if String.IsNullOrWhiteSpace name then invalidArg "name" "A field path is required"
        expression "$field" (box name)
    let binary name left right = expression name (box [|serialize left;serialize right|])
    let unary name value = expression name (serialize value)
    let logic name (values: obj seq) = expression name (values |> Seq.map serialize |> Seq.toArray |> box)

type FilterBuilder(raw: obj) =
    member this.Raw = raw
    member inline this.Value(value: 'T) : obj = Codec.encode<'T> value
    member _.Field(name: string) : obj = NativeExpressions.field name
    member _.Eq(left: obj, right: obj) : obj = NativeExpressions.binary "$eq" left right
    member _.Neq(left: obj, right: obj) : obj = NativeExpressions.binary "$neq" left right
    member _.Lt(left: obj, right: obj) : obj = NativeExpressions.binary "$lt" left right
    member _.Lte(left: obj, right: obj) : obj = NativeExpressions.binary "$lte" left right
    member _.Gt(left: obj, right: obj) : obj = NativeExpressions.binary "$gt" left right
    member _.Gte(left: obj, right: obj) : obj = NativeExpressions.binary "$gte" left right
    member _.And(expressions: obj seq) : obj = NativeExpressions.logic "$and" expressions
    member _.Or(expressions: obj seq) : obj = NativeExpressions.logic "$or" expressions
    member _.Not(expression: obj) : obj = NativeExpressions.unary "$not" expression
    member _.Add(left: obj, right: obj) : obj = NativeExpressions.binary "$add" left right
    member _.Sub(left: obj, right: obj) : obj = NativeExpressions.binary "$sub" left right
    member _.Mul(left: obj, right: obj) : obj = NativeExpressions.binary "$mul" left right
    member _.Div(left: obj, right: obj) : obj = NativeExpressions.binary "$div" left right
    member _.Mod(left: obj, right: obj) : obj = NativeExpressions.binary "$mod" left right
    member _.Neg(expression: obj) : obj = NativeExpressions.unary "$neg" expression

type SearchFilterBuilder =
    {
        SearchField: string
        QueryText: string
        Filters: (string * obj) list
    }
    static member Create(searchField: string, queryText: string) =
        { SearchField = searchField; QueryText = queryText; Filters = [] }
    member inline this.Eq(filterField: string, value: 'T) =
        { this with Filters = this.Filters @ [ (filterField, Codec.encode<'T> value) ] }

type DatabaseVars =
    {
        CommitTs: CommitTimestamp
    }
    member inline this.CommitTsInt64 : int64 =
        invalidOp "A commit timestamp placeholder is not an observed .NET timestamp"

/// One owning async query iterator. Close it after early termination or errors.
type IQueryIterator<'Value> =
    abstract Next: unit -> Async<'Value option>
    abstract Close: unit -> Async<unit>

module QueryIterator =
    let map (decode:'Raw -> 'Value) (raw:IQueryIterator<'Raw>) : IQueryIterator<'Value> =
        { new IQueryIterator<'Value> with
            member _.Next() = async { let! value=raw.Next() in return Option.map decode value }
            member _.Close() = raw.Close() }

    /// The predicate returns false to stop. Cleanup is awaited even on cancellation.
    let iterateWhile (acquire:unit -> IQueryIterator<'Value>) (predicate:'Value -> Async<bool>) =
        AsyncResource.usingAsync acquire (fun iterator -> iterator.Close()) (fun iterator -> async {
            let mutable running=true
            let mutable visited=0
            while running do
                let! item=iterator.Next()
                match item with
                | None -> running<-false
                | Some value ->
                    let! keepGoing=predicate value
                    visited<-visited+1
                    running<-keepGoing
            return visited })

type ITableQueryRaw =
    abstract FullTableScanRaw: unit -> ITableQueryRaw
    abstract CreateIteratorRaw: unit -> IQueryIterator<obj>
    abstract WithIndex: indexName: string * ?rangeOps: IndexRangeOp list -> ITableQueryRaw
    abstract WithSearchIndexRaw: indexName: string * searchField: string * queryText: string * filters: (string * obj) list -> ITableQueryRaw
    abstract Order: order: string -> ITableQueryRaw
    abstract FilterRaw: predicate: (obj -> obj) -> ITableQueryRaw
    abstract CollectRaw: unit -> Async<obj[]>
    abstract TakeRaw: count: int -> Async<obj[]>
    abstract FirstRaw: unit -> Async<obj>
    abstract UniqueRaw: unit -> Async<obj>
    abstract PaginateRaw: numItems: int * cursor: string option -> Async<obj>
    abstract PaginateAdvancedRaw: options: AdvancedPaginationOptions -> Async<obj>

type Doc<'Table, 'Data> =
    { _id: Id<'Table>; _creationTime: float; Data: 'Data }
    member this.Id = this._id
    member this.CreationTime = this._creationTime

/// Actual table identity supplied by the owning query adapter.
type ITableQueryOwner =
    abstract TableName: string option

type ITableQuery<'Doc, 'Table> =
    abstract FullTableScan: unit -> ITableQuery<'Doc,'Table>
    abstract GetIterator: unit -> IQueryIterator<'Doc>
    abstract GetDocumentIterator: unit -> IQueryIterator<Doc<'Table,'Doc>>
    abstract TakeDocs: count:int -> Async<Doc<'Table,'Doc> list>
    abstract PaginateDocs: options:PaginationOptions -> Async<PaginationResult<Doc<'Table,'Doc>>>
    abstract PaginateDocsAdvanced: options:AdvancedPaginationOptions -> Async<AdvancedPaginationResult<Doc<'Table,'Doc>>>
    abstract WithIndex: indexName: string * ?range: (IndexRangeBuilder -> IndexRangeBuilder) -> ITableQuery<'Doc, 'Table>
    abstract WithSearchIndex: indexName: string * searchField: string * queryText: string * ?filter: (SearchFilterBuilder -> SearchFilterBuilder) -> ITableQuery<'Doc, 'Table>
    abstract Order: order: QueryOrder -> ITableQuery<'Doc, 'Table>
    abstract Filter: predicate: (FilterBuilder -> obj) -> ITableQuery<'Doc, 'Table>
    abstract Collect: unit -> Async<'Doc list>
    abstract Take: count: int -> Async<'Doc list>
    abstract First: unit -> Async<'Doc option>
    abstract Unique: unit -> Async<'Doc option>
    abstract Paginate: options: PaginationOptions -> Async<PaginationResult<'Doc>>
    abstract PaginateAdvanced: options: AdvancedPaginationOptions -> Async<AdvancedPaginationResult<'Doc>>
    abstract CollectDocs: unit -> Async<Doc<'Table, 'Doc> list>
    abstract FirstDoc: unit -> Async<Doc<'Table, 'Doc> option>
    abstract UniqueDoc: unit -> Async<Doc<'Table, 'Doc> option>

type ISystemDatabaseReader =
    abstract GetRaw: tableName: string * idStr: string -> Async<obj>
    abstract QueryRaw: tableName: string -> ITableQueryRaw
    abstract NormalizeIdRaw: tableName: string * idString: string -> string option

type IDatabaseReader =
    abstract GetInTableRaw: tableName:string * idStr:string -> Async<obj>
    abstract GetRaw: idStr: string -> Async<obj>
    abstract QueryRaw: tableName: string -> ITableQueryRaw
    abstract NormalizeIdRaw: tableName: string * idString: string -> string option
    abstract System: ISystemDatabaseReader

type IDatabaseWriter =
    inherit IDatabaseReader
    abstract PatchInTableRaw: tableName:string * idStr:string * patch:obj -> Async<unit>
    abstract ReplaceInTableRaw: tableName:string * idStr:string * document:obj -> Async<unit>
    abstract DeleteInTableRaw: tableName:string * idStr:string -> Async<unit>
    abstract InsertRaw: tableName: string * document: obj -> Async<string>
    abstract PatchRaw: idStr: string * patch: obj -> Async<unit>
    abstract ReplaceRaw: idStr: string * document: obj -> Async<unit>
    abstract DeleteRaw: idStr: string -> Async<unit>
    abstract Vars: DatabaseVars

module SystemTable =
    [<Literal>]
    let Storage = "_storage"

    [<Literal>]
    let ScheduledFunctions = "_scheduled_functions"

    type StorageMarker = class end
    type ScheduledFunctionsMarker = class end

type StorageDocument =
    {
        _id: Id<SystemTable.StorageMarker>
        _creationTime: float
        Size: int64
        Sha256: string
        ContentType: string option
    }

[<RequireQualifiedAccess>]
type ScheduledFunctionState =
    | Pending
    | InProgress
    | Success
    | Failed of error: string
    | Canceled

type ScheduledFunctionDocument =
    {
        _id: Id<SystemTable.ScheduledFunctionsMarker>
        _creationTime: float
        Name: string
        Args: obj list
        ScheduledTime: float
        CompletedTime: float option
        State: ScheduledFunctionState
    }

module SystemTables =
    let storage : Table<SystemTable.StorageMarker, StorageDocument> =
        Table.defineWithMarker<SystemTable.StorageMarker, StorageDocument> SystemTable.Storage

    let scheduledFunctions : Table<SystemTable.ScheduledFunctionsMarker, ScheduledFunctionDocument> =
        Table.defineWithMarker<SystemTable.ScheduledFunctionsMarker, ScheduledFunctionDocument> SystemTable.ScheduledFunctions

/// Convex-owned system records have a distinct transport contract from application records.
module SystemDocumentCodec =
    let inline decode<'Doc> (raw: obj) : 'Doc =
        if typeof<'Doc> <> typeof<ScheduledFunctionDocument> then Codec.decodeTransport<'Doc> raw
        else
            let copy = ObjectCodec.emptyPlainObject()
            for key in NativeValue.objectKeys raw do
                NativeValue.setProperty copy key (NativeValue.getProperty raw key)
            let state =
                if NativeValue.hasProperty raw "State" then NativeValue.getProperty raw "State"
                else NativeValue.getProperty raw "state"
            let tag = string (NativeValue.getProperty state "kind")
            let mapped =
                match tag with
                | "pending" -> "Pending"
                | "inProgress" -> "InProgress"
                | "success" -> "Success"
                | "failed" -> "Failed"
                | "canceled" -> "Canceled"
                | other -> other // Strict application decoding rejects unknown states.
            let stateCopy = ObjectCodec.emptyPlainObject()
            for key in NativeValue.objectKeys state do
                NativeValue.setProperty stateCopy key (NativeValue.getProperty state key)
            NativeValue.setProperty stateCopy "kind" (box mapped)
            NativeValue.setProperty copy "State" stateCopy
            Codec.decodeTransport<'Doc> copy

module Doc =
    let inline decodeWith<'Table, 'Data> (decoder: obj -> 'Data) (raw: obj) : Doc<'Table, 'Data> =
        if not (NativeValue.isPlainObject raw) then invalidOp "Expected a Convex document object."
        let identifier = NativeValue.getProperty raw "_id"
        let timestamp = NativeValue.getProperty raw "_creationTime"
        if NativeValue.jsTypeOf identifier <> "string" || String.IsNullOrEmpty(unbox<string> identifier) then
            invalidOp "Convex document has no valid _id."
        if NativeValue.jsTypeOf timestamp <> "number" then invalidOp "Convex document has no numeric _creationTime."
        let created = Convert.ToDouble timestamp
        if Double.IsNaN created || Double.IsInfinity created then invalidOp "Convex document has a nonfinite _creationTime."
        { _id = Id(unbox<string> identifier); _creationTime = created; Data = decoder raw }

    let inline decode<'Table, 'Data> raw = decodeWith<'Table, 'Data> Codec.decodeDocument<'Data> raw

module CheckedIndexQuery =
    let owner expected (query:obj) =
        match query with
        | :? ITableQueryOwner as owner when owner.TableName=Some expected -> ()
        | _ -> invalidArg "index" "A checked index requires the actual matching query table owner"

    /// Convex accepts equalities in any order, then at most one inequality field.
    /// The sorted equality fields plus inequality must form an index prefix.
    let range fields operations =
        // The selected native schema JSON preserves its declared fields exactly.
        // Convex IndexedFields.iter_with_id appends only _id; _creationTime
        // is available here only when the canonical declaration contains it.
        let fields=fields @ ["_id"]
        let mutable equality=Set.empty
        let mutable inequality=None
        let mutable lower=false
        let mutable upper=false
        for operation in operations do
            let field,isEquality,isLower =
                match operation with
                | IndexRangeOp.Eq(field,_) -> field,true,false
                | IndexRangeOp.Gt(field,_) | IndexRangeOp.Gte(field,_) -> field,false,true
                | IndexRangeOp.Lt(field,_) | IndexRangeOp.Lte(field,_) -> field,false,false
            if not(List.contains field fields) then invalidArg "range" "A range field is not declared in the index"
            if isEquality then
                if equality.Contains field then invalidArg "range" "An equality field is assigned more than once"
                equality<-equality.Add field
            else
                if inequality |> Option.exists ((<>) field) then invalidArg "range" "Bounds must use one inequality field"
                if (isLower && lower) || (not isLower && upper) then invalidArg "range" "A range bound is assigned more than once"
                inequality<-Some field
                if isLower then lower<-true else upper<-true
        if inequality |> Option.exists equality.Contains then invalidArg "range" "A field cannot have both equality and inequality bounds"
        let equalities=fields |> List.filter equality.Contains
        let used=equalities @ (inequality |> Option.toList)
        if used<>(fields |> List.truncate used.Length) then invalidArg "range" "Range fields must form an index prefix"

[<AutoOpen>]
module DatabaseExtensions =
    type TableQuery<'Doc, 'Table>(raw: ITableQueryRaw, decoder: obj -> 'Doc, ?tableName:string) =
        interface ITableQueryOwner with member _.TableName=tableName
        interface ITableQuery<'Doc, 'Table> with
            member _.FullTableScan() = TableQuery<'Doc,'Table>(raw.FullTableScanRaw(),decoder,?tableName=tableName) :> ITableQuery<'Doc,'Table>
            member _.GetIterator() = QueryIterator.map decoder (raw.CreateIteratorRaw())
            member _.GetDocumentIterator() = QueryIterator.map (Doc.decodeWith<'Table,'Doc> decoder) (raw.CreateIteratorRaw())
            member _.TakeDocs(count) = async {
                if count<0 then invalidArg "count" "A query take count cannot be negative"
                let! rows=raw.TakeRaw count
                return [for row in rows -> Doc.decodeWith<'Table,'Doc> decoder row] }
            member _.PaginateDocsAdvanced(options) = async {
                Pagination.validateOptions options
                let! result=raw.PaginateAdvancedRaw options
                return Pagination.decodeWith (Doc.decodeWith<'Table,'Doc> decoder) result }
            member _.PaginateDocs(options) = async {
                Pagination.validateOptions (AdvancedPaginationOptions.FromBasic options)
                let! result=raw.PaginateRaw(options.NumItems,options.Cursor)
                let page=Pagination.decodeWith (Doc.decodeWith<'Table,'Doc> decoder) result
                if page.RequiresSplit then invalidOp "Convex requires this page to split; use PaginateDocsAdvanced"
                return ({Page=page.Page;IsDone=page.IsDone;ContinueCursor=page.ContinueCursor}:PaginationResult<Doc<'Table,'Doc>>) }
            member this.WithIndex(indexName: string, ?range) =
                let ops =
                    match range with
                    | Some rangeFn ->
                        let built = rangeFn IndexRangeBuilder.Empty
                        Some built.Ops
                    | None -> None
                let nextRaw = raw.WithIndex(indexName, ?rangeOps = ops)
                TableQuery<'Doc, 'Table>(nextRaw, decoder,?tableName=tableName) :> ITableQuery<'Doc, 'Table>

            member this.WithSearchIndex(indexName: string, searchField: string, queryText: string, ?filter) =
                let initial = SearchFilterBuilder.Create(searchField, queryText)
                let built = match filter with Some f -> f initial | None -> initial
                let nextRaw = raw.WithSearchIndexRaw(indexName, built.SearchField, built.QueryText, built.Filters)
                TableQuery<'Doc, 'Table>(nextRaw, decoder,?tableName=tableName) :> ITableQuery<'Doc, 'Table>

            member this.Order(order) =
                let orderStr = match order with QueryOrder.Asc -> "asc" | QueryOrder.Desc -> "desc"
                let nextRaw = raw.Order(orderStr)
                TableQuery<'Doc, 'Table>(nextRaw, decoder,?tableName=tableName) :> ITableQuery<'Doc, 'Table>

            member this.Filter(predicate) =
                let rawPred (rawQ: obj) = predicate (FilterBuilder rawQ)
                let nextRaw = raw.FilterRaw(rawPred)
                TableQuery<'Doc, 'Table>(nextRaw, decoder,?tableName=tableName) :> ITableQuery<'Doc, 'Table>

            member this.Collect() =
                async {
                    let! rawList = raw.CollectRaw()
                    return [ for item in rawList -> decoder item ]
                }

            member this.Take(count) =
                async {
                    if count<0 then invalidArg "count" "A query take count cannot be negative"
                    let! rawList = raw.TakeRaw(count)
                    return [ for item in rawList -> decoder item ]
                }

            member this.First() =
                async {
                    let! item = raw.FirstRaw()
                    if NativeValue.isNull item || NativeValue.isUndefined item then return None
                    else return Some (decoder item)
                }

            member this.Unique() =
                async {
                    let! item = raw.UniqueRaw()
                    if NativeValue.isNull item || NativeValue.isUndefined item then return None
                    else return Some (decoder item)
                }

            member this.Paginate(options) =
                async {
                    Pagination.validateOptions (AdvancedPaginationOptions.FromBasic options)
                    let! res = raw.PaginateRaw(options.NumItems, options.Cursor)
                    let page = Pagination.decodeWith decoder res
                    if page.RequiresSplit then invalidOp "Convex requires this page to split; use PaginateAdvanced to retain its range and split metadata"
                    return ({ Page = page.Page; IsDone = page.IsDone; ContinueCursor = page.ContinueCursor }: PaginationResult<'Doc>)
                }

            member this.CollectDocs() = async {
                let! rows = raw.CollectRaw()
                return [for row in rows -> Doc.decodeWith<'Table, 'Doc> decoder row]
            }
            member this.FirstDoc() = async {
                let! row = raw.FirstRaw()
                if NativeValue.isNull row || NativeValue.isUndefined row then return None
                else return Some(Doc.decodeWith<'Table, 'Doc> decoder row)
            }

            member this.PaginateAdvanced(options) =
                async {
                    Pagination.validateOptions options
                    let! result = raw.PaginateAdvancedRaw options
                    return Pagination.decodeWith decoder result
                }
            member this.UniqueDoc() = async {
                let! row = raw.UniqueRaw()
                if NativeValue.isNull row || NativeValue.isUndefined row then return None
                else return Some(Doc.decodeWith<'Table, 'Doc> decoder row)
            }

    type ITableQuery<'Doc, 'Table> with
        member this.IterateWhile(predicate:'Doc -> Async<bool>) = QueryIterator.iterateWhile this.GetIterator predicate
        member this.Iterate(action:'Doc -> Async<unit>) =
            this.IterateWhile(fun value -> async {
                do! action value
                return true })
        member this.IterateDocumentsWhile(predicate:Doc<'Table,'Doc> -> Async<bool>) =
            QueryIterator.iterateWhile this.GetDocumentIterator predicate
        member this.TakeEach(count:int,action:'Doc -> Async<unit>) = async {
            if count<0 then invalidArg "count" "A query take count cannot be negative"
            if count=0 then return 0 else
            let mutable visited=0
            return! this.IterateWhile(fun value -> async {
                do! action value
                visited<-visited+1
                return visited<count }) }

        member this.WithIndex(index: DeclaredTableIndex<'Table, 'Doc>, ?range: IndexRangeBuilder -> IndexRangeBuilder) : ITableQuery<'Doc, 'Table> =
            CheckedIndexQuery.owner index.TableName this
            let built=range |> Option.map(fun build -> build IndexRangeBuilder.Empty)
            built |> Option.iter(fun value -> CheckedIndexQuery.range index.Fields value.Ops)
            this.WithIndex(index.IndexName, ?range = (built |> Option.map(fun value -> fun _ -> value)))

        member this.WithSearchIndex(index: DeclaredTableSearchIndex<'Table, 'Doc>, queryText: string, ?filter: SearchFilterBuilder -> SearchFilterBuilder) : ITableQuery<'Doc, 'Table> =
            CheckedIndexQuery.owner index.TableName this
            let initial=SearchFilterBuilder.Create(index.SearchField,queryText)
            let built=filter |> Option.map(fun build -> build initial) |> Option.defaultValue initial
            if built.SearchField<>index.SearchField || built.QueryText<>queryText then
                invalidArg "filter" "A search filter cannot replace the canonical search field or query"
            for field,_ in built.Filters do
                if not(List.contains field index.FilterFields) then invalidArg "filter" "A search filter field is not declared in this index"
            this.WithSearchIndex(index.IndexName,index.SearchField,queryText,filter=(fun _ -> built))

    type ISystemDatabaseReader with
        member inline this.GetDoc<'Doc, 'Table>(table: Table<'Table, 'Doc>, Id idStr: Id<'Table>) : Async<Doc<'Table, 'Doc> option> = async {
            let! raw = this.GetRaw(table.Name, idStr)
            if NativeValue.isNull raw || NativeValue.isUndefined raw then return None
            else return Some(Doc.decodeWith<'Table, 'Doc> SystemDocumentCodec.decode<'Doc> raw)
        }

        member inline this.Get<'Doc, 'Table>(table: Table<'Table, 'Doc>, Id idStr: Id<'Table>) : Async<'Doc option> =
            async {
                let! raw = this.GetRaw(table.Name, idStr)
                if NativeValue.isNull raw || NativeValue.isUndefined raw then return None
                else return Some (SystemDocumentCodec.decode<'Doc> raw)
            }

        member inline this.Get(Id idStr: Id<SystemTable.StorageMarker>) : Async<StorageDocument option> =
            this.Get(SystemTables.storage, Id idStr)

        member inline this.Get(Id idStr: Id<SystemTable.ScheduledFunctionsMarker>) : Async<ScheduledFunctionDocument option> =
            this.Get(SystemTables.scheduledFunctions, Id idStr)

        member inline this.Query<'Doc, 'Table>(table: Table<'Table, 'Doc>) : ITableQuery<'Doc, 'Table> =
            let rawQ = this.QueryRaw(table.Name)
            TableQuery<'Doc, 'Table>(rawQ, SystemDocumentCodec.decode<'Doc>,tableName=table.Name) :> ITableQuery<'Doc, 'Table>

        member inline this.Query<'Doc, 'Table>(tableName: string) : ITableQuery<'Doc, 'Table> =
            let rawQ = this.QueryRaw(tableName)
            TableQuery<'Doc, 'Table>(rawQ, SystemDocumentCodec.decode<'Doc>,tableName=tableName) :> ITableQuery<'Doc, 'Table>

        member inline this.Storage : ITableQuery<StorageDocument, SystemTable.StorageMarker> =
            this.Query(SystemTables.storage)

        member inline this.ScheduledFunctions : ITableQuery<ScheduledFunctionDocument, SystemTable.ScheduledFunctionsMarker> =
            this.Query(SystemTables.scheduledFunctions)

        member inline this.NormalizeId<'Table>(tableName: string, idString: string) : Id<'Table> option =
            match this.NormalizeIdRaw(tableName, idString) with
            | Some id -> Some (Id id)
            | None -> None

        member inline this.NormalizeId<'Table, 'Doc>(table: Table<'Table, 'Doc>, idString: string) : Id<'Table> option =
            this.NormalizeId<'Table>(table.Name, idString)

    type IDatabaseReader with
        member inline this.GetDoc<'Doc, 'Table>(table: Table<'Table, 'Doc>, Id idStr: Id<'Table>) : Async<Doc<'Table, 'Doc> option> = async {
            let! raw = this.GetInTableRaw(table.Name,idStr)
            if NativeValue.isNull raw || NativeValue.isUndefined raw then return None
            else return Some(Doc.decode<'Table, 'Doc> raw)
        }

        member inline this.Get<'Doc>(Id idStr: Id<'Doc>) : Async<'Doc option> =
            async {
                let! raw = this.GetRaw(idStr)
                if NativeValue.isNull raw || NativeValue.isUndefined raw then return None
                else return Some (Codec.decodeDocument<'Doc> raw)
            }

        member inline this.Get<'Doc, 'Table>(table: Table<'Table, 'Doc>, Id idStr: Id<'Table>) : Async<'Doc option> =
            async {
                let! raw = this.GetInTableRaw(table.Name,idStr)
                if NativeValue.isNull raw || NativeValue.isUndefined raw then return None
                else return Some (Codec.decodeDocument<'Doc> raw)
            }

        member inline this.Query<'Doc, 'Table>(tableName: string) : ITableQuery<'Doc, 'Table> =
            let rawQ = this.QueryRaw(tableName)
            TableQuery<'Doc, 'Table>(rawQ, Codec.decodeDocument<'Doc>,tableName=tableName) :> ITableQuery<'Doc, 'Table>

        member inline this.Query<'Doc, 'Table>(table: Table<'Table, 'Doc>) : ITableQuery<'Doc, 'Table> =
            this.Query<'Doc, 'Table>(table.Name)

        member inline this.NormalizeId<'Table>(tableName: string, idString: string) : Id<'Table> option =
            match this.NormalizeIdRaw(tableName, idString) with
            | Some id -> Some (Id id)
            | None -> None

        member inline this.NormalizeId<'Table, 'Doc>(table: Table<'Table, 'Doc>, idString: string) : Id<'Table> option =
            this.NormalizeId<'Table>(table.Name, idString)

    type IDatabaseWriter with
        member inline this.Insert<'Doc, 'Table>(table: Table<'Table, 'Doc>, document: 'Doc) : Async<Id<'Table>> =
            async {
                let encoded = Codec.encode<'Doc> document
                let! idStr = this.InsertRaw(table.Name, encoded)
                return Id idStr
            }

        member inline this.Insert<'Doc, 'Table>(tableName: string, document: 'Doc) : Async<Id<'Table>> =
            async {
                let encoded = Codec.encode<'Doc> document
                let! idStr = this.InsertRaw(tableName, encoded)
                return Id idStr
            }

        member inline this.Patch<'Patch, 'Table>(Id idStr: Id<'Table>, patch: 'Patch) : Async<unit> =
            let encoded = Codec.encode<'Patch> patch
            this.PatchRaw(idStr, encoded)

        member inline this.Unset<'Table>(Id idStr: Id<'Table>, fields: string list) : Async<unit> =
            let patch = ObjectCodec.emptyPlainObject()
            for field in fields do
                match FieldValidation.validateFieldName field with
                | Error error -> invalidArg "fields" error
                | Ok () when field = "_id" || field = "_creationTime" -> invalidArg "fields" "System document fields cannot be removed."
                | Ok () -> NativeValue.setProperty patch field (NativeValue.undefinedValue())
            this.PatchRaw(idStr, patch)

        member inline this.Replace<'Doc, 'Table>(Id idStr: Id<'Table>, document: 'Doc) : Async<unit> =
            let encoded = Codec.encode<'Doc> document
            this.ReplaceRaw(idStr, encoded)

        member inline this.Delete<'Table>(Id idStr: Id<'Table>) : Async<unit> =
            this.DeleteRaw(idStr)

        member inline this.Delete<'Doc, 'Table>(table: Table<'Table, 'Doc>, Id idStr: Id<'Table>) : Async<unit> =
            this.DeleteInTableRaw(table.Name,idStr)
        member inline this.Patch<'Doc,'Table,'Patch>(table:Table<'Table,'Doc>, Id idStr:Id<'Table>, patch:'Patch) : Async<unit> =
            this.PatchInTableRaw(table.Name,idStr,Codec.encode<'Patch> patch)
        member inline this.Replace<'Doc,'Table>(table:Table<'Table,'Doc>, Id idStr:Id<'Table>, document:'Doc) : Async<unit> =
            this.ReplaceInTableRaw(table.Name,idStr,Codec.encode<'Doc> document)
        member inline this.Unset<'Doc,'Table>(table:Table<'Table,'Doc>, Id idStr:Id<'Table>, fields:string list) : Async<unit> =
            let patch=ObjectCodec.emptyPlainObject()
            for field in fields do
                match FieldValidation.validateFieldName field with
                | Ok () when field<>"_id" && field<>"_creationTime" -> NativeValue.setProperty patch field (NativeValue.undefinedValue())
                | _ -> invalidArg "fields" "Only ordinary document fields may be removed"
            this.PatchInTableRaw(table.Name,idStr,patch)



type StorageId = Id<SystemTable.StorageMarker>

[<AutoOpen>]
module TypedStorageExtensions =
    type IStorageReader with
        member this.GetUrl(storageId:StorageId) = this.GetUrl(Id.value storageId)
        member this.GetMetadata(storageId:StorageId) = this.GetMetadata(Id.value storageId)
    type IStorageWriter with
        member this.Delete(storageId:StorageId) = this.Delete(Id.value storageId)
    type IStorageActionWriter with
        member this.Get(storageId:StorageId) = this.Get(Id.value storageId)
        member this.StoreFile(bytes:byte[],?contentType:string,?sha256:string):Async<StorageId> = async {
            let! identifier=this.Store(bytes,?contentType=contentType,?sha256=sha256)
            return Id.ofString<SystemTable.StorageMarker> identifier
        }
    type ISystemDatabaseReader with
        member inline this.GetFileMetadata(storageId:StorageId):Async<StorageDocument option> = this.Get(SystemTables.storage,storageId)


/// A shallow patch can set values and remove fields in one database operation.
/// It is deliberately not a general application-document codec.
type DocumentPatch = private DocumentPatch of (string * obj) list

module DocumentPatch =
    let empty=DocumentPatch []
    let private field name =
        match FieldValidation.validateFieldName name with
        | Ok () when name<>"_id" && name<>"_creationTime" -> ()
        | _ -> invalidArg "name" "Only ordinary document fields may be patched"
    let addRaw name value (DocumentPatch fields) =
        field name
        if fields |> List.exists (fun (existing,_) -> existing=name) then invalidArg "name" "A patch field is assigned more than once"
        if fields.Length>=1024 then invalidArg "patch" "A shallow patch cannot exceed 1024 fields"
        let snapshot=if NativeValue.isUndefined value then value else RawValueCodec.normalize value
        DocumentPatch (fields @ [name,snapshot])
    let inline set name value patch = addRaw name (Codec.encode value) patch
    let remove name patch = addRaw name (NativeValue.undefinedValue()) patch
    let inline ofRecord value =
        let encoded=Codec.encodeArguments value
        NativeValue.objectKeys encoded |> Array.fold (fun patch name -> addRaw name (NativeValue.getProperty encoded name) patch) empty
    let toRaw (DocumentPatch fields) =
        let value=ObjectCodec.emptyPlainObject()
        for name,item in fields do NativeValue.setProperty value name item
        value

/// A table-qualified reader, usable for either ordinary or system tables.
type TableReader<'Table,'Data>(table:Table<'Table,'Data>,getRaw:string -> Async<obj>,queryRaw:unit -> ITableQueryRaw,normalize:string -> string option,decode:obj -> 'Data) =
    member _.Definition=table
    member _.Get(identifier:Id<'Table>) : Async<'Data option> = async {
        let! raw=getRaw (Id.value identifier)
        if NativeValue.isNull raw || NativeValue.isUndefined raw then return None
        else return Some(decode raw) }
    member _.GetDoc(identifier:Id<'Table>) : Async<Doc<'Table,'Data> option> = async {
        let! raw=getRaw (Id.value identifier)
        if NativeValue.isNull raw || NativeValue.isUndefined raw then return None
        else return Some(Doc.decodeWith<'Table,'Data> decode raw) }
    member _.Query() : ITableQuery<'Data,'Table> = TableQuery<'Data,'Table>(queryRaw(),decode,tableName=table.Name) :> ITableQuery<'Data,'Table>
    member _.NormalizeId(identifier:string) : Id<'Table> option = normalize identifier |> Option.map Id.ofString<'Table>

type TableWriter<'Table,'Data>(writer:IDatabaseWriter,table:Table<'Table,'Data>,encode:'Data -> obj,decode:obj -> 'Data) =
    inherit TableReader<'Table,'Data>(table,(fun id -> writer.GetInTableRaw(table.Name,id)),(fun () -> writer.QueryRaw table.Name),(fun id -> writer.NormalizeIdRaw(table.Name,id)),decode)
    member _.RawWriter=writer
    member _.Insert(document:'Data) : Async<Id<'Table>> = async {
        let! identifier=writer.InsertRaw(table.Name,encode document)
        return Id.ofString<'Table> identifier }
    member _.Replace(identifier:Id<'Table>,document:'Data) = writer.ReplaceInTableRaw(table.Name,Id.value identifier,encode document)
    member _.Delete(identifier:Id<'Table>) = writer.DeleteInTableRaw(table.Name,Id.value identifier)
    member inline this.Patch(identifier:Id<'Table>,patch:'Patch) = this.RawWriter.PatchInTableRaw(this.Definition.Name,Id.value identifier,Codec.encode<'Patch> patch)
    member _.Patch(identifier:Id<'Table>,patch:DocumentPatch) = writer.PatchInTableRaw(table.Name,Id.value identifier,DocumentPatch.toRaw patch)
    member _.Unset(identifier:Id<'Table>,fields:string list) =
        let patch=List.fold (fun patch name -> DocumentPatch.remove name patch) DocumentPatch.empty fields
        writer.PatchInTableRaw(table.Name,Id.value identifier,DocumentPatch.toRaw patch)

[<AutoOpen>]
module ScopedDatabaseExtensions =
    type IDatabaseReader with
        member inline this.Table(table:Table<'Table,'Data>) =
            TableReader<'Table,'Data>(table,(fun id -> this.GetInTableRaw(table.Name,id)),(fun () -> this.QueryRaw table.Name),(fun id -> this.NormalizeIdRaw(table.Name,id)),Codec.decodeDocument<'Data>)
    type IDatabaseWriter with
        member inline this.Table(table:Table<'Table,'Data>) = TableWriter<'Table,'Data>(this,table,Codec.encode<'Data>,Codec.decodeDocument<'Data>)
        member this.Patch(identifier:Id<'Table>,patch:DocumentPatch) = this.PatchRaw(Id.value identifier,DocumentPatch.toRaw patch)
        member this.Patch(table:Table<'Table,'Data>,identifier:Id<'Table>,patch:DocumentPatch) = this.PatchInTableRaw(table.Name,Id.value identifier,DocumentPatch.toRaw patch)
    type ISystemDatabaseReader with
        member inline this.Table(table:Table<'Table,'Data>) =
            TableReader<'Table,'Data>(table,(fun id -> this.GetRaw(table.Name,id)),(fun () -> this.QueryRaw table.Name),(fun id -> this.NormalizeIdRaw(table.Name,id)),SystemDocumentCodec.decode<'Data>)
        member inline this.Get(identifier:ScheduledFunctionId) : Async<ScheduledFunctionDocument option> =
            this.Get(SystemTables.scheduledFunctions,Id.ofString<SystemTable.ScheduledFunctionsMarker>(Id.value identifier))
    type IScheduler with
        member this.Cancel(identifier:Id<SystemTable.ScheduledFunctionsMarker>) =
            this.Cancel(Id.ofString<ScheduledFunctionsTable>(Id.value identifier))
    type ITableQuery<'Data,'Table> with
        member this.Fold(initial:'State,folder:'State -> 'Data -> Async<'State>) : Async<'State> = async {
            let mutable state=initial
            let! _=this.Iterate(fun value -> async {
                let! next=folder state value
                state<-next })
            return state }
