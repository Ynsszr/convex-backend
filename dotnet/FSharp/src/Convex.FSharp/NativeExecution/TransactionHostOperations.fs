namespace Convex.FSharp.NativeExecution

open System
open Convex.FSharp

/// An invocation-scoped syscall capability. The backend owns the transaction,
/// identity, component namespace and limits; this interface carries no credential.
type internal ITransactionHostOperations =
    abstract Call: name: string * arguments: obj * asynchronous: bool -> Async<obj>
    abstract CallSync: name: string * arguments: obj -> obj

module internal HostArguments =
    let objectValue (fields: (string * obj) list) : obj =
        let result = Collections.Generic.Dictionary<string, obj>(StringComparer.Ordinal)
        for name, value in fields do result.Add(name, value)
        box result

    let field name value = NativeValue.getProperty value name
    let text name value = field name value |> Codec.decode<string>
    let optionalText name value =
        let item = field name value
        if NativeValue.isNull item || NativeValue.isUndefined item then None
        else Some (Codec.decode<string> item)

    let version = "1.46.0"
    let empty () = objectValue []

    let functionAddress (target:obj) =
        match target with
        | :? string as name when name.StartsWith("function://", StringComparison.Ordinal) -> ["functionHandle", box name]
        | :? string as name when name.StartsWith("_reference/", StringComparison.Ordinal) -> ["reference", box name]
        | :? string as name when not (String.IsNullOrWhiteSpace name) -> ["name", box name]
        | _ -> invalidArg "target" "A portable named, component or handle function reference is required"

