namespace Convex.DotNet.Tests

open System
open System.IO
open System.Reflection
open System.Diagnostics
open System.Threading.Tasks
open Xunit
open Convex.Runtime
open Convex.DotNet.Execution

// Optimized capture regressions use the actual deployment admission owner.
module CaptureStructureContracts =
    let private identity name = "RuntimeFixture.NativeReflectionStructureFixtures:" + name
    let private deployment () =
        let fixture = typeof<RuntimeFixture.NativeReflectionStructureFixtures>.Assembly
        let compiled = fixture.GetCustomAttribute<DebuggableAttribute>()
        Assert.True(isNull compiled || not compiled.IsJITOptimizerDisabled, "Capture fixture must be optimized.")
        let path = fixture.Location
        let sha = Security.Cryptography.SHA256.HashData(File.ReadAllBytes path) |> Convert.ToHexStringLower
        new DeploymentAssembly(path, sha, [||], "describe", frontendAssemblies = [| typeof<Convex.FSharp.QueryCtx>.Assembly |])
    let private invokePure expected selected =
        let frontend = Convex.FSharp.NativeExecution.FSharpExecutionFrontend() :> IExecutionFrontend
        let unexpected name = failwith ("Unexpected capability: " + name)
        let host =
            { new IInvocationHost with
                member _.InvocationId = "reflection-structure-proof"
                member _.Call(name, _) = unexpected name
                member _.CallAsync(name, _, _) = Task.FromResult(unexpected name) }
        use scope = frontend.Enter host
        match selected with
        | NativeExport.Function definition ->
            let actual = definition.Invoke(host, RuntimeJson.parse "{}", RuntimeJson.nullValue).GetAwaiter().GetResult()
            Assert.Equal(expected, actual.GetInt32())
        | _ -> failwith "Expected query"
    let private rejectsClock operation =
        let error = Assert.Throws<HostOperationException>(Action operation)
        Assert.Contains("System.DateTime.get_UtcNow", error.Message)

    [<Fact>]
    let ``warm exact static structure still reads changed callback in same closed owner`` () =
        use selected = deployment()
        selected.Resolve(identity "warmStatic") |> invokePure 7
        selected.Resolve(identity "independentString") |> invokePure 11
        rejectsClock(fun () -> selected.Resolve(identity "mutateSameStatic") |> ignore)

    [<Fact>]
    let ``warm instance structure still walks current fields in a new instance of same closed type`` () =
        use selected = deployment()
        selected.Resolve(identity "warmInstance") |> invokePure 13
        rejectsClock(fun () -> selected.Resolve(identity "mutateSameInstanceType") |> ignore)

    [<Fact>]
    let ``bad captured prefix refuses before later getter can repair shared static`` () =
        use scope = Convex.FSharp.TableRegistry.enterScope()
        let marker = typeof<System.Collections.Generic.KeyValuePair<Guid, decimal>>
        Assert.Equal(None, Convex.FSharp.TableRegistry.tryGetName marker)
        let names = typeof<RuntimeFixture.NativeReflectionStructureFixtures>.GetProperties(BindingFlags.Public ||| BindingFlags.Static)
                    |> Array.filter(fun p -> p.Name="badFirst" || p.Name="repairLater") |> Array.map(fun p -> p.Name)
        Assert.Equal<string>([|"badFirst"; "repairLater"|], names)
        use selected = deployment()
        rejectsClock(fun () -> selected.Declarations(fun name _ -> name=identity "badFirst" || name=identity "repairLater") |> ignore)
        Assert.Equal(None, Convex.FSharp.TableRegistry.tryGetName marker)

    [<Fact>]
    let ``sharing static field structure across action and deterministic profiles shares no admission`` () =
        use selected = deployment()
        selected.Resolve(identity "warmGuid") |> invokePure 19
        // Resolve admits the action closure; do not execute its clock callback.
        selected.Resolve(identity "actionClock") |> ignore
        rejectsClock(fun () -> selected.Resolve(identity "deterministicAfterAction") |> ignore)

    [<Fact>]
    let ``fresh deployment context cannot retain previous mutable callback or structure authority`` () =
        do
            use selected = deployment()
            selected.Resolve(identity "warmStatic") |> invokePure 7
            rejectsClock(fun () -> selected.Resolve(identity "mutateSameStatic") |> ignore)
        use fresh = deployment()
        fresh.Resolve(identity "warmStatic") |> invokePure 7
        fresh.Resolve(identity "independentString") |> invokePure 11
