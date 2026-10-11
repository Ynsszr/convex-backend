namespace Convex.Client

/// Shared subscription delivery for immutable, monotonically versioned views.
/// User callbacks never run under the registration/state lock. A slow callback
/// coalesces pending views but cannot overlap itself or observe older revisions.
module internal SnapshotObserver =
    type Subscription<'T> = private {
        Gate: obj
        Callback: 'T -> unit
        Revision: 'T -> int64
        mutable LastRevision: int64
        mutable Pending: 'T option
        mutable Dispatching: bool
        mutable Active: bool
    }

    let create revision callback =
        {Gate=obj();Callback=callback;Revision=revision;LastRevision= -1L
         Pending=None;Dispatching=false;Active=true}

    let publish observer snapshot =
        let dispatch=lock observer.Gate (fun () ->
            let revision=observer.Revision snapshot
            if not observer.Active || revision<=observer.LastRevision then false else
            match observer.Pending with
            | Some value when observer.Revision value>=revision -> ()
            | _ -> observer.Pending<-Some snapshot
            if observer.Dispatching then false else
            observer.Dispatching<-true
            true)
        if dispatch then
            let mutable running=true
            while running do
                let next=lock observer.Gate (fun () ->
                    match observer.Pending with
                    | Some value when observer.Active ->
                        observer.Pending<-None
                        observer.LastRevision<-observer.Revision value
                        Some value
                    | _ -> observer.Pending<-None;observer.Dispatching<-false;None)
                match next with
                | Some value -> try observer.Callback value with _ -> ()
                | None -> running<-false

    // An already selected callback may finish; queued views are discarded
    // on disposal. Waiting for that callback here would deadlock reentrancy.
    let stop observer =
        lock observer.Gate (fun () -> observer.Active<-false;observer.Pending<-None)
