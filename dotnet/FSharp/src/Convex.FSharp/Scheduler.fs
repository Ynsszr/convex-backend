namespace Convex.FSharp

open System

type ScheduledFunctionsTable = interface end
type ScheduledFunctionId = Id<ScheduledFunctionsTable>

module ScheduleValidation =
    let timestamp (value: int64) =
        if value < -9007199254740991L || value > 9007199254740991L then
            invalidArg "timestamp" "Scheduler milliseconds exceed exact Convex timestamp precision"
    let delay (value: int64) =
        timestamp value
        if value < 0L then invalidArg "delay" "Scheduler delay must be non-negative"

/// Raw operations belong to runtime adapters. Authored calls use the typed
/// extensions, which admit mutation and action targets only.
type IScheduler =
    abstract RunAfterRaw: delayMs: int64 * target: obj * args: obj -> Async<string>
    abstract RunAtRaw: timestampMs: int64 * target: obj * args: obj -> Async<string>
    abstract Cancel: id: ScheduledFunctionId -> Async<unit>

[<AutoOpen>]
module SchedulerExtensions =
    type IScheduler with
        member this.RunAfter<'Kind, 'Visibility, 'Args, 'Result when 'Kind :> IScheduledFunctionKind>
            (delayMs: int64, fn: FunctionReference<'Kind, 'Visibility, 'Args, 'Result>, args: 'Args) = async {
            ScheduleValidation.delay delayMs
            let! id = this.RunAfterRaw(delayMs, fn.ToNativeTarget(), fn.Arguments.Encode args)
            return (Id id: ScheduledFunctionId)
        }
        member this.RunAfter<'Kind, 'Visibility, 'Args, 'Result when 'Kind :> IScheduledFunctionKind>
            (delayMs: int64, handle: FunctionHandle<'Kind, 'Visibility, 'Args, 'Result>, args: 'Args) =
            this.RunAfter(delayMs, handle.Reference, args)
        member this.RunAt<'Kind, 'Visibility, 'Args, 'Result when 'Kind :> IScheduledFunctionKind>
            (timestampMs: int64, fn: FunctionReference<'Kind, 'Visibility, 'Args, 'Result>, args: 'Args) = async {
            ScheduleValidation.timestamp timestampMs
            let! id = this.RunAtRaw(timestampMs, fn.ToNativeTarget(), fn.Arguments.Encode args)
            return (Id id: ScheduledFunctionId)
        }
        member this.RunAt<'Kind, 'Visibility, 'Args, 'Result when 'Kind :> IScheduledFunctionKind>
            (timestampMs: int64, handle: FunctionHandle<'Kind, 'Visibility, 'Args, 'Result>, args: 'Args) =
            this.RunAt(timestampMs, handle.Reference, args)

[<RequireQualifiedAccess>]
type IntervalSchedule =
    | Seconds of int
    | Minutes of int
    | Hours of int

[<RequireQualifiedAccess>]
type CronSchedule =
    | Interval of IntervalSchedule
    | Hourly of minuteUTC: int option
    | Daily of hourUTC: int * minuteUTC: int option
    | Weekly of dayOfWeek: string * hourUTC: int * minuteUTC: int option
    | Monthly of day: int * hourUTC: int * minuteUTC: int option
    | Cron of cronString: string

type CronJob =
    {
        Name: string
        TargetFunction: string
        ArgsJson: string
        Schedule: CronSchedule
    }

module CronValidation =
    let identifier (value:string) =
        if isNull value || value |> Seq.exists (fun c -> c < ' ' || c > '~') then
            invalidArg "identifier" "Cron identifiers must contain only printable ASCII characters"
    let schedule value =
        let between name low high value =
            if value < low || value > high then invalidArg name (sprintf "%s must be between %d and %d" name low high)
        let minute value = value |> Option.iter (between "minuteUTC" 0 59)
        match value with
        | CronSchedule.Interval (IntervalSchedule.Seconds n | IntervalSchedule.Minutes n | IntervalSchedule.Hours n) ->
            if n <= 0 then invalidArg "interval" "Interval must be greater than zero"
        | CronSchedule.Hourly m -> minute m
        | CronSchedule.Daily (h,m) -> between "hourUTC" 0 23 h; minute m
        | CronSchedule.Weekly (day,h,m) ->
            if not (List.contains day ["sunday";"monday";"tuesday";"wednesday";"thursday";"friday";"saturday"]) then
                invalidArg "dayOfWeek" "Use a lowercase day of the week"
            between "hourUTC" 0 23 h; minute m
        | CronSchedule.Monthly (day,h,m) -> between "day" 1 31 day; between "hourUTC" 0 23 h; minute m
        | CronSchedule.Cron expression ->
            if isNull expression then invalidArg "cron" "Cron expression must be a string"
            // The owning Convex backend validates the cron grammar, as in the SDK.
    let arguments (json:string) =
        use parsed=System.Text.Json.JsonDocument.Parse json
        if parsed.RootElement.ValueKind <> System.Text.Json.JsonValueKind.Array || parsed.RootElement.GetArrayLength() <> 1 || parsed.RootElement.[0].ValueKind <> System.Text.Json.JsonValueKind.Object then
            invalidArg "argsJson" "Cron arguments must contain exactly one object envelope"

type Crons() =
    let mutable jobs: (string * CronJob) list = []

    member this.Jobs = jobs

    member this.ScheduleRaw(identifier: string, schedule: CronSchedule, fnName: string, argsJson: string) =
        CronValidation.identifier identifier
        CronValidation.schedule schedule
        CronValidation.arguments argsJson
        if jobs |> List.exists (fun (name,_) -> name = identifier) then invalidArg "identifier" "Cron identifier registered twice"
        if String.IsNullOrWhiteSpace fnName then invalidArg "fnName" "A named Convex function is required"
        let job = { Name = identifier; TargetFunction = fnName; ArgsJson = argsJson; Schedule = schedule }
        jobs <- jobs @ [ (identifier, job) ]

    member this.Export() : string =
        let quote = ValidatorJson.quote
        let sb = System.Text.StringBuilder()
        sb.Append("{") |> ignore
        jobs
        |> List.iteri (fun i (id, job) ->
            if i > 0 then sb.Append(",") |> ignore
            let schedJson =
                match job.Schedule with
                | CronSchedule.Interval(IntervalSchedule.Seconds s) -> sprintf "{\"type\":\"interval\",\"seconds\":%d}" s
                | CronSchedule.Interval(IntervalSchedule.Minutes m) -> sprintf "{\"type\":\"interval\",\"minutes\":%d}" m
                | CronSchedule.Interval(IntervalSchedule.Hours h) -> sprintf "{\"type\":\"interval\",\"hours\":%d}" h
                | CronSchedule.Hourly None -> "{\"type\":\"hourly\"}"
                | CronSchedule.Hourly(Some m) -> sprintf "{\"type\":\"hourly\",\"minuteUTC\":%d}" m
                | CronSchedule.Daily(h, None) -> sprintf "{\"type\":\"daily\",\"hourUTC\":%d}" h
                | CronSchedule.Daily(h, Some m) -> sprintf "{\"type\":\"daily\",\"hourUTC\":%d,\"minuteUTC\":%d}" h m
                | CronSchedule.Weekly(dow, h, None) -> sprintf "{\"type\":\"weekly\",\"dayOfWeek\":%s,\"hourUTC\":%d}" (quote dow) h
                | CronSchedule.Weekly(dow, h, Some m) -> sprintf "{\"type\":\"weekly\",\"dayOfWeek\":%s,\"hourUTC\":%d,\"minuteUTC\":%d}" (quote dow) h m
                | CronSchedule.Monthly(d, h, None) -> sprintf "{\"type\":\"monthly\",\"day\":%d,\"hourUTC\":%d}" d h
                | CronSchedule.Monthly(d, h, Some m) -> sprintf "{\"type\":\"monthly\",\"day\":%d,\"hourUTC\":%d,\"minuteUTC\":%d}" d h m
                | CronSchedule.Cron expr -> sprintf "{\"type\":\"cron\",\"cron\":%s}" (quote expr)

            sb.Append(sprintf "%s:{\"name\":%s,\"args\":%s,\"schedule\":%s}" (quote id) (quote job.TargetFunction) job.ArgsJson schedJson) |> ignore
        )
        sb.Append("}") |> ignore
        sb.ToString()

    interface Convex.Runtime.IDeclaration with
        member _.Kind = Convex.Runtime.DeclarationKind.CronJobs
        member this.Export() = Convex.Runtime.RuntimeJson.parse(this.Export())

module CronJobs =
    let create () = Crons()

[<AutoOpen>]
module CronsExtensions =
    type Crons with
        member this.Schedule<'Kind, 'Visibility, 'Args, 'Ret when 'Kind :> IScheduledFunctionKind>(identifier: string, schedule: CronSchedule, fn: FunctionReference<'Kind, 'Visibility, 'Args, 'Ret>, args: 'Args) =
            let encoded = fn.Arguments.Encode args
            let argsJson =
                ConvexJson.toJson (ConvexValue.Array [Codec.decode<ConvexValue> encoded])
            let name =
                match fn.Target with
                | FunctionTarget.Named name -> name
                | _ -> invalidArg "fn" "Cron jobs require a local named function; schedule component handles with IScheduler"
            this.ScheduleRaw(identifier, schedule, name, argsJson)

        member this.Interval<'Kind, 'Visibility, 'Args, 'Ret when 'Kind :> IScheduledFunctionKind>(identifier: string, interval: IntervalSchedule, fn: FunctionReference<'Kind, 'Visibility, 'Args, 'Ret>, args: 'Args) =
            this.Schedule(identifier, CronSchedule.Interval interval, fn, args)

        member this.Hourly<'Kind, 'Visibility, 'Args, 'Ret when 'Kind :> IScheduledFunctionKind>(identifier: string, fn: FunctionReference<'Kind, 'Visibility, 'Args, 'Ret>, args: 'Args, ?minuteUTC: int) =
            this.Schedule(identifier, CronSchedule.Hourly minuteUTC, fn, args)

        member this.Daily<'Kind, 'Visibility, 'Args, 'Ret when 'Kind :> IScheduledFunctionKind>(identifier: string, hourUTC: int, fn: FunctionReference<'Kind, 'Visibility, 'Args, 'Ret>, args: 'Args, ?minuteUTC: int) =
            this.Schedule(identifier, CronSchedule.Daily(hourUTC, minuteUTC), fn, args)

        member this.Weekly<'Kind, 'Visibility, 'Args, 'Ret when 'Kind :> IScheduledFunctionKind>(identifier: string, dayOfWeek: string, hourUTC: int, fn: FunctionReference<'Kind, 'Visibility, 'Args, 'Ret>, args: 'Args, ?minuteUTC: int) =
            this.Schedule(identifier, CronSchedule.Weekly(dayOfWeek, hourUTC, minuteUTC), fn, args)

        member this.Monthly<'Kind, 'Visibility, 'Args, 'Ret when 'Kind :> IScheduledFunctionKind>(identifier: string, day: int, hourUTC: int, fn: FunctionReference<'Kind, 'Visibility, 'Args, 'Ret>, args: 'Args, ?minuteUTC: int) =
            this.Schedule(identifier, CronSchedule.Monthly(day, hourUTC, minuteUTC), fn, args)

        member this.Cron<'Kind, 'Visibility, 'Args, 'Ret when 'Kind :> IScheduledFunctionKind>(identifier: string, cronExpr: string, fn: FunctionReference<'Kind, 'Visibility, 'Args, 'Ret>, args: 'Args) =
            this.Schedule(identifier, CronSchedule.Cron cronExpr, fn, args)

