namespace RuntimeFixture

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Runtime.CompilerServices
open System.Threading.Tasks
open System.Threading.Tasks.Sources
open Convex.FSharp

// The calls below must remain framework slot operands in optimized IL. Tests
// inspect those operands before exercising the real frozen Resolve boundary.
type private FrameworkCallbackEntryPoints private () =
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Render(value: obj) = value.ToString()

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Dispose(value: IDisposable) = value.Dispose()

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Format(value: obj) = String.Format("{0}", value)

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Add(value: Collection<int>) = value.Add(42)

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member ContainsPair<'T>(values: ICollection<KeyValuePair<string, 'T>>, value: 'T) =
        values.Contains(KeyValuePair("key", value))

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member RemovePair<'T>(values: ICollection<KeyValuePair<string, 'T>>, value: 'T) =
        values.Remove(KeyValuePair("key", value))

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Project<'T>(projection: 'T -> int, value: 'T) = projection value

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Default<'T>(projection: unit -> 'T, value: 'T option) = Option.defaultWith projection value

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Some<'T>(value: 'T) = Some value

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Specialize<'T>(value: Microsoft.FSharp.Core.FSharpTypeFunc) = unbox<int>(value.Specialize<'T>())

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Double(value: obj) = Convert.ToDouble(value, Globalization.CultureInfo.InvariantCulture)

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member FormatValue<'T>() =
        PrintfFormat<'T -> string, unit, string, string, 'T>("%A").Value

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member CapturedFormatValue<'T>(value: 'T) =
        PrintfFormat<string, unit, string, string, 'T>("%P()", [|box value|], null).Captures[0]

type private PureDisplay() =
    override _.ToString() = "42"

type private ClockDisplay() =
    override _.ToString() = DateTime.UtcNow.Day.ToString(Globalization.CultureInfo.InvariantCulture)

type private PureDisposable() =
    let mutable count = 0
    member _.Count = count
    interface IDisposable with
        member _.Dispose() = count <- count + 1

type private ClockDisposable() =
    let mutable count = 0
    member _.Count = count
    interface IDisposable with
        member _.Dispose() = count <- DateTime.UtcNow.Day

type private PureCollection() =
    inherit Collection<int>()
    override _.InsertItem(index, item) = base.InsertItem(index, item)

type private ClockCollection() =
    inherit Collection<int>()
    override _.InsertItem(index, _) = base.InsertItem(index, DateTime.UtcNow.Day)

type private StableTextComparer() =
    inherit StringComparer()
    override _.Compare(left: string, right: string) = StringComparer.Ordinal.Compare(left, right)
    override _.Equals(left: string, right: string) = String.Equals(left, right, StringComparison.Ordinal)
    override _.GetHashCode(_: string) = 0

type private ClockTextComparer() =
    inherit StringComparer()
    override _.Compare(left: string, right: string) = StringComparer.Ordinal.Compare(left, right)
    override _.Equals(left: string, right: string) = String.Equals(left, right, StringComparison.Ordinal)
    override _.GetHashCode(_: string) = DateTime.UtcNow.Day

type private PureEqualityValue(value: int) =
    member _.Value = value
    override _.Equals(other) =
        match other with
        | :? PureEqualityValue as other -> value = other.Value
        | _ -> false
    override _.GetHashCode() = 0

type private ClockEqualityValue(value: int) =
    member _.Value = value
    override _.Equals(other) =
        match other with
        | :? ClockEqualityValue as other -> DateTime.UtcNow.Day = other.Value
        | _ -> false
    override _.GetHashCode() = 0

type PureDecodedInput =
    { value: int }
    override this.ToString() = this.value.ToString(Globalization.CultureInfo.InvariantCulture)

type ClockDecodedInput =
    { value: int }
    override _.ToString() = DateTime.UtcNow.Day.ToString(Globalization.CultureInfo.InvariantCulture)

type private ProjectedRecord = { Value: int; Label: string }

type private PureTypeFunction() =
    inherit Microsoft.FSharp.Core.FSharpTypeFunc()
    override _.Specialize<'T>() = box 42

type private ClockTypeFunction() =
    inherit Microsoft.FSharp.Core.FSharpTypeFunc()
    override _.Specialize<'T>() = box DateTime.UtcNow.Day

