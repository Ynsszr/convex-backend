namespace Convex.FSharp

open System

[<RequireQualifiedAccess>]
type AuditVariable = RequestId | Ip | UserAgent | Now | ConvexActor

/// Audit data is JSON, with explicit server-substituted variables. It is not a
/// Convex document codec: bigint, blobs and unresolved commits are not JSON.
[<RequireQualifiedAccess>]
type AuditValue =
    | Null
    | String of string
    | Boolean of bool
    | Number of float
    | Array of AuditValue list
    | Object of Map<string,AuditValue>
    | Variable of AuditVariable
    | Omitted

module AuditLog =
    let private variableName = function
        | AuditVariable.RequestId -> "requestId"
        | AuditVariable.Ip -> "ip"
        | AuditVariable.UserAgent -> "userAgent"
        | AuditVariable.Now -> "now"
        | AuditVariable.ConvexActor -> "convexActor"

    let private encode resolve fields =
        let rec convert depth value =
            if depth>64 then invalidArg "value" "Audit data exceeds 64 container levels"
            match value with
            | AuditValue.Null -> null
            | AuditValue.String text -> if isNull text then invalidArg "value" "Use AuditValue.Null for null" else box text
            | AuditValue.Boolean flag -> box flag
            | AuditValue.Number number ->
                if Double.IsNaN number || Double.IsInfinity number then invalidArg "value" "Audit numbers must be finite JSON numbers"
                box number
            | AuditValue.Variable variable -> resolve(variableName variable)
            | AuditValue.Omitted -> NativeValue.undefinedValue()
            | AuditValue.Array values ->
                values |> List.map(fun item -> if item=AuditValue.Omitted then null else convert (depth+1) item) |> List.toArray |> box
            | AuditValue.Object properties ->
                let output=NativeValue.createSimpleObject()
                for KeyValue(key,item) in properties do
                    if isNull key || key.StartsWith("$",StringComparison.Ordinal) then invalidArg "fields" "Audit property names cannot start with '$'"
                    if item<>AuditValue.Omitted then NativeValue.setProperty output key (convert (depth+1) item)
                output
        convert 0 (AuditValue.Object fields)

    /// Inspect the exact variable substitutions without recording an audit event.
    let envelope fields =
        encode (fun name -> let value=NativeValue.createSimpleObject() in NativeValue.setProperty value "$var" (box name);value) fields

    /// Entitlement, atomicity and delivery are properties of the owning backend.
    /// Unsupported deployments return its real error; this never reports a fake log.
    let write fields : Async<unit> = async {
        let arguments=NativeValue.createSimpleObject()
        NativeValue.setProperty arguments "body" (envelope fields)
        NativeValue.setProperty arguments "version" (box "1.46.0")
        let! _=InvocationOperations.call "1.0/auditLog" arguments
        return ()
    }
