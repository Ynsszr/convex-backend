module Convex.Development.Tests

open System
open System.IO
open Xunit
open Convex.DotNet.Development

let private withFile (contents:string) action =
    let directory=Path.Combine(Path.GetTempPath(),"convex-cli-tests-"+Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory directory |> ignore
    let path=Path.Combine(directory,"convex.project.json")
    File.WriteAllText(path,contents)
    try action path
    finally Directory.Delete(directory,true)

[<Fact>]
let ``unknown authentication config is not silently ignored`` () =
    withFile """{"version":1,"authentication":"typo","modules":[]}""" (fun path ->
        let error=Assert.Throws<InvalidOperationException>(fun () -> ProjectTool.package path)
        Assert.Contains("Unknown project field: authentication",error.Message))

[<Fact>]
let ``duplicate project options are refused before any build`` () =
    withFile """{"version":1,"output":"first","output":"second","modules":[]}""" (fun path ->
        let error=Assert.Throws<InvalidOperationException>(fun () -> ProjectTool.package path)
        Assert.Contains("Duplicate project field",error.Message))

[<Fact>]
let ``unknown module configuration is refused`` () =
    withFile """{"version":1,"modules":[{"path":"messages","type":"Messages","aliasess":{}}]}""" (fun path ->
        let error=Assert.Throws<InvalidOperationException>(fun () -> ProjectTool.package path)
        Assert.Contains("Unknown module field",error.Message))

[<Fact>]
let ``unsupported manifest versions are refused`` () =
    withFile """{"version":2,"modules":[]}""" (fun path ->
        let error=Assert.Throws<InvalidOperationException>(fun () -> ProjectTool.package path)
        Assert.Contains("Unsupported",error.Message))

[<Fact>]
let ``unbounded manifests are refused`` () =
    withFile (String(' ',65537)) (fun path ->
        Assert.Throws<ArgumentException>(fun () -> ProjectTool.package path) |> ignore)

[<Fact>]
let ``runtime artifacts require a file inventory`` () =
    withFile "{}" (fun path ->
        let root=Path.GetDirectoryName path
        File.WriteAllText(Path.Combine(root,"runtime.json"),"""{"format":"convex-native-runtime"}""")
        let error=Assert.Throws<InvalidOperationException>(fun () -> ProjectTool.runtimePrepare root (Path.Combine(root,"output")))
        Assert.Contains("file digest inventory",error.Message)
        Assert.False(Directory.Exists(Path.Combine(root,"output"))))

[<Fact>]
let ``module names reject hyphens before starting a build`` () =
    withFile "{\"version\":1,\"modules\":[{\"path\":\"http-handlers\",\"type\":\"Example.Http\"}]}" (fun file ->
        let error=Assert.Throws<InvalidOperationException>(fun () -> ProjectTool.build file)
        Assert.Contains("Invalid durable module path",error.Message))

[<Fact>]
let ``CLR namespaces require explicit portable module addresses`` () =
    withFile "{\"version\":1,\"modules\":[{\"path\":\"Example.Messages\",\"type\":\"Example.Messages\"}]}" (fun file ->
        let error=Assert.Throws<InvalidOperationException>(fun () -> ProjectTool.build file)
        Assert.Contains("Invalid durable module path",error.Message))

[<EntryPoint>]
let main _ = 0
