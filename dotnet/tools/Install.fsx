open System
open System.IO
open System.Diagnostics
open System.Security
open System.Security.Cryptography
open System.Text.Json

let main () =
    let source=Path.GetFullPath __SOURCE_DIRECTORY__
    let descriptor=Path.Combine(source,"release.json")
    if not(File.Exists descriptor) || FileInfo(descriptor).Length>2L*1024L*1024L then
        invalidOp "Run Install.fsx from an extracted Convex-for-F# release."
    use document=JsonDocument.Parse(File.ReadAllBytes descriptor)
    let value=document.RootElement
    let text name (item:JsonElement)=item.GetProperty(name:string).GetString()
    let version=text "version" value
    if text "format" value<>"convex-fsharp-release" || not(Text.RegularExpressions.Regex.IsMatch(version,@"^[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.]+)?$")) then
        invalidOp "Unsupported Convex-for-F# release."
    if not(OperatingSystem.IsLinux()) then invalidOp "This release requires Linux."
    let architecture=Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
    let expectedRid=match architecture with Runtime.InteropServices.Architecture.X64 -> "linux-x64" | Runtime.InteropServices.Architecture.Arm64 -> "linux-arm64" | _ -> invalidOp "Unsupported runtime architecture."
    use runtimeDescriptor=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(source,"runtime/runtime.json")))
    if text "rid" runtimeDescriptor.RootElement<>expectedRid then invalidOp "Release runtime does not match this machine."
    let arguments=fsi.CommandLineArgs |> Array.skip 1
    let target=
        match arguments with
        | [||] -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"convex-fsharp","releases",version)
        | [|directory|] -> Path.GetFullPath directory
        | _ -> invalidArg "arguments" "Install.fsx accepts at most one explicit destination directory."
    let hash path =
        use stream=File.OpenRead path
        SHA256.HashData stream |> Convert.ToHexStringLower
    let noLinks path =
        let mutable current=Path.GetFullPath path
        while not(String.IsNullOrEmpty current) do
            let info:FileSystemInfo=if Directory.Exists current then DirectoryInfo current else FileInfo current
            if info.LinkTarget<>null || (info.Exists && info.Attributes.HasFlag FileAttributes.ReparsePoint) then invalidOp "Release installation cannot traverse symbolic links."
            current<-Path.GetDirectoryName current
    noLinks source;noLinks target
    let files=value.GetProperty("files").EnumerateArray() |> Seq.toArray
    if files.Length=0 || files.Length>2048 then invalidOp "Release inventory exceeds its bound."
    let names=Collections.Generic.HashSet<string>(StringComparer.Ordinal)
    let mutable total=0L
    for item in files do
        let name=text "path" item
        if Path.IsPathRooted name || name.Contains('\\') || name.Split('/') |> Array.exists(fun part->part="" || part="." || part="..") || not(names.Add name) then invalidOp "Invalid release inventory path."
        let path=Path.Combine(source,name)
        noLinks path
        if not(File.Exists path) || FileInfo(path).Length>512L*1024L*1024L then invalidOp "Missing or oversized release file."
        total<-total+FileInfo(path).Length
        if total>1536L*1024L*1024L || hash path<>text "sha256" item then invalidOp "Release file digest mismatch."
    let actual=Collections.Generic.HashSet<string>(StringComparer.Ordinal)
    let rec collect depth directory =
        if depth>16 then invalidOp "Release directory nesting exceeds its bound."
        noLinks directory
        for file in Directory.GetFiles directory do
            noLinks file
            if file<>descriptor then
                actual.Add(Path.GetRelativePath(source,file).Replace('\\','/')) |> ignore
                if actual.Count>2048 then invalidOp "Release directory exceeds its file bound."
        for child in Directory.GetDirectories directory do collect (depth+1) child
    collect 0 source
    if not(names.SetEquals actual) then invalidOp "Release contains unlisted or missing files."
    let installed=Path.Combine(target,"release.json")
    if Directory.Exists target then
        if File.Exists installed && hash installed=hash descriptor && not(File.Exists(Path.Combine(target,".incomplete"))) then
            for item in files do
                let path=Path.Combine(target,text "path" item)
                noLinks path
                if not(File.Exists path) || hash path<>text "sha256" item then invalidOp "Installed release files have changed."
            if not(File.Exists(Path.Combine(target,"bin/convex-dotnet"))) then invalidOp "The installed CLI is missing."
            printfn "Already installed and verified: %s" target
        else invalidOp "Destination already exists with different or incomplete release contents."
    else
        let staging=target+".staging-"+Guid.NewGuid().ToString("N")
        Directory.CreateDirectory(staging,UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute) |> ignore
        let mutable committed=false
        try
            try
                for item in files do
                    let name=text "path" item
                    let original=Path.Combine(source,name)
                    let destination=Path.Combine(staging,name)
                    Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
                    File.Copy(original,destination,false)
                    if OperatingSystem.IsLinux() then File.SetUnixFileMode(destination,File.GetUnixFileMode original)
                    if hash destination<>text "sha256" item then invalidOp "Release changed while copying."
                File.Copy(descriptor,Path.Combine(staging,"release.json"))
                File.WriteAllText(Path.Combine(staging,".incomplete"),"Tool installation in progress.")
                Directory.Move(staging,target)
                committed<-true
                let config=Path.Combine(target,"NuGet.Config")
                File.WriteAllText(config,"<configuration><packageSources><clear/><add key=\"release\" value=\""+SecurityElement.Escape(Path.Combine(target,"packages"))+"\"/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources><packageSourceMapping><packageSource key=\"release\"><package pattern=\"Convex.*\"/></packageSource><packageSource key=\"nuget.org\"><package pattern=\"*\"/></packageSource></packageSourceMapping></configuration>")
                let info=ProcessStartInfo("dotnet",UseShellExecute=false,WorkingDirectory=target)
                for argument in ["tool";"install";"Convex.DotNet.Cli";"--version";version;"--tool-path";Path.Combine(target,"bin");"--configfile";config] do info.ArgumentList.Add argument
                use child=Process.Start info
                if not(child.WaitForExit(180000)) then child.Kill(true);child.WaitForExit();invalidOp "Tool installation timed out."
                if child.ExitCode<>0 then invalidOp "The native CLI could not be installed."
                File.Delete(Path.Combine(target,".incomplete"))
                printfn "Installed Convex-for-F# %s: %s" version target
            with _ ->
                if committed && Directory.Exists target then Directory.Delete(target,true)
                reraise()
        finally
            if Directory.Exists staging then Directory.Delete(staging,true)
    printfn "Create an application: %s new MyApplication" (Path.Combine(target,"bin","convex-dotnet"))
    printfn "No Convex source checkout is required. .NET 10 and Linux bubblewrap remain runtime prerequisites."

main()
