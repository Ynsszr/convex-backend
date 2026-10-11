module GraphPaginationProbe

open System
open System.Threading
open System.Threading.Tasks
open Convex.Client
open GraphContracts
open GraphGenerated

let run origin = task {
    use stop=new CancellationTokenSource(TimeSpan.FromSeconds 30.)
    use client=new Reactive.Client(ConvexHttp.endpoint origin)
    use pages=new Paginated.Client<Row>(client,(fun options -> Reactive.query Api.extra.advancedPage options),initialNumItems=5)
    let running=client.RunAsync(stop.Token)
    let wait predicate = task { while not(predicate()) do do! Task.Delay(10,stop.Token) }
    try
        do! wait(fun () -> pages.Current.Status=Paginated.Status.CanLoadMore)
        if pages.Current.Results.Length<>5 || not(pages.LoadMore()) then failwith "First reactive page was not bounded"
        do! wait(fun () -> pages.Current.Status=Paginated.Status.CanLoadMore)
        if pages.Current.Results.Length<>10 || not(pages.LoadMore()) then failwith "Second reactive page did not continue"
        do! wait(fun () -> pages.Current.Status=Paginated.Status.Exhausted)
        if pages.Current.Results.Length<>12 || (pages.Current.Results |> List.map _.value |> Set.ofList).Count<>12 then
            failwith "Reactive pagination has gaps or duplicate rows"
        let! appended=client.MutateAsync(Api.extra.append,{amount=13})
        match appended with ConvexHttp.Completed 13 -> () | _ -> failwith "Reactive pagination append failed"
        do! wait(fun () -> pages.Current.Results.Length=13)
        if (pages.Current.Results |> List.map _.value |> Set.ofList).Count<>13 then failwith "Live pagination has duplicate rows"
        let prior=client.ConnectionCount
        client.Reconnect()
        do! wait(fun () -> client.ConnectionCount>prior && pages.Current.Status=Paginated.Status.Exhausted && pages.Current.Results.Length=13)
        if pages.Current.PageCount<>3 || client.QueryCount<>3 then failwith "Pagination reconnect changed its loaded ranges"
    finally stop.Cancel()
    try do! running with :? OperationCanceledException -> ()
}
