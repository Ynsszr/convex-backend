namespace RuntimeFixture

open System
open System.Runtime.CompilerServices
open Convex.FSharp

// Proposal only: optimized AdmissionFixture source, not executed or registered.
type private StructureStaticCallback<'Tag> private () =
    static let mutable callback: unit -> int = fun () -> 0
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Assign(selected: unit -> int) = callback <- selected
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Read() = callback ()

type private StructureMutableCapture<'Tag>(initial: unit -> int) =
    let mutable callback = initial
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    member _.Assign(selected: unit -> int) = callback <- selected
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    member _.Read() = callback ()

type NativeReflectionStructureFixtures private () =
    static member warmStatic =
        StructureStaticCallback<int>.Assign(fun () -> 7)
        Convex.query (fun _ (_: unit) -> async { return StructureStaticCallback<int>.Read() })

    static member mutateSameStatic =
        StructureStaticCallback<int>.Assign(fun () -> DateTime.UtcNow.Day)
        Convex.query (fun _ (_: unit) -> async { return StructureStaticCallback<int>.Read() })

    static member independentString =
        StructureStaticCallback<string>.Assign(fun () -> 11)
        Convex.query (fun _ (_: unit) -> async { return StructureStaticCallback<string>.Read() })

    static member warmInstance =
        let captured = StructureMutableCapture<int>(fun () -> 13)
        Convex.query (fun _ (_: unit) -> async { return captured.Read() })

    static member mutateSameInstanceType =
        let captured = StructureMutableCapture<int>(fun () -> 0)
        captured.Assign(fun () -> DateTime.UtcNow.Day)
        Convex.query (fun _ (_: unit) -> async { return captured.Read() })

    // These two are deliberately adjacent. The test checks actual property order
    // before using Declarations: catalogue sorting occurs AFTER getter evaluation.
    static member badFirst =
        StructureStaticCallback<decimal>.Assign(fun () -> DateTime.UtcNow.Day)
        Convex.query (fun _ (_: unit) -> async { return StructureStaticCallback<decimal>.Read() })

    static member repairLater =
        TableRegistry.register typeof<System.Collections.Generic.KeyValuePair<Guid, decimal>> "laterCaptureRepairExecuted"
        StructureStaticCallback<decimal>.Assign(fun () -> 17)
        Convex.query (fun _ (_: unit) -> async { return StructureStaticCallback<decimal>.Read() })

    static member warmGuid =
        StructureStaticCallback<Guid>.Assign(fun () -> 19)
        Convex.query (fun _ (_: unit) -> async { return StructureStaticCallback<Guid>.Read() })

    static member actionClock =
        StructureStaticCallback<Guid>.Assign(fun () -> DateTime.UtcNow.Day)
        Convex.action (fun _ (_: unit) -> async { return StructureStaticCallback<Guid>.Read() })

    static member deterministicAfterAction =
        Convex.query (fun _ (_: unit) -> async { return StructureStaticCallback<Guid>.Read() })