type private TransactionQuery(host: ITransactionHostOperations, table: string, source: obj, operators: obj list) =
    let query () = HostArguments.objectValue ["source", source; "operators", box (List.toArray operators)]
    let mutable consumed = false
    let consume () =
        if consumed then invalidOp "A query can only be consumed or chained once"
        consumed <- true

    let iterate count =
        consume ()
        let streamQuery=
            match count with
            | None -> query ()
            | Some limit -> HostArguments.objectValue ["source",source;"operators",box (List.toArray (operators@[HostArguments.objectValue ["limit",box limit]]))]
        let started = host.CallSync("1.0/queryStream", HostArguments.objectValue ["query", streamQuery; "version", box HostArguments.version])
        let id = HostArguments.field "queryId" started
        let mutable closed = false
        { new IQueryIterator<obj> with
            member _.Next() = async {
                if closed then return None else
                let! next = host.Call("1.0/queryStreamNext", HostArguments.objectValue ["queryId", id], true)
                if HostArguments.field "done" next |> Codec.decode<bool> then
                    closed <- true
                    return None
                else return Some (HostArguments.field "value" next)
            }
            member _.Close() = async {
                if not closed then
                    closed <- true
                    host.CallSync("1.0/queryCleanup", HostArguments.objectValue ["queryId", id]) |> ignore
            } }

    let rows count = async {
        let values = ResizeArray<obj>()
        let iterator = iterate (if count=Int32.MaxValue then None else Some count)
        return! AsyncResource.usingAsync (fun () -> iterator) (fun value -> value.Close()) (fun value -> async {
            let mutable running = true
            while running && values.Count < count do
                let! next = value.Next()
                match next with
                | None -> running <- false
                | Some item -> values.Add item
            return values.ToArray()
        })
    }

    let page options = async {
        Pagination.validateOptions options
        consume ()
        let baseArguments = [
            "query", query (); "cursor", options.Cursor |> Option.map box |> Option.defaultValue null
            "endCursor", options.EndCursor |> Option.map box |> Option.defaultValue null
            "pageSize", box options.NumItems
            "maximumRowsRead", options.MaximumRowsRead |> Option.map box |> Option.defaultValue null
            "version", box HostArguments.version
        ]
        let arguments = baseArguments @ (options.MaximumBytesRead |> Option.map (fun value -> ["maximumBytesRead", box value]) |> Option.defaultValue [])
        return! host.Call("1.0/queryPage", HostArguments.objectValue arguments, true)
    }

    interface ITableQueryRaw with
        member _.FullTableScanRaw() =
            consume ()
            TransactionQuery(host, table, HostArguments.objectValue ["type", box "FullTableScan"; "tableName", box table; "order", null], []) :> ITableQueryRaw
        member _.CreateIteratorRaw() = iterate None
        member _.WithIndex(indexName, ?rangeOps) =
            consume ()
            let expressions = defaultArg rangeOps [] |> List.map (fun operation ->
                let kind, field, value =
                    match operation with
                    | IndexRangeOp.Eq(field,value) -> "Eq",field,value
                    | IndexRangeOp.Gt(field,value) -> "Gt",field,value
                    | IndexRangeOp.Gte(field,value) -> "Gte",field,value
                    | IndexRangeOp.Lt(field,value) -> "Lt",field,value
                    | IndexRangeOp.Lte(field,value) -> "Lte",field,value
                HostArguments.objectValue ["type",box kind;"fieldPath",box field;"value",value]) |> List.toArray
            TransactionQuery(host, table, HostArguments.objectValue ["type",box "IndexRange";"indexName",box (table+"."+indexName);"range",box expressions;"order",null], operators) :> ITableQueryRaw
        member _.WithSearchIndexRaw(indexName, searchField, queryText, filters) =
            consume ()
            let expressions = HostArguments.objectValue ["type",box "Search";"fieldPath",box searchField;"value",box queryText] ::
                              (filters |> List.map (fun (field,value) -> HostArguments.objectValue ["type",box "Eq";"fieldPath",box field;"value",value]))
            TransactionQuery(host, table, HostArguments.objectValue ["type",box "Search";"indexName",box (table+"."+indexName);"filters",box (List.toArray expressions)], operators) :> ITableQueryRaw
        member _.Order(order) =
            if order<>"asc" && order<>"desc" then invalidArg "order" "Invalid query order"
            consume ()
            let next = NativeValue.objectKeys source |> Array.map (fun name -> name, if name="order" then box order else HostArguments.field name source) |> Array.toList
            TransactionQuery(host,table,HostArguments.objectValue next,operators) :> ITableQueryRaw
        member _.FilterRaw predicate =
            consume ()
            let expression=predicate null |> NativeExpressions.serialize
            TransactionQuery(host,table,source,operators@[HostArguments.objectValue ["filter",expression]]) :> ITableQueryRaw
        member _.CollectRaw() = rows Int32.MaxValue
        member _.TakeRaw count =
            if count<0 then invalidArg "count" "A query take count cannot be negative"
            rows count
        member _.FirstRaw() = async { let! values=rows 1 in return if values.Length=0 then null else values[0] }
        member _.UniqueRaw() = async {
            let! values=rows 2
            if values.Length>1 then invalidOp "A unique query returned more than one document"
            return if values.Length=0 then null else values[0]
        }
        member _.PaginateRaw(count,cursor) = page (AdvancedPaginationOptions.Create(count,?cursor=cursor))
        member _.PaginateAdvancedRaw options = page options

