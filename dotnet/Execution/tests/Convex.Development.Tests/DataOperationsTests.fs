module Convex.Development.DataOperationsTests

open System
open System.IO
open Xunit
open Convex.DotNet.Development

let private withFile action =
    let directory=Directory.CreateTempSubdirectory("convex-data-tests-")
    let path=Path.Combine(directory.FullName,"input.json")
    File.WriteAllText(path,"[]")
    try action directory.FullName path
    finally Directory.Delete(directory.FullName,true)

[<Fact>]
let ``export never overwrites an existing file`` () =
    withFile(fun _ path ->
        Assert.Throws<ArgumentException>(fun () -> DataOperations.export "http://127.0.0.1:1" path "UNSET" false |> ignore) |> ignore
        Assert.Equal("[]",File.ReadAllText path))

[<Theory>]
[<InlineData("guess")>]
[<InlineData("delete")>]
[<InlineData("")>]
let ``import requires explicit supported mode before reading credentials`` mode =
    withFile(fun _ path ->
        Assert.Throws<ArgumentException>(fun () -> DataOperations.importFile "http://127.0.0.1:1" "jsonArray" "rows" path mode "UNSET" |> ignore) |> ignore)

[<Fact>]
let ``zip import must not guess a target table`` () =
    withFile(fun _ path ->
        Assert.Throws<ArgumentException>(fun () -> DataOperations.importFile "http://127.0.0.1:1" "zip" "rows" path "requireEmpty" "UNSET" |> ignore) |> ignore)

[<Fact>]
let ``import refuses symbolic links`` () =
    withFile(fun root path ->
        let link=Path.Combine(root,"link.json")
        File.CreateSymbolicLink(link,path) |> ignore
        Assert.Throws<ArgumentException>(fun () -> DataOperations.importFile "http://127.0.0.1:1" "jsonArray" "rows" link "requireEmpty" "UNSET" |> ignore) |> ignore)

[<Fact>]
let ``backup refuses unencrypted remote origins`` () =
    withFile(fun root _ ->
        Assert.Throws<ArgumentException>(fun () -> DataOperations.export "http://example.com" (Path.Combine(root,"export.zip")) "UNSET" false |> ignore) |> ignore)
