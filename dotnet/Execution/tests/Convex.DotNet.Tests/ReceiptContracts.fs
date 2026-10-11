namespace Convex.DotNet.Tests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open Xunit
open Convex.DotNet.Deployment
open Convex.DotNet.DeploymentJournal

module ReceiptContracts =
    let private expected={OperationId=String.replicate 32 "a";StartPushSha256=String.replicate 64 "b"}
    let private receipt digest timestamp =
        "{\"operationId\":\""+expected.OperationId+"\",\"startPushSha256\":\""+digest+
        "\",\"commitTimestamp\":"+timestamp+",\"outcome\":\"committed\"}"
    let private read (reply:string) = task {
        use port=new TcpListener(IPAddress.Loopback,0)
        port.Start()
        let address=port.LocalEndpoint :?> IPEndPoint
        port.Stop()
        use listener=new HttpListener()
        let origin=Uri("http://127.0.0.1:"+string address.Port+"/")
        listener.Prefixes.Add(origin.AbsoluteUri)
        listener.Start()
        let mutable count=0
        let owner=task {
            let! request=listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds 10.)
            count<-count+1
            Assert.Equal("/api/deploy2/native_receipt",request.Request.Url.AbsolutePath)
            use reader=new StreamReader(request.Request.InputStream,Encoding.UTF8)
            let! body=reader.ReadToEndAsync()
            use payload=JsonDocument.Parse body
            Assert.Equal("receipt-test-admin",payload.RootElement.GetProperty("adminKey").GetString())
            Assert.Equal(expected.OperationId,payload.RootElement.GetProperty("operationId").GetString())
            let bytes=Encoding.UTF8.GetBytes reply
            request.Response.StatusCode<-200
            request.Response.ContentLength64<-int64 bytes.Length
            do! request.Response.OutputStream.WriteAsync(ReadOnlyMemory<byte>(bytes)).AsTask()
            request.Response.Close()
        }
        let! outcome=NativeDeployment.readReceipt origin "receipt-test-admin" expected CancellationToken.None
        do! owner
        Assert.Equal(1,count)
        return outcome
    }

    [<Fact>]
    let ``matching full snapshot receipt preserves exact historical timestamp without an activation request`` () = task {
        let! outcome=read(receipt expected.StartPushSha256 "\"18446744073709551615\"")
        match outcome with
        | HistoricalCommit value ->
            Assert.Equal(expected,value.Token)
            Assert.Equal("18446744073709551615",value.CommitTimestamp)
        | UnresolvedReceipt -> failwith "Exact owning historical receipt should settle"
    }

    [<Theory>]
    [<InlineData("null")>]
    [<InlineData("{}")>]
    [<InlineData("owning-reply-was-lost")>]
    let ``absent or unreadable receipts retain uncertainty with one read`` reply = task {
        let! outcome=read reply
        Assert.Equal(UnresolvedReceipt,outcome)
    }

    [<Fact>]
    let ``wrong full snapshot digest and lossy numeric timestamp cannot settle`` () = task {
        let! mismatch=read(receipt (String.replicate 64 "c") "\"123\"")
        let! numeric=read(receipt expected.StartPushSha256 "18446744073709551615")
        Assert.Equal(UnresolvedReceipt,mismatch)
        Assert.Equal(UnresolvedReceipt,numeric)
    }

    [<Fact>]
    let ``journal fences every later graph until exact historical owning confirmation`` () =
        let root=Path.Combine(Path.GetTempPath(),"convex-native-receipt-"+Guid.NewGuid().ToString("N"))
        let origin=Uri "https://receipt-journal.invalid/"
        let graph=String.replicate 64 "d"
        let later=String.replicate 64 "e"
        try
            let token =
                use journal=new Journal(origin,graph,stateDirectory=root)
                journal.Write(Preparing,JournalOutcome.Sent)
                let value={OperationId=journal.OperationId;StartPushSha256=expected.StartPushSha256}
                journal.RecordPrepared value
                journal.Write(Activating,JournalOutcome.Sent)
                Assert.Throws<IOException>(fun () -> use duplicate=new Journal(origin,later,stateDirectory=root) in ()) |> ignore
                value
            do
                use unresolved=new Journal(origin,later,stateDirectory=root)
                Assert.Equal(PriorOutcome.Uncertain,unresolved.Prior)
                Assert.Equal(Some token,unresolved.PendingToken)
                Assert.Throws<InvalidOperationException>(fun () -> unresolved.ConfirmHistorical {Token={token with StartPushSha256=String.replicate 64 "f"};CommitTimestamp="123"}) |> ignore
                unresolved.ConfirmHistorical {Token=token;CommitTimestamp="123"}
            use settled=new Journal(origin,later,stateDirectory=root)
            Assert.Equal(Fresh,settled.Prior)
            Assert.Equal(Some "confirmed",settled.HistoricalOutcome)
            Assert.Equal(Some graph,settled.RecordedGraphSha256)
        finally if Directory.Exists root then Directory.Delete(root,true)

    [<Fact>]
    let ``old pending journal entries without owning tokens remain fenced`` () =
        let root=Path.Combine(Path.GetTempPath(),"convex-native-legacy-"+Guid.NewGuid().ToString("N"))
        let origin=Uri "https://receipt-journal.invalid/"
        let graph=String.replicate 64 "d"
        try
            do
                use journal=new Journal(origin,graph,stateDirectory=root)
                journal.Write(Activating,JournalOutcome.Sent)
            let state=Directory.GetFiles(root,"state.json",SearchOption.AllDirectories) |> Array.exactlyOne
            use parsed=JsonDocument.Parse(File.ReadAllBytes state)
            let legacy=parsed.RootElement.EnumerateObject() |> Seq.filter(fun field -> field.Name<>"operationId" && field.Name<>"startPushSha256" && field.Name<>"commitTimestamp")
                       |> Seq.map(fun field -> field.Name,box (field.Value.Clone())) |> dict
            File.WriteAllBytes(state,JsonSerializer.SerializeToUtf8Bytes legacy)
            use journal=new Journal(origin,String.replicate 64 "e",stateDirectory=root)
            Assert.Equal(PriorOutcome.Uncertain,journal.Prior)
            Assert.Equal(None,journal.PendingToken)
        finally if Directory.Exists root then Directory.Delete(root,true)

    [<Theory>]
    [<InlineData("state.json","dangling-link")>]
    [<InlineData("delivery.lock","dangling-link")>]
    [<InlineData("state.json","directory")>]
    [<InlineData("delivery.lock","directory")>]
    let ``journal refuses nonregular state and lock entries before admitting a new attempt`` name kind =
        let root=Path.Combine(Path.GetTempPath(),"convex-native-entry-"+Guid.NewGuid().ToString("N"))
        let origin=Uri "https://receipt-journal.invalid/"
        let graph=String.replicate 64 "d"
        let mutable entry=None
        try
            // A genuinely missing state remains admissible before constructing
            // either invalid entry in this private, disposable journal scope.
            do
                use fresh=new Journal(origin,graph,stateDirectory=root)
                Assert.Equal(Fresh,fresh.Prior)
            let directory=Directory.GetDirectories(root) |> Array.exactlyOne
            let selected=Path.Combine(directory,name)
            if File.Exists selected then File.Delete selected
            entry<-Some selected
            let target=Path.Combine(root,"never-created-target")
            if kind="dangling-link" then File.CreateSymbolicLink(selected,target) |> ignore
            else Directory.CreateDirectory selected |> ignore
            Assert.Throws<InvalidOperationException>(fun () -> use rejected=new Journal(origin,graph,stateDirectory=root) in ()) |> ignore
            Assert.False(File.Exists target || Directory.Exists target)
            if kind="dangling-link" then Assert.Equal(target,FileInfo(selected).LinkTarget)
            else Assert.True(Directory.Exists selected)
        finally
            // Remove the known fixture link itself; never follow its target.
            if kind="dangling-link" then entry |> Option.iter(fun selected -> if FileInfo(selected).LinkTarget<>null then File.Delete selected)
            if Directory.Exists root then Directory.Delete(root,true)
