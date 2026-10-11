module Counter

open Convex.FSharp

type CounterDocument = {name:string;value:float}
type Name = {name:string}
type ById = {id:Id<CounterDocument>}
type Increment = {name:string;amount:float}
type Page = {name:string;cursor:string option}
type Echo = {value:ConvexValue}
type Refusal = {operation:string;reason:string}
type SearchDocument = {name:string;category:string;text:string;embedding:float[]}
type SearchArgs = {category:string;query:string}
type VectorArgs = {category:string;vector:float[]}
type VectorHit = {id:string;score:float}
type ScheduledIdentifier = {id:string}
type HandleRead = {handle:string;name:string}
type CommitDocument = {name:string;committedAt:CommitTimestamp}
type CommitArgument = {committedAt:CommitTimestamp}

let counters=Table.define<CounterDocument> "runtimeCounters"
let searchDocuments=Table.define<SearchDocument> "runtimeSearch"
let commitTimes=Table.define<CommitDocument> "runtimeCommitTimes"
let private textIndexProposal=searchDocuments.SearchIndex("by_text","text",["category"])
let private vectorIndexProposal=searchDocuments.VectorIndex("by_embedding","embedding",4,["category"])
let private searchDefinition=(TableSchema.defineForTable searchDocuments).WithSearchIndex(textIndexProposal).WithVectorIndex(vectorIndexProposal)
let textIndex=searchDefinition.BindSearchIndex(searchDocuments,textIndexProposal)
let vectorIndex=searchDefinition.BindVectorIndex(searchDocuments,vectorIndexProposal)
let tableSchemas () = [
    TableSchema.defineForTable counters |> fun table -> table.WithIndex("by_name",["name"])
    searchDefinition
    TableSchema.defineForTable commitTimes |> fun table -> table.WithIndex("by_name",["name"])
]
let schema=tableSchemas () |> Schema.define

let echo=Convex.query (fun _ (args:Echo) -> async { return args.value })

let read=Convex.query (fun (ctx:QueryCtx) (args:Name) -> async {
    return! ctx.Db.Query(counters).WithIndex("by_name",fun range -> range.Eq("name",args.name)).Unique()
})

let readById=Convex.query (fun (ctx:QueryCtx) (args:ById) -> ctx.Db.Get(counters,args.id))

let handleRead=Convex.query (fun (ctx:QueryCtx) (args:HandleRead) ->
    ctx.RunQuery((FunctionReference.publicQuery "Counter:read" (Contract.arguments<Name>()) (Contract.derived<CounterDocument option>()) |> FunctionReference.withHandle args.handle),{name=args.name}))

let commitRead=Convex.query (fun (ctx:QueryCtx) (args:Name) ->
    ctx.Db.Query(commitTimes).WithIndex("by_name",fun range -> range.Eq("name",args.name)).Unique())

let commitVariable=Convex.query (fun _ (_:unit) -> async { return CommitTimestamp.Placeholder })
let commitEcho=Convex.query (fun _ (args:CommitArgument) -> async { return args.committedAt })

let commitWrite=Convex.mutation (fun (ctx:MutationCtx) (args:Name) -> async {
    let timestamp=ctx.Db.Vars.CommitTs
    let! _=ctx.Db.Insert(commitTimes,{name=args.name;committedAt=timestamp})
    return timestamp
})

let commitNested=Convex.mutation (fun (ctx:MutationCtx) (args:Name) -> async {
    let! variable=ctx.RunQuery(FunctionReference.publicQuery "Counter:commitVariable" (Contract.arguments<unit>()) (Contract.derived<CommitTimestamp>()),())
    let! timestamp=ctx.RunQuery(FunctionReference.publicQuery "Counter:commitEcho" (Contract.arguments<CommitArgument>()) (Contract.derived<CommitTimestamp>()),{committedAt=variable})
    let! _=ctx.Db.Insert(commitTimes,{name=args.name;committedAt=timestamp})
    return timestamp
})

let list=Convex.query (fun (ctx:QueryCtx) (args:Name) -> async {
    let! values=ctx.Db.Query(counters).WithIndex("by_name",fun range -> range.Eq("name",args.name)).Take(100)
    return List.toArray values
})

let insertSearch=Convex.mutation (fun (ctx:MutationCtx) (args:SearchDocument) -> async {
    let! id=ctx.Db.Insert(searchDocuments,args)
    return Id.value id
})

let search=Convex.query (fun (ctx:QueryCtx) (args:SearchArgs) -> async {
    let! found=ctx.Db.Query(searchDocuments).WithSearchIndex(textIndex,args.query,filter=(fun filter -> filter.Eq("category",args.category))).Take(10)
    return found |> List.map(fun doc -> doc.name) |> List.toArray
})

