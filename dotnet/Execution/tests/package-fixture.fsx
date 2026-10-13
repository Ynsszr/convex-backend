#if CONVEX_RELEASE
#r "../../Runtime/src/Convex.Runtime/bin/Release/net10.0/Convex.Runtime.dll"
#r "../../FSharp/src/Convex.FSharp/bin/Release/net10.0/Convex.FSharp.dll"
#r "../src/Convex.DotNet/bin/Release/net10.0/Convex.DotNet.dll"
#else
#r "../../Runtime/src/Convex.Runtime/bin/Debug/net10.0/Convex.Runtime.dll"
#r "../../FSharp/src/Convex.FSharp/bin/Debug/net10.0/Convex.FSharp.dll"
#r "../src/Convex.DotNet/bin/Debug/net10.0/Convex.DotNet.dll"
#endif

#load "NativeFixtureCapsules.fs"

open System
open System.IO
open Convex.DotNet.TestFixtures

let configuration =
#if CONVEX_RELEASE
    "Release"
#else
    "Debug"
#endif

let package=Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let assembly=Path.Combine(package,"tests/RuntimeFixture/bin",configuration,"net10.0/RuntimeFixture.dll")
let directory =
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [||] -> Path.Combine(package,"artifacts/native-capsules")
    | [|selected|] -> Path.GetFullPath selected
    | _ -> invalidArg "arguments" "Expected optional fixture capsule output directory"
NativeFixtureCapsules.packageFixture configuration assembly directory