module internal NativeContexts =
    open HostArguments

    let private query host system (table:string) =
        if table.StartsWith("_",StringComparison.Ordinal) <> system then invalidArg "table" "System tables require the system database capability"
        TransactionQuery(host,table,objectValue ["type",box "FullTableScan";"tableName",box table;"order",null],[]) :> ITableQueryRaw

    let private get (host:ITransactionHostOperations) system table id =
        host.Call("1.0/get",objectValue (["id",box id;"isSystem",box system;"version",box version] @ (table |> Option.map(fun value -> ["table",box value]) |> Option.defaultValue [])),true)

    let private normalize (host:ITransactionHostOperations) system (table:string) id =
        if table.StartsWith("_",StringComparison.Ordinal) <> system then invalidArg "table" "System tables require the system database capability"
        host.CallSync("1.0/db/normalizeId",objectValue ["table",box table;"idString",box id]) |> optionalText "id"

    let reader host : IDatabaseReader =
        let system = { new ISystemDatabaseReader with
            member _.GetRaw(table,id) = get host true (Some table) id
            member _.QueryRaw table = query host true table
            member _.NormalizeIdRaw(table,id) = normalize host true table id }
        { new IDatabaseReader with
            member _.GetInTableRaw(table,id) = get host false (Some table) id
            member _.GetRaw id = get host false None id
            member _.QueryRaw table = query host false table
            member _.NormalizeIdRaw(table,id) = normalize host false table id
            member _.System = system }

    let writer (host:ITransactionHostOperations) : IDatabaseWriter =
        let read = reader host
        let change name table id value = async {
            let fields=["id",box id] @ (table |> Option.map(fun value -> ["table",box value]) |> Option.defaultValue []) @
                       (value |> Option.map(fun value -> ["value",value]) |> Option.defaultValue [])
            let! _=host.Call(name,objectValue fields,true)
            return () }
        { new IDatabaseWriter with
            member _.GetInTableRaw(table,id) = read.GetInTableRaw(table,id)
            member _.GetRaw id = read.GetRaw id
            member _.QueryRaw table = read.QueryRaw table
            member _.NormalizeIdRaw(table,id) = read.NormalizeIdRaw(table,id)
            member _.System = read.System
            member _.InsertRaw(table,value) = async {
                if table.StartsWith("_",StringComparison.Ordinal) then invalidArg "table" "System tables are read only"
                let! result=host.Call("1.0/insert",objectValue ["table",box table;"value",value],true)
                return text "_id" result }
            member _.PatchInTableRaw(table,id,value) = change "1.0/shallowMerge" (Some table) id (Some value)
            member _.ReplaceInTableRaw(table,id,value) = change "1.0/replace" (Some table) id (Some value)
            member _.DeleteInTableRaw(table,id) = change "1.0/remove" (Some table) id None
            member _.PatchRaw(id,value) = change "1.0/shallowMerge" None id (Some value)
            member _.ReplaceRaw(id,value) = change "1.0/replace" None id (Some value)
            member _.DeleteRaw id = change "1.0/remove" None id None
            member _.Vars = {CommitTs=CommitTimestamp.Placeholder} }

    let auth (host:ITransactionHostOperations) =
        { new IAuth with
            member _.GetUserIdentity() = async {
                let! value=host.Call("1.0/getUserIdentity",empty (),true)
                return if isNull value then None else Some (UserIdentity.decode value)
            }
        }

    let storageReader (host:ITransactionHostOperations) requestId : IStorageReader =
        let args id=objectValue ["requestId",box requestId;"version",box version;"storageId",box id]
        { new IStorageReader with
            member _.GetUrl id = async {
                let! value=host.Call("1.0/storageGetUrl",args id,true)
                return if isNull value then None else Some (Codec.decode<string> value) }
            member _.GetMetadata id = async {
                let! value=host.Call("1.0/storageGetMetadata",args id,true)
                if isNull value then return None else
                return Some {
                    StorageId=text "storageId" value
                    Size=
                        let size=field "size" value |> Codec.decode<float>
                        if not(Double.IsFinite size) || size<0. || size>9007199254740991. || Math.Truncate(size)<>size then
                            invalidOp "Storage size metadata must be a nonnegative safe integer"
                        int64 size
                    ContentType=optionalText "contentType" value
                    Sha256=text "sha256" value
                }
            }
        }

    let storageWriter (host:ITransactionHostOperations) requestId : IStorageWriter =
        let read=storageReader host requestId
        { new IStorageWriter with
            member _.GetUrl id=read.GetUrl id
            member _.GetMetadata id=read.GetMetadata id
            member _.GenerateUploadUrl()=async {
                let! result=host.Call("1.0/storageGenerateUploadUrl",objectValue ["requestId",box requestId;"version",box version],true)
                return Codec.decode<string> result }
            member _.Delete id=async {
                let! _=host.Call("1.0/storageDelete",objectValue ["requestId",box requestId;"version",box version;"storageId",box id],true)
                return () } }

    let scheduler (host:ITransactionHostOperations) : IScheduler =
        let schedule at target args = async {
            let! result=host.Call("1.0/schedule",objectValue (functionAddress target @ ["ts",box (float at/1000.);"args",args;"version",box version]),true)
            return Codec.decode<string> result }
        { new IScheduler with
            member _.RunAfterRaw(delay,target,args)=
                ScheduleValidation.delay delay
                schedule (int64 (InvocationRuntime.nowMilliseconds())+delay) target args
            member _.RunAtRaw(at,target,args)=
                ScheduleValidation.timestamp at
                schedule at target args
            member _.Cancel(Id id)=async { let! _=host.Call("1.0/cancel_job",objectValue ["id",box id],true) in return () } }

    let internal metadata (host:ITransactionHostOperations) kind visibility : IMutationMeta =
        let decodeRequest raw = {
            Ip=optionalText "ip" raw; UserAgent=optionalText "userAgent" raw; RequestId=text "requestId" raw
            ScheduledFunctionId=optionalText "scheduledFunctionId" raw; AuthToken=optionalText "authToken" raw }
        { new IMutationMeta with
            member _.GetFunctionMetadata()=async {
                let! raw=host.Call("1.0/getFunctionMetadata",empty (),true)
                return {Name=text "name" raw;ComponentPath=text "componentPath" raw;Type=kind;Visibility=visibility} }
            member _.GetDeploymentMetadata()=async {
                let! raw=host.Call("1.0/getDeploymentMetadata",empty (),true)
                return {Name=text "name" raw;Region=optionalText "region" raw;Class=text "class" raw} }
            member _.GetSnapshotTs()=host.CallSync("1.0/getSnapshotTs",empty ()) |> Codec.decode<int64>
            member _.GetRequestMetadata()=async { let! raw=host.Call("1.0/getRequestMetadata",empty (),true) in return decodeRequest raw }
            member _.GetTransactionMetrics()=async {
                let! raw=host.Call("1.0/getTransactionMetrics",empty (),true)
                let metric name : TransactionMetric =
                    let item=field name raw
                    {Used=field "used" item |> Codec.decode<int>;Remaining=field "remaining" item |> Codec.decode<int>}
                return {BytesRead=metric "bytesRead";BytesWritten=metric "bytesWritten";DatabaseQueries=metric "databaseQueries"
                        DocumentsRead=metric "documentsRead";DocumentsWritten=metric "documentsWritten"
                        FunctionsScheduled=metric "functionsScheduled";ScheduledFunctionArgsBytes=metric "scheduledFunctionArgsBytes"} } }

    let private nested (host:ITransactionHostOperations) kind target args options =
        let extra=if isNull options then [] else NativeValue.objectKeys options |> Array.map(fun name -> name,field name options) |> Array.toList
        host.Call("1.0/runUdf",objectValue (functionAddress target @ ["udfType",box kind;"args",args;"version",box version] @ extra),true)

    let queryContext host requestId visibility : QueryCtx =
        { new QueryCtx with
            member _.Db=reader host
            member _.Auth=auth host
            member _.Storage=storageReader host requestId
            member _.Meta=metadata host "query" visibility :> IQueryMeta
            member _.RunQueryRaw(target,args,options)=nested host "query" target args options }

    let mutationContext host requestId visibility : MutationCtx =
        { new MutationCtx with
            member _.Db=writer host
            member _.Auth=auth host
            member _.Storage=storageWriter host requestId
            member _.Scheduler=scheduler host
            member _.Meta=metadata host "mutation" visibility
            member _.RunQueryRaw(target,args,options)=nested host "query" target args options
            member _.RunMutationRaw(target,args,options)=nested host "mutation" target args options }
