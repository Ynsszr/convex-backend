namespace RuntimeFixture

open Convex.FSharp

type IAdmissionAuthority =
    abstract Revision: int

type private ConstantAdmissionAuthority() =
    interface IAdmissionAuthority with
        member _.Revision = 7

type private ClockAdmissionAuthority() =
    interface IAdmissionAuthority with
        member _.Revision = System.DateTime.UtcNow.Day

type private IAdmissionThunk =
    abstract Run: unit -> int64

type private EagerClockAdmissionThunk() =
    interface IAdmissionThunk with
        member _.Run() =
            [| 0 |] |> Array.map (fun _ -> System.DateTime.UtcNow.Ticks) |> Array.head

type private IUnresolvedInitializationAuthority =
    abstract Revision: int

type private DeferredAdmissionInitialization private () =
    static let revision =
        // A test-owned registry scope observes this harmless effect if admission
        // accidentally evaluates a static before resolving its import authority.
        TableRegistry.register typeof<System.Collections.Generic.KeyValuePair<System.Guid, decimal>> "initializationExecuted"
        let authority = Unchecked.defaultof<IUnresolvedInitializationAuthority>
        authority.Revision
    static member Revision = revision

type NativeLazyInitializationFixture private () =
    static member read =
        Convex.query (fun _ () -> async { return DeferredAdmissionInitialization.Revision })

/// The receiver is constructed in a deeper deferred async continuation than
/// its call site. Both are frozen, but a single breadth-first pass is incomplete.
type NativeDispatchFixtures private () =
    static member closed =
        let eager = [| 1 |] |> Array.map (fun value -> value + 1)
        Convex.query (fun _ (_: unit) -> async {
            let! authority = async {
                let! _ = async { return () }
                let! _ = async { return () }
                return { new IAdmissionAuthority with member _.Revision = 7 }
            }
            return authority.Revision + eager[0]
        })

    static member openTarget =
        let eager = [| 1 |] |> Array.map (fun value -> value + 1)
        Convex.query (fun _ (_: unit) -> async {
            let authority = Unchecked.defaultof<IAdmissionAuthority>
            return authority.Revision + eager[0]
        })

    /// Dispatch review first reaches Run, which then reveals an eager callback.
    /// The callback must be reviewed before the exported getter is evaluated.
    static member eagerInterfaceClock: obj =
        let value = (EagerClockAdmissionThunk() :> IAdmissionThunk).Run()
        Convex.query (fun _ (_: unit) -> async { return value })

/// Resolve these getters in order through one DeploymentAssembly. An admitted
/// interface target does not permit a later, distinct receiver to skip review.
type NativeSequentialDispatchFixtures private () =
    static member first =
        let value = (ConstantAdmissionAuthority() :> IAdmissionAuthority).Revision
        Convex.query (fun _ (_: unit) -> async { return value })

    static member laterClock =
        let value = (ClockAdmissionAuthority() :> IAdmissionAuthority).Revision
        Convex.query (fun _ (_: unit) -> async { return value })