let vectors=Convex.action (fun (ctx:ActionCtx) (args:VectorArgs) -> async {
    let! hits=ctx.VectorSearch(vectorIndex,args.vector,limit=2,filter=(fun filter -> filter.Eq("category",args.category)))
    return hits |> List.map(fun hit -> {id=Id.value hit.Id;score=hit.Score}) |> List.toArray
})

let schedule=Convex.mutation (fun (ctx:MutationCtx) (args:Increment) -> async {
    let! id=ctx.Scheduler.RunAfter(0L,FunctionReference.publicMutation "AliasCounter:increment" (Contract.arguments<Increment>()) (Contract.derived<CounterDocument>()),args)
    return Id.value id
})

let scheduleCancel=Convex.mutation (fun (ctx:MutationCtx) (args:Increment) -> async {
    let! id=ctx.Scheduler.RunAfter(60000L,FunctionReference.publicMutation "AliasCounter:increment" (Contract.arguments<Increment>()) (Contract.derived<CounterDocument>()),args)
    do! ctx.Scheduler.Cancel id
    return Id.value id
})

let scheduleFail:FunctionRegistration<MutationKind,PublicVisibility,Increment,string>=Convex.mutation (fun (ctx:MutationCtx) (args:Increment) -> async {
    let! id=ctx.Scheduler.RunAfter(0L,FunctionReference.publicMutation "AliasCounter:increment" (Contract.arguments<Increment>()) (Contract.derived<CounterDocument>()),args)
    return Errors.failWithConvexError {|operation=args.name;reason="schedule-refused";scheduledId=Id.value id|}
})

let crons =
    let jobs=Crons()
    jobs.Interval("runtime-parity",IntervalSchedule.Seconds 5,
        FunctionReference.publicMutation "AliasCounter:increment" (Contract.arguments<Increment>()) (Contract.derived<CounterDocument>()),{name="cron-counter";amount=1.})
    jobs

let scheduledState=Convex.query (fun (ctx:QueryCtx) (args:ScheduledIdentifier) -> async {
    let! doc=ctx.Db.System.Get(Id.ofString<SystemTable.ScheduledFunctionsMarker> args.id)
    return doc |> Option.map(fun doc ->
        match doc.State with
        | ScheduledFunctionState.Pending -> "pending"
        | ScheduledFunctionState.InProgress -> "inProgress"
        | ScheduledFunctionState.Success -> "success"
        | ScheduledFunctionState.Failed _ -> "failed"
        | ScheduledFunctionState.Canceled -> "canceled")
})

let componentRead=Convex.query (fun (ctx:QueryCtx) (args:Name) ->
    ctx.RunQuery((FunctionReference.publicQuery "counter:read" (Contract.arguments<Name>()) (Contract.derived<CounterDocument option>()) |> FunctionReference.inComponent "probe"),args))

let componentIncrement=Convex.mutation (fun (ctx:MutationCtx) (args:Increment) ->
    ctx.RunMutation((FunctionReference.publicMutation "counter:increment" (Contract.arguments<Increment>()) (Contract.derived<CounterDocument>()) |> FunctionReference.inComponent "probe"),args))

let page=Convex.query (fun (ctx:QueryCtx) (args:Page) -> async {
    return! ctx.Db.Query(counters).WithIndex("by_name",fun range -> range.Eq("name",args.name)).Paginate({NumItems=2;Cursor=args.cursor})
})

let increment=Convex.mutation (fun (ctx:MutationCtx) (args:Increment) -> async {
    let! previous=ctx.Db.Query(counters).WithIndex("by_name",fun range -> range.Eq("name",args.name)).UniqueDoc()
    let value={name=args.name;value=(previous |> Option.map(fun item -> item.Data.value) |> Option.defaultValue 0.)+args.amount}
    match previous with
    | None -> let! _=ctx.Db.Insert(counters,value) in ()
    | Some previous -> do! ctx.Db.Replace(previous.Id,value)
    return value
})

let fail:FunctionRegistration<MutationKind,PublicVisibility,Name,string>=Convex.mutation (fun (ctx:MutationCtx) (args:Name) -> async {
    let! _=ctx.Db.Insert(counters,{name=args.name;value=999.})
    return Errors.failWithConvexError {|reason="refused";operation=args.name|}
})

// Deliberately bypass the typed table writer. The owning backend schema must
// reject this document and roll back its mutation on both execution targets.
let invalidDocument=Convex.mutation (fun (ctx:MutationCtx) (args:Name) ->
    ctx.Db.InsertRaw("runtimeCounters",box {|name=args.name;value="invalid"|}))

