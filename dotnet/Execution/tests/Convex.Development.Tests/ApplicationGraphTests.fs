module Convex.Development.ApplicationGraphTests
open System
open System.IO
open System.Text.Json
open Xunit
open Convex.DotNet.Development
let private withGraph run =
    let directory=Directory.CreateTempSubdirectory("convex-graph-")
    let write name components =
        let path=Path.Combine(directory.FullName,name+".json")
        File.WriteAllText(path,JsonSerializer.Serialize {|version=1;modules=Array.empty<obj>;components=components|})
        path
    try run write directory.FullName
    finally directory.Delete true

[<Fact>]
let ``component cycles are refused before builds`` () =
    withGraph(fun write _ ->
        let first=write "a" [|{|definitionPath="../b";manifest="b.json"|}|]
        write "b" [|{|definitionPath="../a";manifest="a.json"|}|] |> ignore
        Assert.Throws<InvalidOperationException>(fun () -> ApplicationGraph.read first |> ignore) |> ignore)

[<Fact>]
let ``shared component definitions are deduplicated`` () =
    withGraph(fun write _ ->
        write "shared" [||] |> ignore
        write "child" [|{|definitionPath="../shared";manifest="shared.json"|}|] |> ignore
        let root=write "root" [|{|definitionPath="../shared";manifest="shared.json"|};{|definitionPath="../child";manifest="child.json"|}|]
        let graph=ApplicationGraph.read root
        Assert.Equal(2,graph.Components.Length))

[<Fact>]
let ``one deployment definition cannot select conflicting manifests`` () =
    withGraph(fun write _ ->
        write "one" [||] |> ignore
        write "two" [|{|definitionPath="../same";manifest="one.json"|}|] |> ignore
        let root=write "root" [|{|definitionPath="../same";manifest="two.json"|}|]
        Assert.Throws<InvalidOperationException>(fun () -> ApplicationGraph.read root |> ignore) |> ignore)

[<Fact>]
let ``duplicate direct component dependencies are rejected`` () =
    withGraph(fun write _ ->
        write "child" [||] |> ignore
        let root=write "root" [|{|definitionPath="../child";manifest="child.json"|};{|definitionPath="../child";manifest="child.json"|}|]
        Assert.Throws<InvalidOperationException>(fun () -> ApplicationGraph.read root |> ignore) |> ignore)

[<Fact>]
let ``one source manifest cannot receive multiple deployment identities`` () =
    withGraph(fun write _ ->
        write "child" [||] |> ignore
        let root=write "root" [|{|definitionPath="../one";manifest="child.json"|};{|definitionPath="../two";manifest="child.json"|}|]
        Assert.Throws<InvalidOperationException>(fun () -> ApplicationGraph.read root |> ignore) |> ignore)
