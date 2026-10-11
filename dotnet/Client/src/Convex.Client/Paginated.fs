namespace Convex.Client

open System
open System.Threading
open Convex.FSharp

/// Reactive cursor pagination over the shared client. Pages retain Convex query
/// journals; splits replace a parent only when both child ranges are available.
module Paginated =
    [<RequireQualifiedAccess>]
    type Status = LoadingFirstPage | LoadingMore | CanLoadMore | Exhausted | Failed of ConvexHttp.InvocationRefusal
    type View<'T> = {
        Results: 'T list
        Status: Status
        PageCount: int
        Timestamp: uint64 option
        Connection: Reactive.Connection
        Revision: int64
    }
    type private Page<'T> = {
        Options: AdvancedPaginationOptions
        Query: Reactive.Query<AdvancedPaginationResult<'T>>
        Subscription: IDisposable
    }
    type private Segment<'T> = { Page: Page<'T>; Replacement: (Page<'T> * Page<'T>) option }

    /// queryFactory captures application filters and places the supplied native
    /// pagination options into its own typed function arguments.
    type Client<'T>(client:Reactive.Client, queryFactory:AdvancedPaginationOptions -> Reactive.Query<AdvancedPaginationResult<'T>>,
                    ?initialNumItems:int, ?maximumPages:int) =
        let size=defaultArg initialNumItems 25
        let maximum=defaultArg maximumPages 128
        do if size<1 || size>10000 then invalidArg "initialNumItems" "Select 1..10000 rows per page."
        do if maximum<1 || maximum>256 then invalidArg "maximumPages" "Select 1..256 active pagination queries."
        let gate=obj()
        let mutable disposed=false
        let mutable processing=false
        let mutable pending:Reactive.Snapshot option=None
        let mutable inputRevision= -1L
        let mutable identity=Reactive.Snapshot.identityGeneration client.Current
        let mutable observers:Map<int,SnapshotObserver.Subscription<View<'T>>>=Map.empty
        let mutable observerId=0
        let mutable current={Results=[];Status=Status.LoadingFirstPage;PageCount=1;Timestamp=None;Connection=Reactive.Connection.Stopped;Revision=0L}
        let create options =
            Pagination.validateOptions options
            let query=queryFactory options
            {Options=options;Query=query;Subscription=client.Watch query}
        let initial () = {Page=create (AdvancedPaginationOptions.Create size);Replacement=None}
        let mutable segments=[initial()]
        let release segment =
            segment.Page.Subscription.Dispose()
            segment.Replacement |> Option.iter(fun (first,second) -> first.Subscription.Dispose();second.Subscription.Dispose())
        let activeCount () = segments |> List.sumBy(fun segment -> if segment.Replacement.IsSome then 3 else 1)
        let ready query snapshot =
            match Reactive.Snapshot.read query snapshot with
            | Reactive.Value.Ready value -> Some value
            | _ -> None
        let reconcile snapshot =
            let mutable again=true
            let mutable passes=0
            while again do
                again<-false;passes<-passes+1
                if passes>maximum then invalidOp "Pagination split expansion exceeded its bound."
                let mutable count=activeCount()
                let created=ResizeArray<Page<'T>>()
                let retired=ResizeArray<Page<'T>>()
                try
                    let next=segments |> List.collect(fun segment ->
                        match segment.Replacement with
                        | Some(first,second) when (ready first.Query snapshot).IsSome && (ready second.Query snapshot).IsSome ->
                            retired.Add segment.Page
                            count<-count-1
                            again<-true
                            [{Page=first;Replacement=None};{Page=second;Replacement=None}]
                        | Some _ -> [segment]
                        | None ->
                            match ready segment.Page.Query snapshot with
                            | Some page when page.PageStatus.IsSome ->
                                match Pagination.splitRanges segment.Page.Options page with
                                | None -> invalidOp "A split page must contain a valid split cursor."
                                | Some(first,second) ->
                                    if count+2>maximum then invalidOp "Pagination exceeded its active-page capacity."
                                    let left=create first
                                    created.Add left
                                    let right=create second
                                    created.Add right
                                    count<-count+2
                                    [{segment with Replacement=Some(left,right)}]
                            | _ -> [segment])
                    // Commit the new ownership graph before disposing replaced
                    // subscriptions. A failed expansion must not leak new pages
                    // or release any page still owned by the previous graph.
                    segments<-next
                    for page in retired do page.Subscription.Dispose()
                with _ ->
                    for page in created do page.Subscription.Dispose()
                    reraise()
        let project snapshot =
            let mutable rows=[]
            let mutable waiting=false
            let mutable failed=None
            let mutable last=None
            for segment in segments do
                match segment.Replacement with
                | Some(left,right) ->
                    for page in [left;right] do
                        match Reactive.Snapshot.read page.Query snapshot with
                        | Reactive.Value.Failed error -> failed<-Some error
                        | _ -> ()
                | _ -> ()
                match Reactive.Snapshot.read segment.Page.Query snapshot with
                | Reactive.Value.Loading -> waiting<-true
                | Reactive.Value.Failed error -> failed<-Some error
                | Reactive.Value.Ready page ->
                    if not waiting && not page.RequiresSplit then rows<-rows@page.Page
                    if page.RequiresSplit then waiting<-true
                    last<-Some page
            let split=segments |> List.exists(fun segment -> segment.Replacement.IsSome)
            let status =
                match failed,Reactive.Snapshot.connection snapshot with
                | Some error,_ | _,Reactive.Connection.Failed error -> Status.Failed error
                | _ when waiting || split -> if rows.IsEmpty then Status.LoadingFirstPage else Status.LoadingMore
                | _ -> match last with Some page when page.IsDone -> Status.Exhausted | Some _ -> Status.CanLoadMore | None -> Status.LoadingFirstPage
            {Results=rows;Status=status;PageCount=segments.Length;Timestamp=Reactive.Snapshot.timestamp snapshot
             Connection=Reactive.Snapshot.connection snapshot;Revision=current.Revision+1L}
        let enqueue snapshot =
            let start=lock gate (fun () ->
                let revision=Reactive.Snapshot.revision snapshot
                if disposed || revision<inputRevision then false else
                inputRevision<-revision
                pending<-Some snapshot
                if processing then false else processing<-true;true)
            if start then
                let mutable active=true
                while active do
                    let notification=lock gate (fun () ->
                        match pending with
                        | Some snapshot when not disposed ->
                            pending<-None
                            try
                                let nextIdentity=Reactive.Snapshot.identityGeneration snapshot
                                if nextIdentity<>identity then
                                    let previous=segments
                                    segments<-[]
                                    for segment in previous do release segment
                                    identity<-nextIdentity
                                    segments<-[initial()]
                                reconcile snapshot
                                current<-project snapshot
                            with _ ->
                                current<-{current with Results=[];Status=Status.Failed ConvexHttp.InvocationRefusal.IncompatibleResponse
                                                        ;Timestamp=Reactive.Snapshot.timestamp snapshot;Revision=current.Revision+1L}
                            Some(current,observers |> Map.toArray |> Array.map snd)
                        | _ -> processing<-false;None)
                    match notification with
                    | None -> active<-false
                    | Some(view,callbacks) -> for callback in callbacks do SnapshotObserver.publish callback view
        let observation=client.Observe enqueue

        member _.Current=lock gate (fun () -> current)
        member _.Observe(callback:View<'T> -> unit) =
            let observer=SnapshotObserver.create (fun (view:View<'T>) -> view.Revision) callback
            let id,value=lock gate (fun () ->
                if disposed then raise(ObjectDisposedException("Paginated.Client"))
                let id=observerId
                observerId<-observerId+1
                observers<-observers.Add(id,observer)
                id,current)
            SnapshotObserver.publish observer value
            { new IDisposable with
                member _.Dispose() =
                    SnapshotObserver.stop observer
                    lock gate (fun () -> observers<-observers.Remove id) }
        member _.LoadMore(?numItems:int) =
            let count=defaultArg numItems size
            if count<1 || count>10000 then invalidArg "numItems" "Select 1..10000 rows per page."
            let added=lock gate (fun () ->
                if disposed then raise(ObjectDisposedException("Paginated.Client"))
                if current.Status<>Status.CanLoadMore then false else
                if activeCount()>=maximum then invalidOp "Pagination exceeded its active-page capacity."
                let last=List.last segments
                match ready last.Page.Query client.Current with
                | Some page when not page.IsDone ->
                    if String.IsNullOrWhiteSpace page.ContinueCursor || segments |> List.exists(fun item -> item.Page.Options.Cursor=Some page.ContinueCursor) then
                        invalidOp "Pagination continuation did not make progress."
                    let next={last.Page.Options with NumItems=count;Cursor=Some page.ContinueCursor;EndCursor=None}
                    segments<-segments@[{Page=create next;Replacement=None}]
                    true
                | _ -> false)
            if added then enqueue client.Current
            added
        interface IDisposable with
            member _.Dispose() =
                let previous,callbacks=lock gate (fun () ->
                    if disposed then [],[||] else
                    let callbacks=observers |> Map.toArray |> Array.map snd
                    disposed<-true;observers<-Map.empty;pending<-None
                    let previous=segments
                    segments<-[]
                    previous,callbacks)
                for callback in callbacks do SnapshotObserver.stop callback
                observation.Dispose()
                for segment in previous do release segment
