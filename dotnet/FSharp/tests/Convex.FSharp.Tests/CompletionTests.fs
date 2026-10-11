namespace Convex.FSharp.Tests

open System
open System.Text.Json
open Xunit
open Convex.FSharp

module CompletionFixtures =
    let objectValue fields =
        let value=ObjectCodec.emptyPlainObject()
        for name,item in fields do JsHelpers.setProperty value name item
        value
    type FilterState = Waiting | Ready of count:int
    let run (work:Async<'T>) = Async.RunSynchronously(work,10000)

module SequentialWorkflowTests =
    let private bound = TimeSpan.FromSeconds 5.

    [<Fact>]
    let ``a later workflow waits for the original completion and values retain order`` () = task {
        let entered = Threading.Tasks.TaskCompletionSource<unit>(Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
        let release = Threading.Tasks.TaskCompletionSource<unit>(Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable later = false
        let first = async {
            entered.SetResult()
            do! release.Task |> Async.AwaitTask
            return 17
        }
        let second = async { later <- true; return 25 }
        let completion = ConvexAsync.sequence [first;second] |> Async.StartAsTask
        try
            do! entered.Task.WaitAsync(bound)
            Assert.False later
            Assert.False completion.IsCompleted
            release.SetResult()
            let! result = completion.WaitAsync(bound)
            Assert.Equal<int>([|17;25|],result)
            Assert.True later
        finally
            release.TrySetResult() |> ignore
    }

    [<Fact>]
    let ``failure and cancellation do not dispatch the next workflow`` () = task {
        let mutable later = false
        let failed = async { return raise (InvalidOperationException "original failure") }
        let next = async { later <- true; return 42 }
        let failedCompletion = ConvexAsync.sequence [failed;next] |> Async.StartAsTask
        let! failure = Assert.ThrowsAsync<InvalidOperationException>(fun () -> failedCompletion.WaitAsync(bound) :> Threading.Tasks.Task)
        Assert.Equal("original failure",failure.Message)
        Assert.False later
        use cancellation = new Threading.CancellationTokenSource()
        let entered = Threading.Tasks.TaskCompletionSource<unit>(Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
        let held = Threading.Tasks.TaskCompletionSource<int>(Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
        let waiting = async { entered.SetResult(); return! held.Task |> Async.AwaitTask }
        let cancelledCompletion = Async.StartAsTask(ConvexAsync.sequence [waiting;next],cancellationToken=cancellation.Token)
        try
            do! entered.Task.WaitAsync(bound)
            cancellation.Cancel()
            Assert.False later
            // AwaitTask preserves the original task's lifetime. Settle that
            // task before observing cancellation of the containing workflow.
            held.SetResult(17)
            let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> cancelledCompletion.WaitAsync(bound) :> Threading.Tasks.Task)
            Assert.True cancelledCompletion.IsCanceled
            Assert.False later
        finally
            held.TrySetResult(17) |> ignore
    }

module CronCompletionTests =
    open CompletionFixtures

    [<Fact>]
    let ``cron JSON escapes identifiers function names and grammar text`` () =
        let jobs=CronJobs.create()
        let identifier="quoted\" and \\ identifier"
        let target="jobs:quoted\"\\target"
        let expression="0 0 * * *\n\"quoted\""
        jobs.ScheduleRaw(identifier,CronSchedule.Cron expression,target,"[{}]")
        use doc=JsonDocument.Parse(jobs.Export())
        let job=doc.RootElement.GetProperty identifier
        Assert.Equal(target,job.GetProperty("name").GetString())
        Assert.Equal(expression,job.GetProperty("schedule").GetProperty("cron").GetString())

    [<Fact>]
    let ``duplicate cron registration does not overwrite earlier jobs`` () =
        let jobs=CronJobs.create()
        jobs.Hourly("repeat",NativeFixtures.unitMutation "jobs:run",())
        Assert.Throws<ArgumentException>(fun () -> jobs.Hourly("repeat",NativeFixtures.unitMutation "jobs:other",())) |> ignore
        Assert.Single(jobs.Jobs) |> ignore
        use doc=JsonDocument.Parse(jobs.Export())
        Assert.Equal("jobs:run",doc.RootElement.GetProperty("repeat").GetProperty("name").GetString())

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(-1)>]
    let ``nonpositive intervals refuse before registration`` value =
        let jobs=CronJobs.create()
        Assert.Throws<ArgumentException>(fun () -> jobs.Interval("invalid",IntervalSchedule.Seconds value,NativeFixtures.unitMutation "jobs:run",())) |> ignore
        Assert.Empty(jobs.Jobs)

    [<Fact>]
    let ``all schedule ranges are checked without inventing default minutes`` () =
        let invalid=[CronSchedule.Hourly(Some 60);CronSchedule.Hourly(Some -1);CronSchedule.Daily(24,None);CronSchedule.Daily(-1,None);CronSchedule.Weekly("Monday",0,None);CronSchedule.Monthly(0,0,None);CronSchedule.Monthly(32,0,None)]
        for schedule in invalid do Assert.Throws<ArgumentException>(fun () -> CronValidation.schedule schedule) |> ignore
        let jobs=CronJobs.create()
        jobs.Daily("daily",23,NativeFixtures.unitMutation "jobs:run",())
        use doc=JsonDocument.Parse(jobs.Export())
        let schedule=doc.RootElement.GetProperty("daily").GetProperty("schedule")
        Assert.False(schedule.TryGetProperty("minuteUTC") |> fst)
        Assert.Equal(23,schedule.GetProperty("hourUTC").GetInt32())

    [<Theory>]
    [<InlineData("[]")>]
    [<InlineData("[null]")>]
    [<InlineData("[{},{}]")>]
    [<InlineData("{}")>]
    let ``raw cron arguments require one object envelope`` json =
        Assert.Throws<ArgumentException>(fun () -> CronValidation.arguments json) |> ignore

    [<Fact>]
    let ``identifiers reject controls and component addresses are not misnamed`` () =
        Assert.Throws<ArgumentException>(fun () -> CronValidation.identifier "line\nbreak") |> ignore
        Assert.Throws<ArgumentException>(fun () -> CronValidation.identifier "nonascii-λ") |> ignore
        let jobs=CronJobs.create()
        Assert.Throws<ArgumentException>(fun () -> jobs.Hourly("component",(NativeFixtures.unitMutation "functions:run" |> FunctionReference.inComponent "counter"),())) |> ignore
        Assert.Empty(jobs.Jobs)

    [<Fact>]
    let ``scheduler milliseconds never silently lose int64 precision`` () =
        ScheduleValidation.delay 0L
        ScheduleValidation.timestamp -1L
        ScheduleValidation.timestamp 9007199254740991L
        Assert.Throws<ArgumentException>(fun () -> ScheduleValidation.delay -1L) |> ignore
        Assert.Throws<ArgumentException>(fun () -> ScheduleValidation.timestamp 9007199254740992L) |> ignore
        Assert.Throws<ArgumentException>(fun () -> ScheduleValidation.timestamp Int64.MinValue) |> ignore

module SchemaCompletionTests =
    [<Fact>]
    let ``staged indexes and schemas retain their owning wire declarations`` () =
        let current=V.object ["name",V.string();"text",V.string();"embedding",V.array(V.number())]
        let next=V.object ["name",V.string();"text",V.string();"embedding",V.array(V.number());"revision",V.number()]
        let table=TableSchema.defineWithValidator "completion_records" current
        let staged=table.WithIndex("by_name",["name"],staged=true).WithSearchIndex("search_text","text",staged=true).WithVectorIndex("vector","embedding",4,staged=true).WithStagedValidator(next)
        use doc=JsonDocument.Parse((Schema.define [staged]).Export())
        let actual=doc.RootElement.GetProperty("tables").[0]
        Assert.Equal(0,actual.GetProperty("indexes").GetArrayLength())
        Assert.Equal(1,actual.GetProperty("stagedDbIndexes").GetArrayLength())
        Assert.Equal(1,actual.GetProperty("stagedSearchIndexes").GetArrayLength())
        Assert.Equal(4,actual.GetProperty("stagedVectorIndexes").[0].GetProperty("dimensions").GetInt32())
        Assert.Equal(next.ToJson(),actual.GetProperty("stagedDocumentType").GetRawText())
        Assert.Throws<ArgumentException>(fun () -> staged.WithIndex("by_name",["name"]) |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () -> staged.WithStagedValidator(next) |> ignore) |> ignore

    [<Fact>]
    let ``schema document helpers recursively preserve union validators`` () =
        let validator=V.union [V.object ["first",V.string()];V.union [V.object ["second",V.number()];V.any()]]
        let schema=Schema.define [TableSchema.defineWithValidator "completion_union" validator]
        Assert.Equal(Validator.Id "completion_union",schema.Id "completion_union")
        Assert.Contains("_creationTime",(schema.Doc "completion_union").ToJson())
        Assert.Throws<ArgumentException>(fun () -> schema.Doc "absent" |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () -> TableSchema.docValidator "invalid" (V.number()) |> ignore) |> ignore

    [<Fact>]
    let ``object validator transformations retain field optionality`` () =
        let original=V.objectWithOptional ["required",V.string(),false;"optional",V.number(),true]
        let picked=V.pick ["optional"] original
        Assert.Equal(V.objectWithOptional ["optional",V.number(),true],picked)
        Assert.Equal(V.object ["required",V.string()],V.omit ["optional"] original)
        Assert.Equal(V.objectWithOptional ["required",V.string(),true;"optional",V.number(),true],V.partial original)
        let replacement={FieldType=V.boolean();Optional=false}
        Assert.Equal(V.objectWithOptional ["required",V.boolean(),false;"optional",V.number(),true],V.extend ["required",replacement] original)
        Assert.Throws<ArgumentException>(fun () -> V.pick ["missing"] original |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () -> V.object ["duplicate",V.string();"duplicate",V.number()] |> ignore) |> ignore

module AdapterCompletionTests =
    open CompletionFixtures

    [<Fact>]
    let ``typed range and search values use the application codec`` () =
        let state=FilterState.Ready 42
        let range=IndexRangeBuilder.Empty.Eq("state",state)
        match range.Ops with
        | [IndexRangeOp.Eq(_,value)] -> Assert.Equal(state,Codec.decode<FilterState> value)
        | _ -> failwith "Expected one equality"
        let filter=SearchFilterBuilder.Create("text","query").Eq("state",state)
        Assert.Equal(state,Codec.decode<FilterState> (snd filter.Filters.Head))
        match IndexRangeBuilder.Empty.EqMissing("optional").Ops with
        | [IndexRangeOp.Eq(_,value)] -> Assert.True(JsHelpers.isUndefined value)
        | _ -> failwith "Expected a missing-field equality"

    [<Fact>]
    let ``native-only adapters never manufacture a successful result`` () =
        let unavailable read =
            let query=Convex.query (fun context () -> read context)
            Assert.Throws<InvalidOperationException>(fun () ->
                NativeFixtures.invoke (FunctionRegistration.erase query) (NativeFixtures.unavailableHost()) NativeFixtures.emptyArguments |> ignore) |> ignore
        unavailable (fun context -> context.Meta.GetFunctionMetadata() |> Async.Ignore)
        unavailable (fun context -> context.Meta.GetTransactionMetrics() |> Async.Ignore)
        unavailable (fun context -> async { context.Meta.GetSnapshotTs() |> ignore })
        // Native filters now produce a descriptor. That is not an executed
        // query and cannot manufacture a successful database result.
        let expression=FilterBuilder(null).Eq(box 1,box 2) |> NativeExpressions.serialize
        Assert.True(JsHelpers.hasProperty expression "$eq")
        Assert.Throws<InvalidOperationException>(fun () -> Convex.createFunctionHandle(NativeFixtures.unitMutation "jobs:run") |> run |> ignore) |> ignore

    [<Fact>]
    let ``transport nulls map to None without weakening application records`` () =
        let metadata=objectValue ["name",box "fixture";"region",null;"class",box "s16"]
        let decoded=Codec.decodeTransport<DeploymentMetadata> metadata
        Assert.Equal(None,decoded.Region)
        Assert.Equal("fixture",decoded.Name)
        let application=objectValue ["Name",box "fixture";"Region",null;"Class",box "s16"]
        Assert.ThrowsAny<Exception>(fun () -> Codec.decode<DeploymentMetadata> application |> ignore) |> ignore

    [<Fact>]
    let ``identity claims preserve custom data and standard typed fields`` () =
        let raw=objectValue ["tokenIdentifier",box "issuer|subject";"subject",box "subject";"issuer",box "https://issuer.invalid";"gender",box "example";"timezone",box "Europe/Istanbul";"language",box "en";"roles",box [|box "reader";box "editor"|];"emailVerified",box true]
        let identity=UserIdentity.decode raw
        Assert.Equal(Some "Europe/Istanbul",identity.Timezone)
        Assert.Equal(Some true,identity.EmailVerified)
        Assert.Equal<string>([|"reader";"editor"|],UserIdentity.claim<string[]> "roles" identity |> Option.get)
        Assert.False(identity.CustomClaims.ContainsKey "subject")
        JsHelpers.setProperty raw "roles" (box [|box "changed"|])
        Assert.Equal<string>([|"reader";"editor"|],UserIdentity.claim<string[]> "roles" identity |> Option.get)
        JsHelpers.setProperty raw "emailVerified" (box "true")
        Assert.ThrowsAny<Exception>(fun () -> UserIdentity.decode raw |> ignore) |> ignore

    [<Fact>]
    let ``vector and nested transaction options reject impossible requests`` () =
        VectorSearchValidation.validate [|1.;0.|] (Some 256)
        for vector in [[||];[|Double.NaN|];[|Double.PositiveInfinity|];Array.zeroCreate 4097] do
            Assert.Throws<ArgumentException>(fun () -> VectorSearchValidation.validate vector None) |> ignore
        for limit in [0;257] do Assert.Throws<ArgumentException>(fun () -> VectorSearchValidation.validate [|1.|] (Some limit)) |> ignore
        Assert.Throws<ArgumentException>(fun () -> NestedOptionsHelper.serializeLimits {TransactionLimits.Empty with DocumentsRead=Some -1} |> ignore) |> ignore


module AsyncResourceCompletionTests =
    open System.Threading
    open System.Threading.Tasks
    open CompletionFixtures

    [<Fact>]
    let ``query iteration closes after early termination`` () =
        let mutable next=0
        let mutable closed=0
        let acquire() =
            { new IQueryIterator<int> with
                member _.Next() = async { next<-next+1; return Some next }
                member _.Close() = async { closed<-closed+1 } }
        let count=QueryIterator.iterateWhile acquire (fun value -> async { return value<2 }) |> run
        Assert.Equal(2,count)
        Assert.Equal(2,next)
        Assert.Equal(1,closed)

    [<Fact>]
    let ``cleanup errors do not hide an existing operation error`` () =
        let primary=InvalidOperationException("primary operation")
        let secondary=InvalidOperationException("cleanup")
        let workflow=AsyncResource.usingAsync (fun () -> ()) (fun () -> async { return raise secondary }) (fun () -> async { return raise primary })
        let result=Assert.Throws<InvalidOperationException>(fun () -> workflow |> run |> ignore)
        Assert.Same(primary,result)

    [<Fact>]
    let ``cleanup failures are reported after a successful operation`` () =
        let failure=InvalidOperationException("cleanup")
        let workflow=AsyncResource.usingAsync (fun () -> ()) (fun () -> async { return raise failure }) (fun () -> async { return 42 })
        Assert.Same(failure,Assert.Throws<InvalidOperationException>(fun () -> workflow |> run |> ignore))

    [<Fact>]
    let ``a synchronous body-construction failure still releases the resource`` () =
        let mutable closed=false
        let workflow=AsyncResource.usingAsync (fun () -> ()) (fun () -> async { closed<-true }) (fun () -> failwith "before workflow creation")
        Assert.ThrowsAny<Exception>(fun () -> workflow |> run |> ignore) |> ignore
        Assert.True(closed)

    [<Fact>]
    let ``cancellation is delivered only after asynchronous cleanup completes`` () : Task = task {
        use cancellation=new CancellationTokenSource()
        let entered=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable closed=false
        let workflow=AsyncResource.usingAsync (fun () -> ())
                        (fun () -> async {
                            do! Async.Sleep 10
                            closed<-true })
                        (fun () -> async {
                            entered.SetResult()
                            do! Async.Sleep 60000
                            return 42 })
        let pending=Async.StartAsTask(workflow,cancellationToken=cancellation.Token)
        do! entered.Task.WaitAsync(TimeSpan.FromSeconds 5.)
        cancellation.Cancel()
        let! _=Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> pending.WaitAsync(TimeSpan.FromSeconds 5.) :> Task)
        Assert.True(closed)
    }

module DocumentPatchCompletionTests =
    [<Fact>]
    let ``a shallow patch preserves set and removal as different wire operations`` () =
        let patch=DocumentPatch.empty |> DocumentPatch.set "Name" "new" |> DocumentPatch.remove "Obsolete"
        let raw=DocumentPatch.toRaw patch
        Assert.Equal("new",unbox<string>(JsHelpers.getProperty raw "Name"))
        Assert.True(JsHelpers.hasProperty raw "Obsolete")
        Assert.True(JsHelpers.isUndefined(JsHelpers.getProperty raw "Obsolete"))
        Assert.Throws<ArgumentException>(fun () -> patch |> DocumentPatch.set "Name" "again" |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () -> DocumentPatch.empty |> DocumentPatch.remove "_id" |> ignore) |> ignore

    [<Fact>]
    let ``record patches retain omission without converting it into removal`` () =
        let raw=DocumentPatch.ofRecord {|Present=Some 42;Omitted=(None:int option)|} |> DocumentPatch.toRaw
        Assert.True(JsHelpers.hasProperty raw "Present")
        Assert.False(JsHelpers.hasProperty raw "Omitted")

    [<Fact>]
    let ``missing storage and system readers fail instead of fabricating absence`` () =
        let unavailable read =
            let query=Convex.query (fun context () -> read context)
            Assert.Throws<InvalidOperationException>(fun () ->
                NativeFixtures.invoke (FunctionRegistration.erase query) (NativeFixtures.unavailableHost()) NativeFixtures.emptyArguments |> ignore) |> ignore
        unavailable (fun context -> context.Storage.GetUrl "fixture" |> Async.Ignore)
        unavailable (fun context -> context.Db.System.GetRaw("_storage","fixture") |> Async.Ignore)


module ExplicitContractTests =
    type Positive = private Positive of int
    let positive = Contract.derived<int>() |> Contract.map (fun (Positive number) -> number) (fun number ->
        if number<=0 then invalidArg "value" "A positive value is required"
        Positive number)

    [<Fact>]
    let ``explicit contracts validate both custom decode and encode`` () =
        Assert.Equal(Positive 7,positive.Decode(box 7))
        Assert.Equal(7,unbox<int>(positive.Encode(Positive 7)))
        Assert.Throws<ArgumentException>(fun () -> positive.Decode(box -1) |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () -> positive.Decode(box "7") |> ignore) |> ignore
        let broken=Contract.create (V.string()) (fun (_:int) -> box 42) (fun _ -> 42)
        Assert.Throws<ArgumentException>(fun () -> broken.Encode 1 |> ignore) |> ignore

    [<Fact>]
    let ``custom contracts snapshot mutable wire data before domain decode`` () =
        let contract=Contract.create (V.object ["value",V.number()]) id id
        let input=CompletionFixtures.objectValue ["value",box 1]
        let decoded=contract.Decode input
        JsHelpers.setProperty input "value" (box 2)
        Assert.Equal(1,unbox<int>(JsHelpers.getProperty decoded "value"))

    [<Fact>]
    let ``unit arguments remain an empty object contract`` () =
        let contract=Contract.arguments<unit>()
        Assert.Empty(JsHelpers.objectKeys(contract.Encode()))
        contract.Decode(CompletionFixtures.objectValue [])
        Assert.ThrowsAny<Exception>(fun () -> contract.Decode null) |> ignore
        Assert.Throws<ArgumentException>(fun () -> Contract.arguments<int>() |> ignore) |> ignore

    [<Fact>]
    let ``literal and object validators reject values outside an explicit contract`` () =
        let contract=Contract.create (V.objectWithOptional ["kind",V.literalString "accepted",false;"count",V.number(),true]) id id
        let accepted=CompletionFixtures.objectValue ["kind",box "accepted"]
        contract.Decode accepted |> ignore
        for fields in [["kind",box "other"];["kind",box "accepted";"extra",box true];["kind",box "accepted";"count",null]] do
            Assert.Throws<ArgumentException>(fun () -> contract.Decode(CompletionFixtures.objectValue fields) |> ignore) |> ignore

module WireAndHttpCompletionTests =
    type Choice = Named of text:string | Empty
    type WireRecord = {Big:int64;Bytes:byte[];Choice:Choice;Optional:int option;Value:float}

    [<Fact>]
    let ``Convex JSON preserves full width integer bytes union and negative zero`` () =
        let value={Big=Int64.MinValue;Bytes=[|0uy;255uy;128uy|];Choice=Named "λ";Optional=None;Value= -0.}
        let json=ConvexWire.encode value
        let decoded=ConvexWire.decode<WireRecord> json
        Assert.Equal(value.Big,decoded.Big)
        Assert.Equal<byte>(value.Bytes,decoded.Bytes)
        Assert.Equal(value.Choice,decoded.Choice)
        Assert.Equal(None,decoded.Optional)
        Assert.Equal(BitConverter.DoubleToInt64Bits(value.Value),BitConverter.DoubleToInt64Bits(decoded.Value))
        Assert.Contains("$integer",json)
        Assert.Contains("$bytes",json)
        Assert.Contains("$float",json)

    [<Fact>]
    let ``special float and placeholder JSON roundtrip without fake numeric observations`` () =
        for value in [Double.NaN;Double.PositiveInfinity;Double.NegativeInfinity] do
            let decoded=ConvexWire.decode<float>(ConvexWire.encode value)
            Assert.True(if Double.IsNaN value then Double.IsNaN decoded else decoded=value)
        let placeholder=ConvexWire.decode<CommitTimestamp>(ConvexWire.encode CommitTimestamp.Placeholder)
        Assert.Equal(CommitTimestamp.Placeholder,placeholder)
        for value in [box "commitTsPlaceholder";box "garbage";box 1.5;box Double.PositiveInfinity;box 9007199254740992.] do
            Assert.ThrowsAny<Exception>(fun () -> Codec.decode<CommitTimestamp> value |> ignore) |> ignore

    [<Theory>]
    [<InlineData("{\"$integer\":\"AA==\"}")>]
    [<InlineData("{\"$integer\":42}")>]
    [<InlineData("{\"$bytes\":null}")>]
    [<InlineData("{\"$commitTs\":1}")>]
    [<InlineData("{\"$unknown\":true}")>]
    [<InlineData("{\"$float\":\"AAAAAAAA8D8=\"}")>]
    let ``malformed special JSON envelopes are rejected`` json =
        Assert.ThrowsAny<Exception>(fun () -> ConvexWire.parseValue json |> ignore) |> ignore

    [<Fact>]
    let ``plain HTTP JSON uses the same FSharp record and DU encoding`` () =
        let value={Big=Int64.MaxValue;Bytes=[|0uy;255uy|];Choice=Named "human";Optional=None;Value=1.5}
        let response=Http.json value
        use json=JsonDocument.Parse response.Body
        Assert.Equal("9223372036854775807",json.RootElement.GetProperty("Big").GetString())
        Assert.Equal("AP8=",json.RootElement.GetProperty("Bytes").GetString())
        Assert.Equal("Named",json.RootElement.GetProperty("Choice").GetProperty("kind").GetString())
        Assert.Equal("human",json.RootElement.GetProperty("Choice").GetProperty("text").GetString())
        Assert.False(json.RootElement.TryGetProperty "Optional" |> fst)
        Assert.Throws<ArgumentException>(fun () -> Http.json Double.NaN |> ignore) |> ignore

    [<Fact>]
    let ``bodyless HTTP statuses and duplicate response headers are explicit`` () =
        Assert.Equal(204,(Http.validate Http.noContent).Status)
        for status in [204;205;304] do
            Assert.Throws<ArgumentException>(fun () -> Http.validate(Http.response status "unexpected body" []) |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () -> Http.validate(Http.response 199 "" []) |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () -> Http.validate(Http.response 600 "" []) |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () -> Http.validate(Http.response 200 "" ["bad header","value"]) |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () -> Http.validate(Http.response 200 "" ["X-Value","a\r\nb"]) |> ignore) |> ignore
        let response=Http.ok "body" |> Http.withHeader "X-Value" "one" |> Http.withHeader "X-Value" "two"
        Assert.Equal(2,response.Headers |> List.filter(fun (name,_) -> name="X-Value") |> List.length)

module ConfigurationCompletionTests =
    [<Fact>]
    let ``configuration rejects invalid environment declarations and unbound parent references`` () =
        let app=Configuration.appWithEnvironment [ComponentEnvironment.required "REGION" (V.union [V.literalString "local";V.literalString "cloud"])]
        Assert.Equal(EnvironmentBinding.FromParent "REGION",app.Env "REGION")
        Assert.Throws<ArgumentException>(fun () -> app.Env "UNDECLARED" |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () -> Configuration.appWithEnvironment [ComponentEnvironment.required "INVALID" (V.number())] |> ignore) |> ignore
        let imported=ComponentImport.create "counter" (Configuration.defineComponent("counter").ToNative())
        app.Use(imported,name="counter",environment=["REGION",app.Env "REGION"]) |> ignore
        Assert.Equal(1,app.MountCount)
        Assert.Throws<ArgumentException>(fun () -> app.Use(imported,name="counter") |> ignore) |> ignore

    [<Fact>]
    let ``router declaration follows exact SDK prefix and duplicate rules`` () =
        let handler=Convex.httpActionResponse (fun _ _ -> async { return Http.ok "isolated declaration" })
        HttpRouter.validate [HttpRoute.Exact(HttpMethod.Get,"/health",handler);HttpRoute.Prefix(HttpMethod.Get,"/items/",handler)] |> ignore
        for routes in [[HttpRoute.Exact(HttpMethod.Get,"health",handler)];[HttpRoute.Prefix(HttpMethod.Get,"/items",handler)];[HttpRoute.Exact(HttpMethod.Get,"/.files",handler)];[HttpRoute.Exact(HttpMethod.Get,"/health",handler);HttpRoute.Exact(HttpMethod.Get,"/health",handler)]] do
            Assert.Throws<ArgumentException>(fun () -> HttpRouter.validate routes |> ignore) |> ignore

    [<Fact>]
    let ``audit variable encoding is inspectable without recording a fake event`` () =
        let fields=Map.ofList ["request",AuditValue.Variable AuditVariable.RequestId;"nested",AuditValue.Object(Map.ofList ["time",AuditValue.Variable AuditVariable.Now]);"omitted",AuditValue.Omitted]
        let result=AuditLog.envelope fields
        Assert.Equal("requestId",JsHelpers.getProperty (JsHelpers.getProperty result "request") "$var" |> unbox<string>)
        Assert.False(JsHelpers.hasProperty result "omitted")
        Assert.Throws<ArgumentException>(fun () -> AuditLog.envelope(Map.ofList ["$private",AuditValue.Null]) |> ignore) |> ignore
        Assert.Throws<InvalidOperationException>(fun () -> AuditLog.write fields |> CompletionFixtures.run) |> ignore

module AuditBoundaryRegressionTests =
    open CompletionFixtures
    type RequiredNullable = { required: NullOr<string> }
    type RequiredAny = { required: obj }
    type RequiredNullableUnion = Case of required:NullOr<string>

    [<Fact>]
    let ``required nullable and raw fields reject missing values`` () =
        let missing=objectValue []
        Assert.ThrowsAny<Exception>(fun()->Codec.decode<RequiredNullable> missing |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun()->Codec.decode<RequiredAny> missing |> ignore) |> ignore
        Assert.ThrowsAny<Exception>(fun()->Codec.decode<obj> (JsHelpers.undefinedValue()) |> ignore) |> ignore
        let explicitNull=objectValue ["required",null]
        Assert.Equal(NullOr.Null,(Codec.decode<RequiredNullable> explicitNull).required)
        Assert.Null((Codec.decode<RequiredAny> explicitNull).required)

    [<Fact>]
    let ``derived root absence has the nullable wire representation`` () =
        let option=Contract.derived<string option>()
        let valueOption=Contract.derived<string voption>()
        Assert.Null(option.Encode None)
        Assert.Equal(None,option.Decode null)
        Assert.Null(valueOption.Encode ValueNone)
        Assert.Equal(ValueNone,valueOption.Decode null)
        let custom=Contract.create (V.any()) (fun (_:unit)->JsHelpers.undefinedValue()) (fun _->())
        Assert.ThrowsAny<Exception>(fun()->custom.Encode() |> ignore) |> ignore

    [<Fact>]
    let ``native ordering matches Convex special float bits and scalar strings`` () =
        let positiveNaN=BitConverter.Int64BitsToDouble(0x7ff8000000000000L)
        let otherNaN=BitConverter.Int64BitsToDouble(0x7ff8000000000001L)
        let negativeZero=BitConverter.Int64BitsToDouble(Int64.MinValue)
        Assert.True(ConvexValues.compare (box positiveNaN) (box Double.PositiveInfinity)>0)
        Assert.True(ConvexValues.compare (box Double.NaN) (box Double.NegativeInfinity)<0)
        Assert.True(ConvexValues.compare (box positiveNaN) (box otherNaN)<0)
        Assert.True(ConvexValues.compare (box negativeZero) (box 0.)<0)
        Assert.True(ConvexValues.compare (box "\U00010000") (box "\uE000")>0)
        Assert.True(ValidatorCheck.matches (V.literalFloat positiveNaN) (box positiveNaN))
        Assert.False(ValidatorCheck.matches (V.literalFloat negativeZero) (box 0.))
