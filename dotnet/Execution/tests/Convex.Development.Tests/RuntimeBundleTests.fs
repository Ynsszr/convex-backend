module Convex.Development.RuntimeBundleTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open Convex.DotNet.Execution
open Convex.DotNet.Development

let private required = [|
    "backend/convex-local-backend"; "host/Convex.DotNet.Host.dll"; "host/Convex.Runtime.dll"
    "host/Convex.DotNet.dll"; "host/Convex.FSharp.dll"; "host/FSharp.Core.dll"
    "host/Convex.DotNet.Host.deps.json"; "host/Convex.DotNet.Host.runtimeconfig.json"
    "cli/Convex.DotNet.Cli.dll"; "cli/Convex.DotNet.Cli.deps.json"; "cli/Convex.DotNet.Cli.runtimeconfig.json"
|]

let private manifest root =
    let files = required |> Array.map (fun relative ->
        {| path = relative; sha256 = SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relative))) |> Convert.ToHexStringLower |})
    JsonSerializer.SerializeToNode({|
        format = "convex-native-runtime"; version = "0.2.0-test"; rid = "linux-x64"
        backend = "backend/convex-local-backend"; host = "host/Convex.DotNet.Host.dll"
        cli = "cli/Convex.DotNet.Cli.dll"; files = files
    |}).AsObject()

let private save root (value: JsonObject) = File.WriteAllText(Path.Combine(root, "runtime.json"), value.ToJsonString())

// Synthetic bytes only test the artifact boundary. No backend or worker is executed.
let private withBundle action =
    let root = Path.Combine(Path.GetTempPath(), "convex-runtime-bundle-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        for relative in required do
            let path = Path.Combine(root, relative)
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, "synthetic artifact")
        let elf = Array.zeroCreate<byte> 64
        Array.blit [|0x7fuy; 0x45uy; 0x4cuy; 0x46uy; 2uy; 1uy|] 0 elf 0 6
        elf[16] <- 3uy
        elf[18] <- 62uy
        File.WriteAllBytes(Path.Combine(root, required[0]), elf)
        let descriptor = manifest root
        save root descriptor
        action root descriptor
    finally Directory.Delete(root, true)

let private refused root message =
    let error = Assert.Throws<InvalidOperationException>(fun () -> RuntimeBundle.read root |> ignore)
    Assert.Contains(message, error.Message)

[<Fact>]
let ``complete inventory exposes checked entry points without creating state`` () =
    withBundle (fun root _ ->
        let value = RuntimeBundle.read root
        Assert.Equal("linux-x64", RuntimeBundle.runtimeIdentifier value)
        Assert.Equal(Path.Combine(root, required[0]), RuntimeBundle.backendPath value)
        Assert.False(Directory.Exists(Path.Combine(root, "runtime-selections"))))

[<Fact>]
let ``empty digest inventory cannot mark a runtime complete`` () =
    withBundle (fun root descriptor ->
        descriptor["files"] <- JsonArray()
        save root descriptor
        refused root "1..1024")

[<Fact>]
let ``backend cannot escape the inventory through its entry point`` () =
    withBundle (fun root descriptor ->
        descriptor["backend"] <- JsonValue.Create("../other-backend")
        save root descriptor
        refused root "contract paths")

[<Fact>]
let ``every required binary must be inventoried`` () =
    withBundle (fun root descriptor ->
        descriptor["files"].AsArray().RemoveAt(0)
        save root descriptor
        refused root "every required entry point")

[<Fact>]
let ``unlisted host files are rejected`` () =
    withBundle (fun root _ ->
        File.WriteAllText(Path.Combine(root, "host/Unexpected.dll"), "extra")
        refused root "complete distribution")

[<Fact>]
let ``tampered artifact is rejected`` () =
    withBundle (fun root _ ->
        File.AppendAllText(Path.Combine(root, "host/Convex.Runtime.dll"), "changed")
        refused root "digest mismatch")

[<Fact>]
let ``duplicate inventory entries are rejected`` () =
    withBundle (fun root descriptor ->
        let entries = descriptor["files"].AsArray()
        entries.Add(entries[0].DeepClone())
        save root descriptor
        refused root "Duplicate runtime inventory path")

[<Fact>]
let ``duplicate descriptor fields are rejected`` () =
    withBundle (fun root descriptor ->
        let contents = descriptor.ToJsonString()
        File.WriteAllText(Path.Combine(root, "runtime.json"), contents.Insert(1, "\"rid\":\"linux-x64\","))
        refused root "Duplicate runtime descriptor field")

[<Fact>]
let ``unfinished publication creates no runtime run directory`` () =
    withBundle (fun root _ ->
        File.WriteAllText(Path.Combine(root, ".incomplete"), "pending")
        let output = Path.Combine(root, "must-not-exist")
        let error = Assert.Throws<InvalidOperationException>(fun () -> ProjectTool.runtimeRun root output [||] |> ignore)
        Assert.Contains("incomplete", error.Message)
        Assert.False(Directory.Exists output))

[<Fact>]
let ``inventory cannot traverse symlinked files`` () =
    withBundle (fun root _ ->
        let original = Path.Combine(root, "host/Convex.Runtime.dll")
        let saved = Path.Combine(root, "host/source.dll")
        File.Move(original, saved)
        File.CreateSymbolicLink(original, saved) |> ignore
        refused root "symbolic links")

[<Fact>]
let ``hash-consistent wrong architecture is rejected`` () =
    withBundle (fun root _ ->
        let file = Path.Combine(root, required[0])
        let bytes = File.ReadAllBytes file
        bytes[18] <- 183uy
        File.WriteAllBytes(file, bytes)
        save root (manifest root)
        refused root "architecture")

[<Fact>]
let ``hash-consistent non executable backend is rejected`` () =
    withBundle (fun root _ ->
        File.WriteAllText(Path.Combine(root, required[0]), String('x', 64))
        save root (manifest root)
        refused root "ELF backend")

[<Fact>]
let ``managed-only artifact is not a complete distribution`` () =
    withBundle (fun root descriptor ->
        descriptor["format"] <- JsonValue.Create("convex-native-managed-runtime")
        save root descriptor
        refused root "complete native")
