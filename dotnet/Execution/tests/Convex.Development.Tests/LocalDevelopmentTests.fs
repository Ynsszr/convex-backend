module Convex.Development.LocalDevelopmentTests

open System
open System.IO
open Convex.DotNet.Development
open Xunit

[<Fact>]
let ``local development parses explicit runtime and loopback ports`` () =
    let selected=LocalDevelopment.options [|"convex.project.json";"--runtime";"/tmp/runtime";"--port";"4001";"--site-port";"4002";"--once"|]
    Assert.Equal(4001,selected.Port)
    Assert.Equal(4002,selected.SitePort)
    Assert.True(selected.Once)
    Assert.Equal("/tmp/runtime",selected.Runtime)

[<Fact>]
let ``local development rejects duplicate options`` () =
    Assert.Throws<ArgumentException>(fun()->LocalDevelopment.options [|"project.json";"--port";"4001";"--port";"4002"|] |> ignore) |> ignore

[<Fact>]
let ``local development rejects shared API and HTTP ports`` () =
    Assert.Throws<ArgumentException>(fun()->LocalDevelopment.options [|"project.json";"--port";"4001";"--site-port";"4001"|] |> ignore) |> ignore

[<Fact>]
let ``local development refuses unknown flags`` () =
    Assert.Throws<ArgumentException>(fun()->LocalDevelopment.options [|"project.json";"--production"|] |> ignore) |> ignore

[<Fact>]
let ``project creation rejects unsafe names before creating state`` () =
    let output=Path.Combine(Path.GetTempPath(),"convex-create-check-"+Guid.NewGuid().ToString("N"))
    Assert.Throws<ArgumentException>(fun()->ReleaseLocation.create "../other" output) |> ignore
    Assert.False(Directory.Exists output)

[<Fact>]
let ``project creation never replaces an existing application`` () =
    let directory=Directory.CreateTempSubdirectory "convex-existing-"
    try
        File.WriteAllText(Path.Combine(directory.FullName,"owned.fs"),"keep")
        Assert.Throws<ArgumentException>(fun()->ReleaseLocation.create "MyApp" directory.FullName) |> ignore
        Assert.Equal("keep",File.ReadAllText(Path.Combine(directory.FullName,"owned.fs")))
    finally directory.Delete(true)

[<Fact>]
let ``new external wildcard inputs trigger another watch iteration`` () =
    let directory=Directory.CreateTempSubdirectory "convex-watch-external-"
    try
        let app=Directory.CreateDirectory(Path.Combine(directory.FullName,"app")).FullName
        let shared=Directory.CreateDirectory(Path.Combine(directory.FullName,"shared")).FullName
        File.WriteAllText(Path.Combine(shared,"First.fs"),"module First\nlet value=1\n")
        File.WriteAllText(Path.Combine(app,"Application.fsproj"),"<Project><ItemGroup><Compile Include=\"../shared/*.fs\" /></ItemGroup></Project>")
        let manifest=Path.Combine(app,"convex.project.json")
        File.WriteAllText(manifest,"{\"version\":1,\"project\":\"Application.fsproj\",\"output\":\"artifacts/convex\",\"modules\":[]}")
        use stop=new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds 8.)
        let mutable builds=0
        ProjectTool.watchWith manifest (fun () ->
            builds<-builds+1
            if builds=1 then File.WriteAllText(Path.Combine(shared,"Second.fs"),"module Second\nlet value=2\n")
            else stop.Cancel()) stop.Token
        Assert.Equal(2,builds)
    finally directory.Delete(true)

[<Fact>]
let ``backend identity uses a private file instead of a secret argument`` () =
    let directory=Directory.CreateTempSubdirectory "convex-secret-file-"
    try
        let secret=String.replicate 64 "a"
        let path=LocalDevelopment.secretFile directory.FullName secret
        Assert.Equal(secret,File.ReadAllText path)
        Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite,File.GetUnixFileMode path)
        let arguments=LocalDevelopment.identityArguments "fsharp-dev-audit" path
        Assert.Contains("--instance-secret-file",arguments)
        Assert.DoesNotContain("--instance-secret",arguments)
        Assert.DoesNotContain(secret,arguments)
        Assert.Throws<IOException>(fun () -> LocalDevelopment.secretFile directory.FullName secret |> ignore) |> ignore
    finally directory.Delete(true)

[<Fact>]
let ``changing ports retains one local deployment history`` () =
    let directory=Directory.CreateTempSubdirectory "convex-port-journal-"
    try
        let state=LocalDevelopment.journalState directory.FullName
        let graphA=String.replicate 64 "a"
        let graphB=String.replicate 64 "b"
        let openJournal port graph =
            new Convex.DotNet.DeploymentJournal.Journal(Uri(sprintf "http://127.0.0.1:%d" port),graph,
                stateDirectory=state,deploymentIdentity="fsharp-dev-audit")
        let record port graph =
            use journal=openJournal port graph
            journal.Write(Convex.DotNet.Deployment.DeploymentPhase.Activating,Convex.DotNet.DeploymentJournal.JournalOutcome.Confirmed)
        record 43110 graphA
        record 43120 graphB
        use journal=openJournal 43110 graphA
        Assert.Equal(Convex.DotNet.DeploymentJournal.Fresh,journal.Prior)
    finally directory.Delete(true)

