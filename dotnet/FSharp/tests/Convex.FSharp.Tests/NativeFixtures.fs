namespace Convex.FSharp.Tests

open System
open System.Text.Json
open System.Threading.Tasks
open Convex.FSharp
open Convex.Runtime

/// Fixture hosts exercise the public native ABI without exposing frontend internals.
module NativeFixtures =
    let unitMutation name =
        FunctionReference.internalMutation name (Contract.arguments<unit>()) (Contract.derived<unit>())

    let host (call: string -> JsonElement -> bool -> Task<JsonElement>) =
        { new IInvocationHost with
            member _.InvocationId = "isolated-native-fixture"
            member _.CallAsync(name, arguments, asynchronous) = call name arguments asynchronous
            member _.Call(name, arguments) = call name arguments false |> fun pending -> pending.GetAwaiter().GetResult() }

    let unavailableHost () =
        host (fun name _ _ -> Task.FromException<JsonElement>(InvalidOperationException("Unavailable owning capability: " + name)))

    let invoke (definition: IRegisteredFunction) host arguments =
        definition.Invoke(host, arguments, RuntimeJson.nullValue).GetAwaiter().GetResult()

    let emptyArguments = RuntimeJson.parse "{}"
