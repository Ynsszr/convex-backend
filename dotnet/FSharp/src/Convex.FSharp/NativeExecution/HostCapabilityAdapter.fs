namespace Convex.FSharp.NativeExecution

open System
open Convex.FSharp
open Convex.Runtime

/// Canonical values are encoded only at the F# frontend edge. The execution
/// engine carries JsonElement values and never reflects over an F# domain value.
type internal HostCapabilityAdapter(host: IInvocationHost) =
    let decode (operation: System.Threading.Tasks.Task<System.Text.Json.JsonElement>) = async {
        try
            let! value = operation |> Async.AwaitTask
            return ConvexWire.toNative(value.GetRawText())
        with error ->
            // FSharp Async.AwaitTask can retain a task fault inside a single
            // AggregateException. Recover that owning failure before translating
            // application data; a multi-failure aggregate remains an aggregate.
            let rec owningFailure (failure:exn) =
                match failure with
                | :? AggregateException as aggregate when aggregate.InnerExceptions.Count=1 ->
                    owningFailure aggregate.InnerExceptions[0]
                | _ -> failure
            match owningFailure error with
            | :? Convex.Runtime.ApplicationException as application ->
                let raw = ConvexWire.toNative(application.EncodedData.GetRawText())
                return raise (ConvexError<obj>(raw, raw, message=application.Message))
            | failure ->
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw()
                return Unchecked.defaultof<obj>
    }
    interface ITransactionHostOperations with
        member _.Call(name, arguments, asynchronous) =
            host.CallAsync(name, HostValueCodec.element arguments, asynchronous) |> decode
        member _.CallSync(name, arguments) =
            try host.Call(name, HostValueCodec.element arguments).GetRawText() |> ConvexWire.toNative
            with :? Convex.Runtime.ApplicationException as error ->
                let raw=ConvexWire.toNative(error.EncodedData.GetRawText())
                raise(ConvexError<obj>(raw,raw,message=error.Message))

/// The host chooses this frontend at composition time. Runtime assembly loading,
/// admission and catalogue code depend only on Convex.Runtime interfaces.
type FSharpExecutionFrontend() =
    interface IExecutionFrontend with
        member _.Enter(host) =
            let channel = HostCapabilityAdapter(host) :> ITransactionHostOperations
            let tables = TableRegistry.enterScope()
            let environment = InvocationRuntime.enter {
                Now = (fun () -> channel.CallSync("dotnet/now", HostArguments.empty()) |> Codec.decode<float>)
                Random = (fun () -> channel.CallSync("dotnet/random", HostArguments.empty()) |> Codec.decode<float>)
                EnvironmentVariable = (fun name ->
                    let raw = channel.CallSync("dotnet/environmentVariable", HostArguments.objectValue ["name", box name])
                    if isNull raw then None else Some(Codec.decode<string> raw))
            }
            { new IDisposable with
                member _.Dispose() =
                    environment.Dispose()
                    tables.Dispose() }

module internal FunctionInvocation =
    let enter (host: ITransactionHostOperations) =
        let operations = InvocationOperations.enter { Call = (fun name args -> host.Call(name, args, true)) }
        let logging = NativeLog.enter (fun name args -> host.CallSync(name, args))
        { new IDisposable with
            member _.Dispose() =
                logging.Dispose()
                operations.Dispose() }