let identity=Convex.query (fun (ctx:QueryCtx) (_:unit) -> async {
    let! identity=ctx.Auth.GetUserIdentity()
    return identity |> Option.map(fun identity -> identity.Subject)
})

let clock=Convex.query (fun _ (_:unit) -> async {
    return [|InvocationRuntime.nowMilliseconds();InvocationRuntime.nowMilliseconds()|]
})

let randomValue=Convex.query (fun _ (_:unit) -> async { return InvocationRuntime.random() })
let environmentValue=Convex.query (fun _ (args:Name) -> async { return InvocationRuntime.environmentVariable args.name })

let authorizedIncrement=Convex.mutation (fun (ctx:MutationCtx) (args:Increment) -> async {
    let! identity=ctx.Auth.GetUserIdentity()
    if identity.IsNone then invalidOp "Verified identity required"
    let! value=ctx.Db.Query(counters).WithIndex("by_name",fun range -> range.Eq("name",args.name)).UniqueDoc()
    let next={name=args.name;value=(value |> Option.map(fun item -> item.Data.value) |> Option.defaultValue 0.)+args.amount}
    match value with
    | Some previous -> do! ctx.Db.Replace(previous.Id,next)
    | None -> let! _=ctx.Db.Insert(counters,next) in ()
    return next
})

let filtered=Convex.query (fun (ctx:QueryCtx) (args:Increment) -> async {
    let! values=ctx.Db.Query(counters).WithIndex("by_name",fun range -> range.Eq("name",args.name))
                    .Filter(fun filter -> filter.Gte(filter.Field "value",filter.Value args.amount)).Take(10)
    return List.toArray values
})

let nestedRead=Convex.query (fun (ctx:QueryCtx) (args:Name) ->
    ctx.RunQuery(FunctionReference.publicQuery "Counter:read" (Contract.arguments<Name>()) (Contract.derived<CounterDocument option>()),args))

let nestedIncrement=Convex.mutation (fun (ctx:MutationCtx) (args:Increment) ->
    ctx.RunMutation(FunctionReference.publicMutation "Counter:increment" (Contract.arguments<Increment>()) (Contract.derived<CounterDocument>()),args))

let nestedRollback=Convex.mutation (fun (ctx:MutationCtx) (args:Name) -> async {
    let! refusal=async {
        try
            let! _=ctx.RunMutation(FunctionReference.publicMutation "Counter:fail" (Contract.arguments<Name>()) (Contract.derived<string>()),args)
            return invalidOp "The nested mutation should have been refused"
        with
        | ConvexError (data:Refusal) -> return data
    }
    // The child insert must have rolled back before the parent continues.
    let! rolledBack=ctx.Db.Query(counters).WithIndex("by_name",fun range -> range.Eq("name",args.name)).Unique()
    if rolledBack.IsSome then invalidOp "A refused nested mutation retained its write"
    let! _=ctx.Db.Insert(counters,{name=args.name;value=1.})
    return refusal
})

let incrementAction=Convex.action (fun (ctx:ActionCtx) (args:Increment) ->
    ctx.RunMutation(FunctionReference.publicMutation "Counter:increment" (Contract.arguments<Increment>()) (Contract.derived<CounterDocument>()),args))

let handle=Convex.query (fun _ (_:unit) -> async {
    let! target=Convex.createFunctionHandle(FunctionReference.publicQuery "Counter:read" (Contract.arguments<Name>()) (Contract.derived<CounterDocument option>()))
    return target.Value
})

let storageRoundtrip=Convex.action (fun (ctx:ActionCtx) (args:Echo) -> async {
    let bytes=Codec.encode<ConvexValue> args.value |> Codec.decode<byte[]>
    let! id=ctx.Storage.Store(bytes,contentType="application/octet-stream")
    let! stored=ctx.Storage.Get id
    let! metadata=ctx.Storage.GetMetadata id
    do! ctx.Storage.Delete id
    return {|bytes=stored |> Option.defaultWith(fun () -> invalidOp "Stored file disappeared")
             contentType=metadata |> Option.bind(fun value -> value.ContentType)|}
})

let httpEcho=Convex.httpActionRequestResponse (fun _ request -> async {
    let clone=request.Clone()
    if request.BodyUsed || clone.BodyUsed then invalidOp "The HTTP body was consumed before its handler"
    let! bytes=request.Bytes()
    let! copied=clone.Bytes()
    if (request.Method="POST" && not request.BodyUsed) || bytes<>copied then invalidOp "HTTP body clone semantics changed"
    return Http.bytes bytes "application/octet-stream"
           |> Http.withHeader "x-native-probe" (request.Header "x-native-probe" |> Option.defaultValue "absent")
})