[<Theory>]
[<InlineData("data.sqlite3")>]
[<InlineData("deployment")>]
[<InlineData("deployment-by-instance")>]
[<InlineData("storage")>]
let ``missing identity cannot replace retained database ownership`` name =
    let directory=Directory.CreateTempSubdirectory "convex-missing-identity-"
    try
        let retained=Path.Combine(directory.FullName,name)
        File.WriteAllText(retained,"retained")
        Assert.Throws<InvalidOperationException>(fun () -> LocalDevelopment.identity directory.FullName |> ignore) |> ignore
        Assert.False(File.Exists(Path.Combine(directory.FullName,"identity.json")))
        Assert.Equal("retained",File.ReadAllText retained)
    finally directory.Delete(true)

[<Theory>]
[<InlineData("sent")>]
[<InlineData("prepared")>]
[<InlineData("uncertain")>]
let ``local journal migration preserves unresolved legacy delivery`` outcome =
    let directory=Directory.CreateTempSubdirectory "convex-legacy-journal-"
    try
        let legacy=Path.Combine(directory.FullName,"deployment")
        let scope=Path.Combine(legacy,String.replicate 64 "a")
        let mode=UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        Directory.CreateDirectory(legacy,mode) |> ignore
        Directory.CreateDirectory(scope,mode) |> ignore
        let state=Path.Combine(scope,"state.json")
        File.WriteAllText(state,System.Text.Json.JsonSerializer.Serialize
            {|version=1;originHash=Path.GetFileName scope;graphSha256=String.replicate 64 "b";outcome=outcome|})
        Assert.Throws<InvalidOperationException>(fun () -> LocalDevelopment.journalState directory.FullName |> ignore) |> ignore
        Assert.True(File.Exists state)
    finally directory.Delete(true)

[<Theory>]
[<InlineData("directory")>]
[<InlineData("fifo")>]
[<InlineData("dangling")>]
let ``local journal migration rejects nonregular legacy entries`` kind =
    let directory=Directory.CreateTempSubdirectory "convex-legacy-entry-"
    try
        let legacy=Path.Combine(directory.FullName,"deployment")
        if kind="dangling" then
            Directory.CreateSymbolicLink(legacy,Path.Combine(directory.FullName,"absent")) |> ignore
        else
            let mode=UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
            Directory.CreateDirectory(legacy,mode) |> ignore
            let scope=Directory.CreateDirectory(Path.Combine(legacy,String.replicate 64 "a"),mode).FullName
            let state=Path.Combine(scope,"state.json")
            if kind="directory" then Directory.CreateDirectory(state,mode) |> ignore
            else
                let start=System.Diagnostics.ProcessStartInfo("mkfifo",UseShellExecute=false)
                start.ArgumentList.Add state
                use child=System.Diagnostics.Process.Start start
                child.WaitForExit()
                Assert.Equal(0,child.ExitCode)
        Assert.Throws<InvalidOperationException>(fun () -> LocalDevelopment.journalState directory.FullName |> ignore) |> ignore
    finally directory.Delete(true)

[<Fact>]
let ``source edits made during a build trigger another watch iteration`` () =
    let directory=Directory.CreateTempSubdirectory "convex-watch-overlap-"
    try
        let source=Path.Combine(directory.FullName,"Source.fs")
        let project=Path.Combine(directory.FullName,"Application.fsproj")
        let manifest=Path.Combine(directory.FullName,"convex.project.json")
        File.WriteAllText(source,"module Source\nlet value = 1\n")
        // Evaluated MSBuild items without restoring or building any dependency.
        File.WriteAllText(project,"<Project><ItemGroup><Compile Include=\"Source.fs\" /></ItemGroup></Project>")
        File.WriteAllText(manifest,"{\"version\":1,\"project\":\"Application.fsproj\",\"output\":\"artifacts/convex\",\"modules\":[]}")
        use stop=new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds 5.)
        let builds=ResizeArray<string>()
        ProjectTool.watchWith manifest (fun () ->
            builds.Add(File.ReadAllText source)
            if builds.Count=1 then
                // Simulate an edit after the compiler captured the earlier input.
                File.WriteAllText(source,"module Source\nlet value = 222\n")
            else stop.Cancel()) stop.Token
        Assert.Equal(2,builds.Count)
        Assert.Contains("222",builds[1])
    finally directory.Delete(true)
