namespace FleetManagement

open Convex.FSharp

module NativeDeclarations =
    let schema=Schema.define [
        FleetIndexes.computersDefinition
        TableSchema.defineForTable Tables.logs
    ]
    let app=NativeComponentDefinitions.app [] [] NativeComponentDefinitions.emptyExports None
    let functions = [|
        "registerComputer",FunctionRegistration.erase Functions.registerComputer
        "setDesiredState",FunctionRegistration.erase Functions.setDesiredState
        "reportHeartbeat",FunctionRegistration.erase Functions.reportHeartbeat
        "getComputer",FunctionRegistration.erase Functions.getComputer
        "listFleet",FunctionRegistration.erase Functions.listFleet
        "searchByName",FunctionRegistration.erase Functions.searchByName
        "paginateFleet",FunctionRegistration.erase Functions.paginateFleet
        "reconcileComputer",FunctionRegistration.erase Functions.reconcileComputer
        "listLogs",FunctionRegistration.erase Functions.listLogs
        "restartComputer",FunctionRegistration.erase Functions.restartComputer
        "whoami",FunctionRegistration.erase Functions.whoami
    |]
    let admin = [|"getFleetSummary",FunctionRegistration.erase Admin.getFleetSummary;"decommissionFleet",FunctionRegistration.erase Admin.decommissionFleet|]
    let modules=[|"functions","FleetManagement.Functions",functions;"admin","FleetManagement.Admin",admin|]
