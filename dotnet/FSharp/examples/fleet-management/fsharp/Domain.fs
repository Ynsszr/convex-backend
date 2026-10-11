namespace FleetManagement

open System
open Convex.FSharp

type DesiredState =
    | Running
    | Stopped

type ObservedState =
    | Unknown
    | Starting
    | Running of pid: int64
    | Stopping
    | Stopped
    | Failed of reason: string

type Computer =
    {
        Name: string
        Desired: DesiredState
        Observed: ObservedState
        LastPing: int64 option
    }

type LogEntry =
    {
        ComputerName: string
        Message: string
        Timestamp: int64
    }

module Tables =
    let computers = Table.define<Computer> "computers"
    let logs = Table.define<LogEntry> "logs"

type RegisterComputerArgs =
    {
        Name: string
    }


type SetDesiredArgs =
    {
        Id: Id<Computer>
        Desired: DesiredState
    }

type ReportHeartbeatArgs =
    {
        Id: Id<Computer>
        Observed: ObservedState
        Timestamp: int64
    }

type ComputerIdArgs =
    {
        Id: Id<Computer>
    }

type SearchByNameArgs =
    {
        Name: string
    }

type PaginateFleetArgs =
    {
        NumItems: int
        Cursor: string option
    }

type ReconcileArgs =
    {
        ComputerName: string
        ExpectedDesired: DesiredState
    }

type RestartActionArgs =
    {
        Id: Id<Computer>
    }

module FleetIndexes =
    let computersDefinition = (TableSchema.defineForTable Tables.computers).WithIndex("by_name",["Name"])
    let byName = computersDefinition.BindIndex(Tables.computers,Tables.computers.Index("by_name",["Name"]))