[<CustomEquality; NoComparison>]
type private UnusedHashRecord =
    { Value: int }
    override this.Equals(other) =
        match other with
        | :? UnusedHashRecord as other -> this.Value = other.Value
        | _ -> false
    override _.GetHashCode() = DateTime.UtcNow.Day

type private NumericConvertible(value: unit -> float) =
    interface IConvertible with
        member _.GetTypeCode() = TypeCode.Double
        member _.ToDouble(_) = value()
        member _.ToType(_, _) = box 42
        member _.ToBoolean(_) = true
        member _.ToChar(_) = 'x'
        member _.ToSByte(_) = 42y
        member _.ToByte(_) = 42uy
        member _.ToInt16(_) = 42s
        member _.ToUInt16(_) = 42us
        member _.ToInt32(_) = 42
        member _.ToUInt32(_) = 42u
        member _.ToInt64(_) = 42L
        member _.ToUInt64(_) = 42UL
        member _.ToSingle(_) = 42.0f
        member _.ToDecimal(_) = 42M
        member _.ToDateTime(_) = DateTime.MinValue
        member _.ToString(_) = "42"

type private GenericFormatter<'Tag>(value: int) =
    interface IFormattable with
        member _.ToString(_, _) = value.ToString(Globalization.CultureInfo.InvariantCulture)

type private PureValueTaskSource() =
    interface IValueTaskSource<int> with
        member _.GetStatus(_) = ValueTaskSourceStatus.Succeeded
        member _.GetResult(_) = 42
        member _.OnCompleted(continuation, state, _, _) = continuation.Invoke state

type private ClockValueTaskSource() =
    interface IValueTaskSource<int> with
        member _.GetStatus(_) = ValueTaskSourceStatus.Succeeded
        member _.GetResult(_) = DateTime.UtcNow.Day
        member _.OnCompleted(continuation, state, _, _) = continuation.Invoke state

[<AbstractClass>]
type private FrozenVirtualBase() =
    abstract Read: unit -> int
    default _.Read() = 42

type private PureFrozenVirtual() =
    inherit FrozenVirtualBase()

type private ClockFrozenVirtual() =
    inherit FrozenVirtualBase()
    override _.Read() = DateTime.UtcNow.Day

type private FrozenVirtualEntryPoint private () =
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Read(value: FrozenVirtualBase) = value.Read()

