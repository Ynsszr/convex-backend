namespace FleetManagement

open Convex.FSharp

module Admin =
    let getFleetSummary=Convex.query(fun ctx () -> async {
        let! computers=ctx.Db.Query(Tables.computers).Collect()
        return {|total=computers.Length
                 running=computers |> List.filter(fun value -> value.Desired=DesiredState.Running) |> List.length
                 stopped=computers |> List.filter(fun value -> value.Desired=DesiredState.Stopped) |> List.length|}
    })
    let decommissionFleet=Convex.mutation(fun ctx () -> async {
        let! computers=ctx.Db.Query(Tables.computers).CollectDocs()
        for computer in computers do
            do! ctx.Db.Patch(Tables.computers,computer.Id,DocumentPatch.empty |> DocumentPatch.set "Desired" DesiredState.Stopped)
        return computers.Length
    })
