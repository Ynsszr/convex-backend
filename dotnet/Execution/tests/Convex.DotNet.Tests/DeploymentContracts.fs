namespace Convex.DotNet.Tests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Convex.DotNet.Deployment

module DeploymentContracts =
    let private graph source =
        JsonSerializer.Serialize {|functions="convex";dryRun=false;nodeVersion=Unchecked.defaultof<string>;nodeDependencies=Array.empty<obj>;
            appDefinition={|definition=Unchecked.defaultof<obj>;dependencies=[|"child"|];schema=Unchecked.defaultof<obj>;changedModules=Array.empty<obj>;unchangedModuleHashes=Array.empty<obj>;udfServerVersion="1.46.0"|};
            componentDefinitions=[|{|definitionPath="child";definition={|path="convex.config.js";environment="isolate";source="component-declaration"|};dependencies=Array.empty<string>;schema=Unchecked.defaultof<obj>;functions=[|{|path="counter.js";environment="isolate";source=source|}|];udfServerVersion="1.46.0"|}|]|}
    let private prepare raw =
        use document=JsonDocument.Parse(raw:string)
        NativeDeployment.prepareGraph document.RootElement

    [<Fact>]
    let ``compiled graphs retain component namespaces and refuse stored administrator credentials`` () =
        let prepared=prepare(graph "component-function")
        let review=NativeDeployment.review prepared
        Assert.Equal(2,review.Length)
        Assert.Contains("component(child)/counter.js",review |> Array.map fst)
        Assert.Contains("component(child)/convex.config.js",review |> Array.map fst)
        Assert.NotEqual<string>(NativeDeployment.graphHash prepared,NativeDeployment.graphHash(prepare(graph "changed-function")))
        let injected=(graph "component-function").TrimEnd('}')+",\"adminKey\":\"private\"}"
        Assert.Throws<ArgumentException>(fun () -> prepare injected |> ignore) |> ignore

    let private owningReplies invalidActivation = task {
        use port=new TcpListener(IPAddress.Loopback,0)
        port.Start()
        let address=port.LocalEndpoint :?> IPEndPoint
        port.Stop()
        use listener=new HttpListener()
        let origin=Uri("http://127.0.0.1:"+string address.Port+"/")
        listener.Prefixes.Add(origin.AbsoluteUri)
        listener.Start()
        let requests=Collections.Generic.List<string>()
        let owner=task {
            for step in ["start_push";"wait_for_schema";"finish_push"] do
                let! request=listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds 10.)
                requests.Add request.Request.Url.AbsolutePath
                use reader=new StreamReader(request.Request.InputStream,Encoding.UTF8)
                let! body=reader.ReadToEndAsync()
                use payload=JsonDocument.Parse body
                Assert.Equal("private-admin",payload.RootElement.GetProperty("adminKey").GetString())
                if step="start_push" then
                    let child=payload.RootElement.GetProperty("componentDefinitions").EnumerateArray() |> Seq.exactlyOne
                    let functionModule=child.GetProperty("functions").EnumerateArray() |> Seq.exactlyOne
                    let dependency=payload.RootElement.GetProperty("appDefinition").GetProperty("dependencies").EnumerateArray() |> Seq.exactlyOne
                    Assert.Equal("component-function",functionModule.GetProperty("source").GetString())
                    Assert.Equal("child",dependency.GetString())
                let reply=
                    match step with
                    | "start_push" -> "{\"schemaChange\":{\"schemaIds\":{}},\"app\":{}}"
                    | "wait_for_schema" -> "{\"type\":\"complete\"}"
                    | _ when invalidActivation -> "owning-commit-reply-was-lost"
                    | _ -> "{\"owner\":\"activated\"}"
                let bytes=Encoding.UTF8.GetBytes reply
                request.Response.StatusCode<-200
                request.Response.ContentLength64<-int64 bytes.Length
                do! request.Response.OutputStream.WriteAsync(ReadOnlyMemory<byte>(bytes)).AsTask()
                request.Response.Close()
        }
        let markers=Collections.Generic.List<DeploymentPhase>()
        let! outcome=NativeDeployment.activateObserved origin "private-admin" (prepare(graph "component-function")) markers.Add CancellationToken.None
        do! owner
        Assert.Equal<string>([|"/api/deploy2/start_push";"/api/deploy2/wait_for_schema";"/api/deploy2/finish_push"|],requests)
        Assert.Equal<DeploymentPhase>([|Preparing;WaitingForSchema;Activating|],markers)
        return outcome
    }

    [<Fact>]
    let ``activation preserves the compiled component graph through one owning commit`` () = task {
        let! outcome=owningReplies false
        match outcome with
        | Confirmed receipt -> Assert.Equal("activated",receipt.OwningResponse.GetProperty("owner").GetString())
        | _ -> failwith "A complete owning response should confirm activation"
    }

    [<Fact>]
    let ``unreadable owning activation replies remain uncertain without repeating the commit`` () = task {
        let! outcome=owningReplies true
        match outcome with
        | Uncertain Activating -> ()
        | _ -> failwith "An unreadable commit response must preserve uncertainty"
    }

    // Drive the production owner's private TimeProvider seam; its public API
    // always uses System time and exposes no caller-selected deadline policy.
    type private ControlledTimer = {
        Callback:TimerCallback;State:obj;mutable Due:int64;mutable Period:int64;mutable Active:bool
    }
    type private ControlledClock() =
        inherit TimeProvider()
        let gate=obj()
        let timers=Collections.Generic.List<ControlledTimer>()
        let mutable ticks=0L
        member _.Advance(duration:TimeSpan) =
            let selected=lock gate (fun () ->
                ticks<-ticks+duration.Ticks
                timers |> Seq.filter(fun timer -> timer.Active && timer.Due<=ticks)
                |> Seq.map(fun timer ->
                    if timer.Period<0L then timer.Active<-false else timer.Due<-ticks+timer.Period
                    timer.Callback,timer.State) |> Seq.toArray)
            for callback,state in selected do callback.Invoke state
        override _.CreateTimer(callback,state,dueTime,period) =
            let timer={Callback=callback;State=state;Due=0L;Period=period.Ticks;Active=false}
            let change (due:TimeSpan) (period:TimeSpan) = lock gate (fun () ->
                timer.Active<-due<>Timeout.InfiniteTimeSpan
                timer.Due<-ticks+due.Ticks;timer.Period<-period.Ticks;true)
            change dueTime period |> ignore
            lock gate (fun () -> timers.Add timer)
            {new ITimer with
                member _.Change(due,period)=change due period
                member _.Dispose()=lock gate (fun () -> timer.Active<-false)
                member _.DisposeAsync()=lock gate (fun () -> timer.Active<-false);ValueTask()}

    let private timedOwner (clock:TimeProvider) origin prepared operation before record cancellation =
        let owner=typeof<PreparedDeployment>.Assembly.GetType("Convex.DotNet.Deployment.NativeDeployment")
        let method=owner.GetMethod("activateOwner",Reflection.BindingFlags.Static ||| Reflection.BindingFlags.NonPublic)
        Assert.NotNull method
        method.Invoke(null,[|box clock;box origin;box "private-admin";box prepared;box (Some operation);
                            box before;box record;box cancellation|]) :?> Task<ActivationOutcome>

    let private heldReply cancelledPhase callerCancellation totalExpiry = task {
        use listener=new HttpListener()
        use port=new TcpListener(IPAddress.Loopback,0)
        port.Start()
        let address=port.LocalEndpoint :?> IPEndPoint
        port.Stop()
        let origin=Uri("http://127.0.0.1:"+string address.Port+"/")
        listener.Prefixes.Add(origin.AbsoluteUri);listener.Start()
        let clock=ControlledClock()
        use caller=new CancellationTokenSource()
        let directory=Path.Combine(Path.GetTempPath(),"convex-native-deadline-"+Guid.NewGuid().ToString("N"))
        let prepared=prepare(graph "component-function")
        let requests=Collections.Generic.List<string>()
        let mutable operation=""
        let mutable retained=None
        try
            let! result=task {
                use journal=new Convex.DotNet.DeploymentJournal.Journal(origin,NativeDeployment.graphHash prepared,stateDirectory=directory)
                operation<-journal.OperationId
                let before phase=
                    journal.Write(phase,Convex.DotNet.DeploymentJournal.JournalOutcome.Sent)
                    if totalExpiry && phase=Activating then clock.Advance(TimeSpan.FromSeconds 386.)
                let recorded token=journal.RecordPrepared token;retained<-Some token
                let activation=timedOwner clock origin prepared operation before recorded caller.Token
                let reply (context:HttpListenerContext) (body:string) = task {
                    let bytes=Encoding.UTF8.GetBytes body
                    context.Response.StatusCode<-200;context.Response.ContentLength64<-int64 bytes.Length
                    do! context.Response.OutputStream.WriteAsync(ReadOnlyMemory<byte>(bytes)).AsTask()
                    context.Response.Close()
                }
                let observe expected = task {
                    let! context=listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds 5.)
                    requests.Add context.Request.Url.AbsolutePath
                    Assert.Equal(expected,context.Request.Url.AbsolutePath)
                    use reader=new StreamReader(context.Request.InputStream,Encoding.UTF8)
                    let! body=reader.ReadToEndAsync()
                    use document=JsonDocument.Parse body
                    if expected.EndsWith("start_push",StringComparison.Ordinal) then
                        Assert.Equal(operation,document.RootElement.GetProperty("nativeDeploymentId").GetString())
                    return context
                }
                let! start=observe "/api/deploy2/start_push"
                if cancelledPhase=Preparing then
                    if callerCancellation then caller.Cancel() else clock.Advance(TimeSpan.FromSeconds 301.)
                else
                    // This exceeds the previous global90s while actual start_push is held.
                    clock.Advance(TimeSpan.FromSeconds 95.)
                    Assert.False(activation.IsCompleted,"Valid cold preparation must survive the former90s deadline")
                    let receipt="{\"schemaChange\":{\"schemaIds\":{}},\"app\":{},\"nativeReceipt\":{\"operationId\":\""+operation+"\",\"startPushSha256\":\""+String('a',64)+"\"}}"
                    do! reply start receipt
                    let! schema=observe "/api/deploy2/wait_for_schema"
                    Assert.Equal(Some {OperationId=operation;StartPushSha256=String('a',64)},retained)
                    if cancelledPhase=WaitingForSchema then clock.Advance(TimeSpan.FromSeconds 61.)
                    else
                        do! reply schema "{\"type\":\"complete\"}"
                        if not totalExpiry then
                            let! finish=observe "/api/deploy2/finish_push"
                            // The original prepared token exists before the single owning commit request.
                            Assert.Equal(Some {OperationId=operation;StartPushSha256=String('a',64)},journal.PendingToken)
                            clock.Advance(TimeSpan.FromSeconds 91.)
                            finish.Response.Close()
                let! result=activation.WaitAsync(TimeSpan.FromSeconds 5.)
                match result with
                | Uncertain phase when phase=cancelledPhase ->
                    journal.Write(phase,Convex.DotNet.DeploymentJournal.JournalOutcome.Uncertain)
                | _ -> failwith "An expired original transition must remain uncertain at its actual phase"
                return result
            }
            use reopened=new Convex.DotNet.DeploymentJournal.Journal(origin,NativeDeployment.graphHash prepared,stateDirectory=directory)
            Assert.Equal(Convex.DotNet.DeploymentJournal.Uncertain,reopened.Prior)
            Assert.Equal(operation,reopened.OperationId)
            Assert.Equal(retained,reopened.PendingToken)
            let expected=match cancelledPhase with Preparing -> 1 | WaitingForSchema -> 2 | Activating -> if totalExpiry then 2 else 3
            Assert.Equal(expected,requests.Count)
            Assert.Equal(requests.Count,requests |> Seq.distinct |> Seq.length)
            return result
        finally
            listener.Stop()
            if Directory.Exists directory then Directory.Delete(directory,true)
    }

    [<Fact>]
    let ``preparation phase expires with one original send and durable uncertainty`` () = task {
        let! _=heldReply Preparing false false
        return ()
    }

    [<Fact>]
    let ``caller cancellation keeps original preparation uncertain without resending`` () = task {
        let! _=heldReply Preparing true false
        return ()
    }

    [<Fact>]
    let ``slow valid preparation leaves a distinct bounded schema phase and retained token`` () = task {
        let! _=heldReply WaitingForSchema false false
        return ()
    }

    [<Fact>]
    let ``lost finish reply expires activation while original token and send remain retained`` () = task {
        let! _=heldReply Activating false false
        return ()
    }

    [<Fact>]
    let ``aggregate deadline remains finite across slow preparation and transition checks`` () = task {
        let! _=heldReply Activating false true
        return ()
    }