type NativeFrameworkCallbackFixtures private () =
    static member caughtProjection =
        Convex.query (fun _ (_: unit) -> async {
            let! value = async {
                try return invalidOp "caught fixture"
                with _ -> return { Value = 42; Label = "caught" }
            }
            return value.Value
        })

    static member catchClock =
        Convex.query (fun _ (_: unit) -> async {
            let! value = async {
                try return invalidOp "caught fixture"
                with _ -> return { Value = DateTime.UtcNow.Day; Label = "caught" }
            }
            return value.Value
        })

    static member storedFormatArgument =
        Convex.query (fun _ (_: unit) -> async {
            return 21 * (FrameworkCallbackEntryPoints.FormatValue<UnusedHashRecord>()).Length
        })

    static member renderedCapturedFormat =
        Convex.query (fun _ (_: unit) -> async {
            return int (FrameworkCallbackEntryPoints.Format(FrameworkCallbackEntryPoints.CapturedFormatValue(PureDisplay())))
        })

    static member clockCapturedFormat =
        Convex.query (fun _ (_: unit) -> async {
            return int (FrameworkCallbackEntryPoints.Format(FrameworkCallbackEntryPoints.CapturedFormatValue(ClockDisplay())))
        })

    static member pureNumericConversion =
        Convex.query (fun _ (_: unit) -> async {
            let record = { Value = 42; Label = "kept" }
            let number = FrameworkCallbackEntryPoints.Double(box record.Value)
            return int number
        })

    static member pureConvertible =
        Convex.query (fun _ (_: unit) -> async {
            return int (FrameworkCallbackEntryPoints.Double(NumericConvertible(fun () -> 42.)))
        })

    static member clockConvertible =
        Convex.query (fun _ (_: unit) -> async {
            return int (FrameworkCallbackEntryPoints.Double(NumericConvertible(fun () -> float DateTime.UtcNow.Day)))
        })

    static member pureSpecialization =
        Convex.query (fun _ (_: unit) -> async {
            return FrameworkCallbackEntryPoints.Specialize<ProjectedRecord>(PureTypeFunction())
        })

    static member clockSpecialization =
        Convex.query (fun _ (_: unit) -> async {
            return FrameworkCallbackEntryPoints.Specialize<ProjectedRecord>(ClockTypeFunction())
        })

    static member sequentialProjection =
        Convex.query (fun _ (_: unit) -> async {
            let! values = [ async { return { Value = 42; Label = "kept" } } ] |> ConvexAsync.sequence
            return values |> Array.sumBy (fun value -> FrameworkCallbackEntryPoints.Project((fun (record: ProjectedRecord) -> record.Value), value))
        })

    static member projectionClock =
        Convex.query (fun _ (_: unit) -> async {
            return FrameworkCallbackEntryPoints.Project((fun (_: ProjectedRecord) -> DateTime.UtcNow.Day), { Value = 42; Label = "kept" })
        })

    static member collectedProjection =
        Convex.query (fun _ (_: unit) -> async {
            let inputs = [1] @ [2]
            let values = [| for value in inputs do yield { Value = value*14; Label = "kept" } |]
            return values |> Array.sumBy (fun value -> value.Value)
        })

    static member defaultProjection =
        Convex.query (fun _ (_: unit) -> async {
            let value = FrameworkCallbackEntryPoints.Default((fun () -> { Value = 42; Label = "kept" }),None)
            return value.Value
        })

    static member someProjection =
        Convex.query (fun _ (_: unit) -> async {
            let value = FrameworkCallbackEntryPoints.Some { Value = 42; Label = "kept" }
            return value |> Option.map (fun item -> item.Value) |> Option.defaultValue 0
        })

    static member defaultClock =
        Convex.query (fun _ (_: unit) -> async {
            let value = FrameworkCallbackEntryPoints.Default((fun () -> { Value = DateTime.UtcNow.Day; Label = "kept" }),None)
            return value.Value
        })

    static member projectionHash =
        Convex.query (fun _ (_: unit) -> async {
            return FrameworkCallbackEntryPoints.Project((fun (record: ProjectedRecord) -> record.GetHashCode()), { Value = 42; Label = "kept" })
        })

    static member pureToString =
        Convex.query (fun _ (_: unit) -> async {
            return Int32.Parse(FrameworkCallbackEntryPoints.Render(PureDisplay()))
        })

    static member clockToString =
        Convex.query (fun _ (_: unit) -> async {
            return Int32.Parse(FrameworkCallbackEntryPoints.Render(ClockDisplay()))
        })

    static member capturedClockToString =
        let receiver = ClockDisplay() :> obj
        Convex.query (fun _ (_: unit) -> async {
            return Int32.Parse(FrameworkCallbackEntryPoints.Render receiver)
        })

    static member pureDispose =
        Convex.query (fun _ (_: unit) -> async {
            let receiver = new PureDisposable()
            FrameworkCallbackEntryPoints.Dispose receiver
            return receiver.Count
        })

    static member clockDispose =
        Convex.query (fun _ (_: unit) -> async {
            let receiver = new ClockDisposable()
            FrameworkCallbackEntryPoints.Dispose receiver
            return receiver.Count
        })

    static member pureFormat =
        Convex.query (fun _ (_: unit) -> async {
            return Int32.Parse(FrameworkCallbackEntryPoints.Format(PureDisplay()))
        })

    static member clockFormat =
        Convex.query (fun _ (_: unit) -> async {
            return Int32.Parse(FrameworkCallbackEntryPoints.Format(ClockDisplay()))
        })

    static member pureCollection =
        Convex.query (fun _ (_: unit) -> async {
            let receiver = PureCollection()
            FrameworkCallbackEntryPoints.Add receiver
            return receiver[0]
        })

    static member clockCollection =
        Convex.query (fun _ (_: unit) -> async {
            let receiver = ClockCollection()
            FrameworkCallbackEntryPoints.Add receiver
            return receiver[0]
        })

    static member stableComparer =
        Convex.query (fun _ (_: unit) -> async {
            let values = Dictionary<string, int>(StableTextComparer())
            values.Add("key", 42)
            return values["key"]
        })

    static member clockComparer =
        Convex.query (fun _ (_: unit) -> async {
            let values = Dictionary<string, int>(ClockTextComparer())
            values.Add("key", 42)
            return values["key"]
        })

    static member pureContainsValue =
        Convex.query (fun _ (_: unit) -> async {
            let values = Dictionary<string, PureEqualityValue>()
            values.Add("key", PureEqualityValue(42))
            return if values.ContainsValue(PureEqualityValue(42)) then 42 else 0
        })

    static member clockContainsValue =
        Convex.query (fun _ (_: unit) -> async {
            let values = Dictionary<string, ClockEqualityValue>()
            values.Add("key", ClockEqualityValue(42))
            return if values.ContainsValue(ClockEqualityValue(42)) then 42 else 0
        })

    static member pureContainsPair =
        Convex.query (fun _ (_: unit) -> async {
            let values = Dictionary<string, PureEqualityValue>()
            values.Add("key", PureEqualityValue(42))
            return if FrameworkCallbackEntryPoints.ContainsPair(values, PureEqualityValue(42)) then 42 else 0
        })

    static member clockContainsPair =
        Convex.query (fun _ (_: unit) -> async {
            let values = Dictionary<string, ClockEqualityValue>()
            values.Add("key", ClockEqualityValue(42))
            return if FrameworkCallbackEntryPoints.ContainsPair(values, ClockEqualityValue(42)) then 42 else 0
        })

    static member pureRemovePair =
        Convex.query (fun _ (_: unit) -> async {
            let values = Dictionary<string, PureEqualityValue>()
            values.Add("key", PureEqualityValue(42))
            return if FrameworkCallbackEntryPoints.RemovePair(values, PureEqualityValue(42)) then 42 else 0
        })

    static member clockRemovePair =
        Convex.query (fun _ (_: unit) -> async {
            let values = Dictionary<string, ClockEqualityValue>()
            values.Add("key", ClockEqualityValue(42))
            return if FrameworkCallbackEntryPoints.RemovePair(values, ClockEqualityValue(42)) then 42 else 0
        })

    static member decodedArgument =
        Convex.query (fun _ (input: PureDecodedInput) -> async {
            return Int32.Parse(FrameworkCallbackEntryPoints.Format input)
        })

    static member clockDecodedArgument =
        Convex.query (fun _ (input: ClockDecodedInput) -> async {
            return Int32.Parse(FrameworkCallbackEntryPoints.Format input)
        })

    static member storedRecord =
        Convex.query (fun _ (_: unit) -> async {
            let record = { Value = 42 }
            let values = ResizeArray<UnusedHashRecord>()
            values.Add record
            let indexed = Dictionary<string, UnusedHashRecord>()
            indexed.Add("key", values[0])
            let projected = System.Linq.Enumerable.ToDictionary(values, Func<UnusedHashRecord, string>(fun _ -> "projected"))
            return indexed["key"].Value + projected["projected"].Value
        })

    static member hashedRecord =
        Convex.query (fun _ (_: unit) -> async {
            let values = Dictionary<UnusedHashRecord, int>()
            values.Add({ Value = 42 }, 42)
            return values.Count
        })

    static member capturedGuidFormatter =
        let receiver = GenericFormatter<Guid>(42) :> IFormattable
        Convex.query (fun _ (_: unit) -> async {
            return Int32.Parse(receiver.ToString(null, Globalization.CultureInfo.InvariantCulture))
        })

    static member pureFrozenVirtual =
        Convex.query (fun _ (_: unit) -> async {
            return FrozenVirtualEntryPoint.Read(PureFrozenVirtual())
        })

    static member clockFrozenVirtual =
        Convex.query (fun _ (_: unit) -> async {
            return FrozenVirtualEntryPoint.Read(ClockFrozenVirtual())
        })

    static member importClock =
        let value = FrameworkCallbackEntryPoints.Format(ClockDisplay())
        Convex.query (fun _ (_: unit) -> async { return Int32.Parse value })

    static member retainedActionResource =
        let receiver = new ClockDisposable()
        let pending = async {
            use _owned = receiver :> IDisposable
            return 42
        }
        Convex.action (fun _ (_: unit) -> pending)

    static member retainedQueryResource =
        let receiver = new ClockDisposable()
        let pending = async {
            use _owned = receiver :> IDisposable
            return 42
        }
        Convex.query (fun _ (_: unit) -> pending)

    static member taskValue =
        Convex.query (fun _ (_: unit) -> async {
            let completion = TaskCompletionSource<int>()
            completion.SetResult 42
            return! Async.AwaitTask completion.Task
        })

    static member pureValueTaskSource =
        Convex.query (fun _ (_: unit) -> async {
            let value = ValueTask<int>(PureValueTaskSource(), 0s)
            return! Async.AwaitTask(value.AsTask())
        })

    static member clockValueTaskSource =
        Convex.query (fun _ (_: unit) -> async {
            let value = ValueTask<int>(ClockValueTaskSource(), 0s)
            return! Async.AwaitTask(value.AsTask())
        })

    static member queryTaskId =
        Convex.query (fun _ (_: unit) -> async {
            let completion = TaskCompletionSource<int>()
            completion.SetResult 42
            return completion.Task.Id
        })

    static member mutationTaskId =
        Convex.mutation (fun _ (_: unit) -> async {
            let completion = TaskCompletionSource<int>()
            completion.SetResult 42
            return completion.Task.Id
        })

    static member actionTaskId =
        Convex.action (fun _ (_: unit) -> async {
            let completion = TaskCompletionSource<int>()
            completion.SetResult 42
            return completion.Task.Id
        })

    static member queryCurrentTaskId =
        Convex.query (fun _ (_: unit) -> async {
            return Task.CurrentId.GetValueOrDefault()
        })

    static member queryTaskStatus =
        Convex.query (fun _ (_: unit) -> async {
            let completion = TaskCompletionSource<int>()
            return int completion.Task.Status
        })

    static member queryAwaiterState =
        Convex.query (fun _ (_: unit) -> async {
            let completion = TaskCompletionSource<int>()
            return if completion.Task.GetAwaiter().IsCompleted then 1 else 0
        })

    static member queryValueTaskState =
        Convex.query (fun _ (_: unit) -> async {
            let completion = TaskCompletionSource<int>()
            return if ValueTask<int>(completion.Task).IsCompleted then 1 else 0
        })

    static member queryConfiguredAwaiterState =
        Convex.query (fun _ (_: unit) -> async {
            let completion = TaskCompletionSource<int>()
            return if completion.Task.ConfigureAwait(false).GetAwaiter().IsCompleted then 1 else 0
        })

    static member queryAwaiterStateInsideTask =
        Convex.queryTask (fun _ (_: unit) -> task {
            let! value = Task.FromResult 42
            return if Task.FromResult(value).GetAwaiter().IsCompleted then value else 0
        })

    static member queryAwaiterOnCompleted =
        Convex.query (fun _ (_: unit) -> async {
            Task.FromResult(42).GetAwaiter().OnCompleted(Action(fun () -> ()))
            return 42
        })

    static member queryAwaiterUnsafeOnCompleted =
        Convex.query (fun _ (_: unit) -> async {
            Task.FromResult(42).GetAwaiter().UnsafeOnCompleted(Action(fun () -> ()))
            return 42
        })

    static member queryParallelRace =
        Convex.query (fun _ (_: unit) -> async {
            let mutable observed = 0
            do! Async.Parallel [| async { observed <- 1 }; async { observed <- 2 } |] |> Async.Ignore
            return observed
        })

    static member queryChoiceRace =
        Convex.query (fun _ (_: unit) -> async {
            let! observed = Async.Choice [| async { return Some 1 }; async { return Some 2 } |]
            return defaultArg observed 0
        })

    static member querySwitchThread =
        Convex.query (fun _ (_: unit) -> async {
            do! Async.SwitchToThreadPool()
            return 42
        })

    static member actionAwaiterState =
        Convex.action (fun _ (_: unit) -> async {
            return if Task.FromResult(42).GetAwaiter().IsCompleted then 42 else 0
        })
