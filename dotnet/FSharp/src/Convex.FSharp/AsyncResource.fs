namespace Convex.FSharp

open System
open System.Threading

/// Await resource cleanup on success, exception, and cancellation. Cleanup is not
/// cancelled by the operation it is cleaning up, and never hides a primary error.
module AsyncResource =
    type private Outcome<'T> =
        | Completed of 'T
        | Failed of exn
        | Cancelled of OperationCanceledException

    let usingAsync (acquire:unit -> 'Resource) (release:'Resource -> Async<unit>) (body:'Resource -> Async<'T>) : Async<'T> = async {
        let! cancellation=Async.CancellationToken
        return! Async.FromContinuations(fun (success,failure,cancelled) ->
            let acquired =
                try Choice1Of2(acquire())
                with error -> Choice2Of2 error
            match acquired with
            | Choice2Of2 error -> failure error
            | Choice1Of2 resource ->
                let gate=obj()
                let mutable finished=false
                let finish outcome =
                    let first=lock gate (fun () ->
                        if finished then false
                        else finished<-true;true)
                    if first then
                        let forward () =
                            match outcome with
                            | Completed value -> success value
                            | Failed error -> failure error
                            | Cancelled error -> cancelled error
                        let cleanupFailed error =
                            match outcome with
                            | Completed _ -> failure error
                            | _ -> forward()
                        try
                            Async.StartWithContinuations(
                                release resource,
                                forward,
                                cleanupFailed,
                                (fun error -> cleanupFailed error),
                                cancellationToken=CancellationToken.None)
                        with error -> cleanupFailed error
                try
                    Async.StartWithContinuations(
                        body resource,
                        (fun value -> finish (Completed value)),
                        (fun error -> finish (Failed error)),
                        (fun error -> finish (Cancelled error)),
                        cancellationToken=cancellation)
                with error -> finish (Failed error))
    }
