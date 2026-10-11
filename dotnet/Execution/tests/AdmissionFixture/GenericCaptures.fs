namespace RuntimeFixture

open System
open System.Runtime.CompilerServices
open Convex.FSharp

/// This helper must retain its generic method body. Its optimized async bind
/// emits a singleton continuation on a declaring type closed over 'T.
type private GenericAsyncContinuation private () =
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Ignore<'T>(pending: Async<'T>) : Async<unit> = async {
        let! _ = pending
        return ()
    }

/// Every closed owner has the same metadata field token, but its callback
/// value and admission authority are independent.
type private GenericStaticCallback<'Tag> private () =
    static let mutable callback: unit -> int = fun () -> 0

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Assign(selected: unit -> int) = callback <- selected

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Read() = callback ()

type private IGenericCaptureReceiver<'T> =
    abstract Value: int

type private IntGenericCaptureReceiver() =
    interface IGenericCaptureReceiver<int> with
        member _.Value = 13

type private GenericCaptureReceiver<'T>(value: int) =
    interface IGenericCaptureReceiver<'T> with
        member _.Value = value

type private GenericInterfaceReader private () =
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Read<'T>(receiver: IGenericCaptureReceiver<'T>) = receiver.Value

type private ExpandingGenericCapture private () =
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Recur<'T>() : int = ExpandingGenericCapture.Recur<'T list>()

type NativeGenericCaptureFixtures private () =
    static member asyncClosed =
        Convex.query (fun _ (_: unit) -> async {
            do! GenericAsyncContinuation.Ignore(async { return 41 })
            do! GenericAsyncContinuation.Ignore(async { return "another closed continuation" })
            return 42
        })

    static member first =
        GenericStaticCallback<int>.Assign(fun () -> 7)
        Convex.query (fun _ (_: unit) -> async {
            return GenericStaticCallback<int>.Read()
        })

    static member second =
        GenericStaticCallback<string>.Assign(fun () -> 11)
        Convex.query (fun _ (_: unit) -> async {
            return GenericStaticCallback<string>.Read()
        })

    /// Constructing this callback does not read the clock. Capture admission
    /// must discover and refuse it before a native handler can invoke it.
    static member laterClock =
        GenericStaticCallback<Guid>.Assign(fun () -> DateTime.UtcNow.Day)
        Convex.query (fun _ (_: unit) -> async {
            return GenericStaticCallback<Guid>.Read()
        })

    static member matchingInterface =
        Convex.query (fun _ (_: unit) -> async {
            let receiver = IntGenericCaptureReceiver() :> IGenericCaptureReceiver<int>
            return GenericInterfaceReader.Read receiver
        })

    /// A captured receiver obtains Guid's actual runtime identity through
    /// reflection; its call site names the same type through metadata forwarding.
    static member capturedGuidInterface =
        let receiver = GenericCaptureReceiver<Guid>(17) :> IGenericCaptureReceiver<Guid>
        Convex.query (fun _ (_: unit) -> async {
            return GenericInterfaceReader.Read receiver
        })

    static member mismatchedInterface =
        Convex.query (fun _ (_: unit) -> async {
            let receiver = IntGenericCaptureReceiver() :> IGenericCaptureReceiver<int>
            let unresolved = Unchecked.defaultof<IGenericCaptureReceiver<string>>
            return GenericInterfaceReader.Read receiver + GenericInterfaceReader.Read unresolved
        })

    /// Admission walks this expanding method-instantiation graph without
    /// executing it. It must reject at its finite substituted-shape bound.
    static member expandingContext =
        Convex.query (fun _ (_: unit) -> async {
            return ExpandingGenericCapture.Recur<int>()
        })