let httpMutation=Convex.httpActionRequestResponse (fun context request -> async {
    let! args=request.Json<Increment>()
    let! value=context.RunMutation(FunctionReference.publicMutation "Counter:increment" (Contract.arguments<Increment>()) (Contract.derived<CounterDocument>()),args)
    return Http.json value
})

let httpHang=Convex.httpActionRequestResponse (fun _ _ -> async {
    while true do ()
    return Http.ok "unreachable"
})

type StreamingSize = { size:int }

let private streamSource size =
    if size<0 || size>20*1024*1024 then invalidArg "size" "Native stream fixture requires at most 20 MiB"
    let mutable offset=0
    let mutable closed=false
    { new INativeByteSource with
        member _.Read()=async {
            if closed then invalidOp "Source is closed"
            if offset=size then return None else
            let count=min 65536 (size-offset)
            let bytes=Array.init count (fun index -> byte ((offset+index)%251))
            offset<-offset+count
            return Some bytes
        }
        member _.Close()=async { closed<-true } }

let httpStream=Convex.httpActionRequest (fun _ request -> async {
    let size=request.Header "x-native-size" |> Option.map int |> Option.defaultValue (9*1024*1024)
    return Http.stream 200 ["content-type","application/octet-stream"] (streamSource size) |> NativeHttpResponse.Streamed
})

let storageStreamRoundtrip=Convex.action (fun (ctx:ActionCtx) (args:StreamingSize) -> async {
    let storage=(box ctx.Storage) :?> INativeStorageStreaming
    let! upload=storage.OpenWrite(contentType="application/octet-stream",contentLength=int64 args.size)
    let! id=AsyncResource.usingAsync (fun () -> upload) (fun upload -> upload.Abort()) (fun upload ->
        AsyncResource.usingAsync (fun () -> streamSource args.size) (fun source -> source.Close()) (fun source -> async {
            let mutable finished=false
            while not finished do
                let! part=source.Read()
                match part with None -> finished<-true | Some bytes -> do! upload.Write bytes
            return! upload.Commit()
        }))
    let! download=storage.OpenRead id
    let download=download |> Option.defaultWith(fun () -> invalidOp "Uploaded native stream disappeared")
    use hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256)
    let! length=AsyncResource.usingAsync (fun () -> download) (fun source -> source.Close()) (fun source -> async {
        let mutable length=0L
        let mutable finished=false
        while not finished do
            let! part=source.Read()
            match part with
            | None -> finished<-true
            | Some bytes -> hash.AppendData bytes;length<-length+int64 bytes.Length
        if length<>source.ContentLength then invalidOp "Stored stream length changed"
        return length
    })
    let digestBytes=hash.GetHashAndReset()
    let digest=System.Convert.ToBase64String digestBytes
    let! metadata=ctx.Storage.GetMetadata id
    if metadata |> Option.exists(fun value -> value.Sha256=System.Convert.ToHexStringLower digestBytes && value.Size=length) |> not then invalidOp "Stored stream hash changed"
    do! ctx.Storage.Delete id
    return {|length=length;sha256=digest;contentType=download.ContentType|}
})

let httpRouter=HttpRouter.create [
    HttpRoute.Exact(HttpMethod.Post,"/native/echo",httpEcho)
    HttpRoute.Prefix(HttpMethod.Get,"/native/prefix/",httpEcho)
    HttpRoute.Exact(HttpMethod.Post,"/native/mutation",httpMutation)
    HttpRoute.Exact(HttpMethod.Post,"/native/hang",httpHang)
    HttpRoute.Exact(HttpMethod.Get,"/native/stream",httpStream)
]

// Deadline proof: this handler does not yield or cooperate with cancellation.
let hang=Convex.query (fun _ (_:unit) -> async {
    while true do ()
    return ()
})

let nestedHang=Convex.query (fun (ctx:QueryCtx) (_:unit) ->
    ctx.RunQuery(FunctionReference.publicQuery "Counter:hang" (Contract.arguments<unit>()) (Contract.derived<unit>()),()))

let hangMutation=Convex.mutation (fun (ctx:MutationCtx) (args:Name) -> async {
    let! _=ctx.Db.Insert(counters,{name=args.name;value=999.})
    while true do ()
    return ()
})

let hangAction=Convex.action (fun _ (_:unit) -> async {
    while true do ()
    return ()
})
