namespace FleetManagement

open System
open Convex.FSharp
open Tables

module Functions =

    let registerComputer =
        Convex.mutation (fun ctx (args: RegisterComputerArgs) ->
            async {
                let doc = {
                    Name = args.Name
                    Desired = DesiredState.Running
                    Observed = ObservedState.Unknown
                    LastPing = None
                }
                let! id = ctx.Db.Insert(computers, doc)
                let log = {
                    ComputerName = args.Name
                    Message = sprintf "Computer %s registered into fleet" args.Name
                    Timestamp = 1L
                }
                let! _ = ctx.Db.Insert(logs, log)
                return Id.value id
            }
        )

    let setDesiredState =
        Convex.mutation (fun ctx (args: SetDesiredArgs) ->
            async {
                do! ctx.Db.Patch(args.Id, {| Desired = args.Desired |})
                let! comp = ctx.Db.Get(args.Id)
                match comp with
                | Some c ->
                    let reconcilePayload = { ComputerName = c.Name; ExpectedDesired = args.Desired }
                    let! _ = ctx.Scheduler.RunAfter(1000L, FunctionReference.internalMutation "functions:reconcileComputer" (Contract.arguments<ReconcileArgs>()) (Contract.derived<unit>()), reconcilePayload)
                    ()
                | None -> ()
                return ()
            }
        )

    let reportHeartbeat =
        Convex.mutation (fun ctx (args: ReportHeartbeatArgs) ->
            ctx.Db.Patch(
                args.Id,
                {| Observed = args.Observed; LastPing = Some args.Timestamp |}
            )
        )

    let getComputer =
        Convex.query (fun ctx (args: ComputerIdArgs) ->
            ctx.Db.Get(args.Id)
        )

    let listFleet =
        Convex.query (fun ctx () ->
            ctx.Db.Query(computers).Collect()
        )

    let searchByName =
        Convex.query (fun ctx (args: SearchByNameArgs) ->
            ctx.Db.Query(computers)
                .WithIndex(FleetIndexes.byName, fun r -> r.Eq("Name", args.Name))
                .Collect()
        )

    let paginateFleet =
        Convex.query (fun ctx (args: PaginateFleetArgs) ->
            ctx.Db.Query(computers)
                .Order(QueryOrder.Asc)
                .Paginate({ NumItems = args.NumItems; Cursor = args.Cursor })
        )

    let reconcileComputer =
        Convex.internalMutation (fun ctx (args: ReconcileArgs) ->
            async {
                let log = {
                    ComputerName = args.ComputerName
                    Message = sprintf "Reconciliation verified for %A" args.ExpectedDesired
                    Timestamp = 2L
                }
                let! _ = ctx.Db.Insert(logs, log)
                return ()
            }
        )

    let listLogs =
        Convex.query (fun ctx () ->
            ctx.Db.Query(logs).Collect()
        )

    let restartComputer =
        Convex.action (fun ctx (args: RestartActionArgs) ->
            async {
                // Read current state
                let! _ = ctx.RunQuery(FunctionRegistration.reference "functions:getComputer" getComputer, ({ Id = args.Id }: ComputerIdArgs))
                // Trigger Stop
                do! ctx.RunMutation(FunctionRegistration.reference "functions:setDesiredState" setDesiredState, { Id = args.Id; Desired = DesiredState.Stopped })
                // Trigger Run
                do! ctx.RunMutation(FunctionRegistration.reference "functions:setDesiredState" setDesiredState, { Id = args.Id; Desired = DesiredState.Running })
                // Read final state
                let! finalComp = ctx.RunQuery(FunctionRegistration.reference "functions:getComputer" getComputer, ({ Id = args.Id }: ComputerIdArgs))
                return finalComp
            }
        )

    let whoami =
        Convex.query (fun ctx () ->
            async {
                let! ident = ctx.Auth.GetUserIdentity()
                return ident |> Option.map (fun i -> i.TokenIdentifier)
            }
        )
