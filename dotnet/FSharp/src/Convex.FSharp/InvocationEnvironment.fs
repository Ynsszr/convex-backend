namespace Convex.FSharp

open System

/// Invocation time and randomness come from the transaction owner. The native
/// executor supplies these capabilities; application code never reads host time.
module InvocationRuntime =
    let executionTarget = "convex-dotnet"
    type Environment = {
        Now: unit -> float; Random: unit -> float
        EnvironmentVariable: string -> string option
    }
    let private current = Threading.AsyncLocal<Environment option>()

    let enter environment =
        let previous = current.Value
        current.Value <- Some environment
        { new IDisposable with member _.Dispose() = current.Value <- previous }

    let private environment () =
        current.Value |> Option.defaultWith (fun () -> invalidOp "A Convex invocation environment is required")

    let nowMilliseconds () = (environment ()).Now ()
    let random () = (environment ()).Random ()
    let environmentVariable name = (environment ()).EnvironmentVariable name

/// Runtime operations that are not methods on a context (for example durable
/// function handles) still require the owning, invocation-scoped capability.
module internal InvocationOperations =
    type Environment = { Call: string -> obj -> Async<obj> }
    let private current=Threading.AsyncLocal<Environment option>()
    let enter environment =
        let previous=current.Value
        current.Value<-Some environment
        { new IDisposable with member _.Dispose()=current.Value<-previous }
    let call name arguments = async {
        let owner=current.Value |> Option.defaultWith(fun () -> invalidOp "A Convex invocation operation capability is required")
        return! owner.Call name arguments
    }


/// Structured native diagnostics use the owning synchronous log operation.
[<RequireQualifiedAccess>]
type NativeLogLevel = Debug | Information | Warning | Error

module NativeLog =
    let private current = Threading.AsyncLocal<(string -> obj -> obj) option>()
    let internal enter call =
        let previous = current.Value
        current.Value <- Some call
        { new IDisposable with member _.Dispose() = current.Value <- previous }
    let write level (messages: string seq) =
        let values = messages |> Seq.truncate 257 |> Seq.toArray
        if values.Length > 256 || (values |> Array.exists isNull) ||
           (values |> Array.sumBy (fun value -> Text.Encoding.UTF8.GetByteCount value)) > 4096 then
            invalidArg "messages" "Native diagnostics require at most 256 messages and 4096 UTF-8 bytes"
        let name =
            match level with
            | NativeLogLevel.Debug -> "debug"
            | NativeLogLevel.Information -> "info"
            | NativeLogLevel.Warning -> "warn"
            | NativeLogLevel.Error -> "error"
        let arguments = NativeValue.createSimpleObject()
        NativeValue.setProperty arguments "level" (box name)
        NativeValue.setProperty arguments "messages" (box values)
        let call = current.Value |> Option.defaultWith (fun () -> invalidOp "An invocation logging capability is required")
        call "dotnet/log" arguments |> ignore
