namespace RuntimeFixture

open Convex.FSharp

/// Independently selected negative roots. No positive module initializer
/// constructs these registrations or executes their ambient operations.
type NativeNegative private () =
    static member filesystem: obj = Convex.query(fun _ (_:unit) -> async {return System.IO.File.Exists("/etc/hostname")})
    static member clock: obj = Convex.query(fun _ (_:unit) -> async {return System.DateTime.UtcNow.Ticks})
    static member random: obj = Convex.query(fun _ (_:unit) -> async {return System.Random.Shared.NextDouble()})
    static member environment: obj = Convex.query(fun _ (_:unit) -> async {return System.Environment.GetEnvironmentVariable("PATH")})
    static member reflection = Convex.query(fun _ (_:unit) -> async {return System.Type.GetType("System.Environment").Name})
