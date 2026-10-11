namespace Convex.DotNet.Tests

open System
open System.Reflection
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Convex.Runtime
open Xunit

/// Exercise the executable's real guard without exposing a test-only public ABI.
module HostPreparationContracts =
    let private deadline = TimeSpan.FromSeconds 5.
    let private flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic
    let private capability =
        Assembly.Load("Convex.DotNet.Host").GetTypes()
        |> Array.find (fun kind -> kind.Name = "PreparationCapability")

    type private Owner() =
        let mutable calls = 0
        let value = JsonSerializer.SerializeToElement(true)
        member _.Calls = Volatile.Read(&calls)
        interface IInvocationHost with
            member _.InvocationId = "host-preparation-contract"
            member _.Call(_, _) =
                Interlocked.Increment(&calls) |> ignore
                value
            member _.CallAsync(_, _, _) =
                Interlocked.Increment(&calls) |> ignore
                Task.FromResult(value)

    let private create () =
        let owner = Owner()
        let instance = Activator.CreateInstance(capability, flags, null, [| box owner |], null)
        instance, (instance :?> IInvocationHost), owner

    let private prepared instance (writer: unit -> Task<unit>) =
        capability.GetMethod("Prepared", flags).Invoke(instance, [| box writer |]) :?> Task

    let private isOpen instance = capability.GetProperty("IsOpen", flags).GetValue(instance) :?> bool
    let private arguments = JsonSerializer.SerializeToElement(true)

    let private assertPoisoned instance (owner: Owner) = task {
        let mutable frames = 0
        let! failure = Assert.ThrowsAsync<InvalidOperationException>(fun () ->
            prepared instance (fun () -> task { frames <- frames + 1 }) |> fun running -> running.WaitAsync(deadline))
        Assert.Contains("attempted an invocation host operation", failure.Message)
        Assert.Equal(0, frames)
        Assert.Equal(0, owner.Calls)
        Assert.False(isOpen instance)
    }

    [<Fact>]
    let ``synchronous preflight refusal poisons preparation before readiness or delegation`` () = task {
        let instance, host, owner = create ()
        let failure = Assert.Throws<InvalidOperationException>(fun () -> host.Call("offline/forbidden", arguments) |> ignore)
        Assert.Contains("refuses invocation host operations", failure.Message)
        do! assertPoisoned instance owner
    }

    [<Fact>]
    let ``asynchronous preflight refusal poisons preparation before readiness or delegation`` () = task {
        let instance, host, owner = create ()
        let refused = host.CallAsync("offline/forbidden", arguments, true)
        let! failure = Assert.ThrowsAsync<InvalidOperationException>(fun () -> refused.WaitAsync(deadline) :> Task)
        Assert.Contains("refuses invocation host operations", failure.Message)
        do! assertPoisoned instance owner
    }

    [<Fact>]
    let ``concurrent calls during readiness writing prevent execution from opening`` () = task {
        for _ in 1 .. 100 do
            let instance, host, owner = create ()
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable frames = 0
            let running = prepared instance (fun () -> task {
                Interlocked.Increment(&frames) |> ignore
                entered.TrySetResult(()) |> ignore
                do! release.Task.WaitAsync(deadline)
            })
            let mutable assertionFailure: exn option = None
            try
                try
                    do! entered.Task.WaitAsync(deadline)
                    Assert.False(isOpen instance)
                    let synchronous = Task.Run(Func<InvalidOperationException>(fun () ->
                        Assert.Throws<InvalidOperationException>(fun () -> host.Call("offline/sync", arguments) |> ignore)))
                    let asynchronous = Task.Run<InvalidOperationException>(Func<Task<InvalidOperationException>>(fun () ->
                        Assert.ThrowsAsync<InvalidOperationException>(fun () -> host.CallAsync("offline/async", arguments, true) :> Task)))
                    let! failures = Task.WhenAll([| synchronous; asynchronous |]).WaitAsync(deadline)
                    for failure in failures do Assert.Contains("refuses invocation host operations", failure.Message)
                    Assert.Equal(0, owner.Calls)
                    Assert.False(isOpen instance)
                with error -> assertionFailure <- Some error
            finally
                release.TrySetResult(()) |> ignore
            let! failure = Assert.ThrowsAsync<InvalidOperationException>(fun () -> running.WaitAsync(deadline))
            Assert.Contains("attempted an invocation host operation", failure.Message)
            Assert.Equal(1, frames)
            Assert.Equal(0, owner.Calls)
            Assert.False(isOpen instance)
            let! _ = Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                (prepared instance (fun () -> task { Interlocked.Increment(&frames) |> ignore })).WaitAsync(deadline))
            Assert.Equal(1, frames)
            match assertionFailure with
            | Some error -> System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw()
            | None -> ()
    }

    [<Fact>]
    let ``one successful readiness allows owner calls and a second readiness emits no frame`` () = task {
        let instance, host, owner = create ()
        let mutable frames = 0
        do! (prepared instance (fun () -> task { frames <- frames + 1 })).WaitAsync(deadline)
        Assert.True(isOpen instance)
        Assert.Equal(1, frames)
        Assert.Equal("host-preparation-contract", host.InvocationId)
        Assert.True(host.Call("offline/sync", arguments).GetBoolean())
        let! result = host.CallAsync("offline/async", arguments, true).WaitAsync(deadline)
        Assert.True(result.GetBoolean())
        Assert.Equal(2, owner.Calls)
        let! failure = Assert.ThrowsAsync<InvalidOperationException>(fun () ->
            (prepared instance (fun () -> task { frames <- frames + 1 })).WaitAsync(deadline))
        Assert.Contains("only once", failure.Message)
        Assert.Equal(1, frames)
        Assert.True(isOpen instance)
    }

    [<Fact>]
    let ``legacy explicit open delegates without a readiness frame`` () = task {
        let instance, host, owner = create ()
        capability.GetMethod("OpenLegacy", flags).Invoke(instance, [||]) |> ignore
        Assert.True(isOpen instance)
        Assert.Equal("host-preparation-contract", host.InvocationId)
        Assert.True(host.Call("offline/legacy", arguments).GetBoolean())
        let! result = host.CallAsync("offline/legacy", arguments, false).WaitAsync(deadline)
        Assert.True(result.GetBoolean())
        Assert.Equal(2, owner.Calls)
    }
