#r "../../Runtime/src/Convex.Runtime/bin/Debug/net10.0/Convex.Runtime.dll"
#r "../../FSharp/src/Convex.FSharp/bin/Debug/net10.0/Convex.FSharp.dll"
#r "../src/Convex.DotNet/bin/Debug/net10.0/Convex.DotNet.dll"
#r "ComponentFixture/bin/Debug/net10.0/ComponentFixture.dll"

open System
open System.IO
open System.Text.Json
open Convex.FSharp
open Convex.DotNet.Execution

let package=Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__,".."))
let assembly=Path.Combine(package,"tests/ComponentFixture/bin/Debug/net10.0/ComponentFixture.dll")
let directory=Path.Combine(package,"artifacts/native-component-capsules")
Directory.CreateDirectory directory |> ignore
let write file kind selected definition aliases =
    File.WriteAllText(Path.Combine(directory,file),DeploymentCapsule.createWithAliases assembly [||] kind selected definition aliases)
let declaration file selected (value:NativeComponentDefinition) =
    use definition=JsonDocument.Parse(value.Export())
    write file NativeModuleKind.Component (Some selected) (Some definition.RootElement) [||]
declaration "app.js" "RuntimeFixture.NativeDefinitions:App" RuntimeFixture.NativeDefinitions.App
declaration "child.js" "RuntimeFixture.NativeDefinitions:Child" RuntimeFixture.NativeDefinitions.Child
declaration "child-declared-environment.js" "RuntimeFixture.NativeDefinitions:ChildWithDeclaredEnvironment" RuntimeFixture.NativeDefinitions.ChildWithDeclaredEnvironment
for file,selected in [
    "app-environment.js","AppWithEnvironment"
    "child-environment.js","ChildWithEnvironment"
    "app-clock.js","AppWithClock"
    "app-random.js","AppWithRandom"
] do write file NativeModuleKind.Component (Some("RuntimeFixture.NativeDefinitions:"+selected)) None [||]
let aliases=[|
    {functionPath="counter:read";entryPoint="NativeProbe:read"}
    {functionPath="counter:increment";entryPoint="NativeProbe:increment"}
    {functionPath="counter:environment";entryPoint="NativeProbe:environment"}
|]
write "counter.js" NativeModuleKind.Functions None None aliases
let packageSchema() =
    use schema=JsonDocument.Parse(NativeProbe.schema.Export())
    write "schema.js" NativeModuleKind.Schema (Some "NativeProbe:schema") (Some schema.RootElement) [||]
packageSchema()
printfn "Native component proof capsules: %s" directory
