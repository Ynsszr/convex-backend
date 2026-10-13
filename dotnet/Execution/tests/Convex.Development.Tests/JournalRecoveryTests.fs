module Convex.Development.JournalRecoveryTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Convex.DotNet.Deployment
open Convex.DotNet.DeploymentJournal
open Convex.DotNet.Development
open Xunit

let private command stateRoot origin identity =
    let cli=typeof<LocalDevelopment.Options>.Assembly.Location
    let info=ProcessStartInfo("dotnet",UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true)
    for value in [cli;"reconcile";origin;"CONVEX_RECOVERY_TEST_ADMIN"] do info.ArgumentList.Add value
    identity |> Option.iter(fun value -> info.ArgumentList.Add "--deployment-identity";info.ArgumentList.Add value)
    info.Environment["CONVEX_DOTNET_STATE_DIRECTORY"]<-stateRoot
    info.Environment["CONVEX_RECOVERY_TEST_ADMIN"]<-"synthetic-recovery-admin"
    info

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``CLI receipt reconciliation settles the selected journal without an activation`` local = task {
    let directory=Directory.CreateTempSubdirectory "convex-reconcile-scope-"
    try
        let stateRoot=Path.Combine(directory.FullName,"history")
        let identity=if local then Some "fsharp-dev-recovery" else None
        let graph=String.replicate 64 "d"
        use port=new TcpListener(IPAddress.Loopback,0)
        port.Start()
        let address=port.LocalEndpoint :?> IPEndPoint
        port.Stop()
        use listener=new HttpListener()
        let origin=Uri(sprintf "http://127.0.0.1:%d/" address.Port)
        listener.Prefixes.Add origin.AbsoluteUri
        listener.Start()
        // A local backend may have moved ports since the original dispatch.
        let original=if local then Uri "http://127.0.0.1:1/" else origin
        let token =
            use journal=new Journal(original,graph,stateDirectory=stateRoot,?deploymentIdentity=identity)
            journal.Write(Preparing,JournalOutcome.Sent)
            let token={OperationId=journal.OperationId;StartPushSha256=String.replicate 64 "e"}
            journal.RecordPrepared token
            journal.Write(Activating,JournalOutcome.Uncertain)
            token
        let owner=task {
            let! request=listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds 15.)
            Assert.Equal("/api/deploy2/native_receipt",request.Request.Url.AbsolutePath)
            Assert.Equal("POST",request.Request.HttpMethod)
            use reader=new StreamReader(request.Request.InputStream)
            let! contents=reader.ReadToEndAsync()
            use body=JsonDocument.Parse contents
            Assert.Equal(token.OperationId,body.RootElement.GetProperty("operationId").GetString())
            Assert.Equal("synthetic-recovery-admin",body.RootElement.GetProperty("adminKey").GetString())
            let bytes=JsonSerializer.SerializeToUtf8Bytes {|operationId=token.OperationId;startPushSha256=token.StartPushSha256;
                                                          commitTimestamp="18446744073709551615";outcome="committed"|}
            request.Response.StatusCode<-200
            request.Response.ContentLength64<-int64 bytes.Length
            do! request.Response.OutputStream.WriteAsync(ReadOnlyMemory<byte>(bytes)).AsTask()
            request.Response.Close()
        }
        use child=Process.Start(command stateRoot origin.AbsoluteUri identity)
        let output=child.StandardOutput.ReadToEndAsync()
        let errors=child.StandardError.ReadToEndAsync()
        try
            do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 20.)
            let! stdout=output
            let! stderr=errors
            Assert.True(child.ExitCode=0,stderr+stdout)
            do! owner
            use result=JsonDocument.Parse stdout
            Assert.Equal("confirmed",result.RootElement.GetProperty("outcome").GetString())
            Assert.False(result.RootElement.GetProperty("sent").GetBoolean())
            Assert.Equal("18446744073709551615",result.RootElement.GetProperty("commitTimestamp").GetString())
            use resumed=new Journal(origin,graph,stateDirectory=stateRoot,?deploymentIdentity=identity)
            Assert.Equal(Recorded,resumed.Prior)
            Assert.Equal(Some "confirmed",resumed.HistoricalOutcome)
            if local then
                use unrelated=new Journal(origin,graph,stateDirectory=stateRoot)
                Assert.Equal(Fresh,unrelated.Prior)
        finally
            if not child.HasExited then child.Kill(true);child.WaitForExit()
    finally directory.Delete(true)
}

[<Fact>]
let ``invalid explicit deployment identity is refused before journal creation or network`` () = task {
    let directory=Directory.CreateTempSubdirectory "convex-reconcile-invalid-"
    try
        let stateRoot=Path.Combine(directory.FullName,"unused-history")
        use child=Process.Start(command stateRoot "http://127.0.0.1:1/" (Some "../different-instance"))
        let output=child.StandardOutput.ReadToEndAsync()
        let errors=child.StandardError.ReadToEndAsync()
        try
            do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 10.)
            let! stdout=output
            let! _=errors
            Assert.Equal(1,child.ExitCode)
            Assert.Equal("",stdout)
            Assert.False(Directory.Exists stateRoot)
        finally
            if not child.HasExited then child.Kill(true);child.WaitForExit()
    finally directory.Delete(true)
}
