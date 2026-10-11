module Convex.FSharp.Tests.ClientCatalogTests

open Convex.FSharp
open Convex.Runtime
open Xunit

let private fn address visibility =
    let reference=FunctionReference.publicQuery address (Contract.arguments<unit>()) (Contract.derived<int>())
    {Address=address;Kind=FunctionKind.Query;Visibility=visibility;Contract=reference :> ICallableContract}
let private child exports = NativeComponentDefinitions.child "child" [] [] [] exports
let private root = NativeComponentDefinitions.app [] [NativeComponentDefinitions.mount "child" "../child"] NativeComponentDefinitions.emptyExports None
let private generate definition functions = NativeClientCatalog.render "Generated" Set.empty [] root [|{DefinitionPath="../child";Declaration=definition;Functions=functions}|] [||]

[<Fact>]
let ``empty component exports infer the public file based API`` () =
    let text=generate (child NativeComponentDefinitions.emptyExports) [|fn "counter:read" FunctionVisibility.Public;fn "hidden:secret" FunctionVisibility.Internal|]
    Assert.Contains("module ``counter``",text)
    Assert.Contains("[\"counter\"; \"read\"]",text)
    Assert.DoesNotContain("secret",text)

[<Fact>]
let ``explicit aliases do not add file based fallback exports`` () =
    let declaration=child(NativeComponentExport.Branch ["value",NativeComponentExport.Leaf(NativeComponentReference.Function "counter:read")])
    let text=generate declaration [|fn "counter:read" FunctionVisibility.Public|]
    Assert.Contains("let ``value``",text)
    Assert.DoesNotContain("module ``counter``",text)

[<Fact>]
let ``explicit exports cannot expose internal registered functions`` () =
    let declaration=child(NativeComponentExport.Branch ["value",NativeComponentExport.Leaf(NativeComponentReference.Function "counter:secret")])
    Assert.Throws<System.InvalidOperationException>(fun () -> generate declaration [|fn "counter:secret" FunctionVisibility.Internal|] |> ignore) |> ignore

[<Fact>]
let ``component references in parent clients are internal`` () =
    let text=generate (child NativeComponentDefinitions.emptyExports) [|fn "counter:read" FunctionVisibility.Public|]
    Assert.Contains("FunctionReference.internalQuery",text)
    Assert.DoesNotContain("FunctionReference.publicQuery",text)
