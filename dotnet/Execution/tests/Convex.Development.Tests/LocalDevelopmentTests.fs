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
