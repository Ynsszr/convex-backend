// Compatibility entry point. The producer project builds both example backends
// and packages their exact outputs before client contracts are compiled.
open System
open System.IO
open System.Diagnostics

let configuration =
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [||] -> "Release"
    | [|("Debug" | "Release") as selected|] -> selected
    | _ -> invalidArg "arguments" "Expected optional Debug or Release configuration"
let project=Path.Combine(__SOURCE_DIRECTORY__,"PackageExamples/PackageExamples.fsproj")
let info=ProcessStartInfo("dotnet",UseShellExecute=false)
for argument in ["build";project;"-c";configuration;"--nologo"] do info.ArgumentList.Add argument
let build () =
    use child=Process.Start info
    child.WaitForExit()
    if child.ExitCode<>0 then failwithf "Native example packaging failed with exit %d" child.ExitCode
build()
