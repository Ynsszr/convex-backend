namespace Convex.DotNet.Development

open System
open System.IO
open System.Reflection
open System.Security
open System.Text.Json
open System.Text.RegularExpressions
open System.Diagnostics

/// Stable installed release locations replace knowledge of a source checkout.
module ReleaseLocation =
    let version () =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion.Split('+')[0]
    let directory () =
        match Environment.GetEnvironmentVariable "CONVEX_FSHARP_RELEASE" with
        | value when not(String.IsNullOrWhiteSpace value) -> Path.GetFullPath value
        | _ -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"convex-fsharp","releases",version())
    let runtime () = Path.Combine(directory(),"runtime")
    let private run directory arguments =
        let info=ProcessStartInfo("dotnet",WorkingDirectory=directory,UseShellExecute=false)
        for arg:string in arguments do info.ArgumentList.Add arg
        use child=Process.Start info
        if not(child.WaitForExit(180000)) then child.Kill(true);child.WaitForExit();invalidOp "Project creation command timed out."
        if child.ExitCode<>0 then invalidOp "Project creation command failed."
    let private createFromTemplate (templateName:string) (isComponent:bool) (name:string) (destination:string) =
        if not(Regex.IsMatch(name,@"^[A-Za-z_][A-Za-z0-9_]*$")) then invalidArg "name" "Use an F# identifier for the project name."
        let destination=Path.GetFullPath destination
        if Directory.Exists destination || File.Exists destination then invalidArg "destination" "Choose a new application directory."
        let release=directory()
        let selectedVersion=version()
        let manifest=Path.Combine(release,"release.json")
        if File.Exists(Path.Combine(release,".incomplete")) || not(File.Exists manifest) then
            invalidOp "Install a complete Convex-for-F# release before creating a project."
        use descriptor=JsonDocument.Parse(File.ReadAllBytes manifest)
        if descriptor.RootElement.GetProperty("format").GetString()<>"convex-fsharp-release" ||
           descriptor.RootElement.GetProperty("version").GetString()<>selectedVersion then
            invalidOp "The installed release does not match this CLI version."
        let feed=Path.Combine(release,"packages")
        let template=Path.Combine(feed,"Convex.Templates."+selectedVersion+".nupkg")
        if not(File.Exists template) then invalidOp "Install the matching Convex-for-F# release before creating a project."
        Directory.CreateDirectory destination |> ignore
        let localHome=Path.Combine(destination,".convex","template-home")
        // Template installation stays inside this application's temporary CLI home.
        let invoke args =
            let info=ProcessStartInfo("dotnet",WorkingDirectory=destination,UseShellExecute=false)
            info.Environment["DOTNET_CLI_HOME"]<-localHome
            info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]<-"true"
            for arg:string in args do info.ArgumentList.Add arg
            use child=Process.Start info
            if not(child.WaitForExit(180000)) then child.Kill(true);child.WaitForExit();invalidOp "Template command timed out."
            if child.ExitCode<>0 then invalidOp "Native template creation failed."
        try
            invoke ["new";"install";template]
            invoke ["new";templateName;"-n";name;"-o";destination;"--force"]
            let config="<configuration><packageSources><clear/><add key=\"convex-fsharp\" value=\""+SecurityElement.Escape feed+
                       "\"/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources><packageSourceMapping>"+
                       "<packageSource key=\"convex-fsharp\"><package pattern=\"Convex.*\"/></packageSource>"+
                       "<packageSource key=\"nuget.org\"><package pattern=\"*\"/></packageSource></packageSourceMapping></configuration>"
            File.WriteAllText(Path.Combine(destination,"NuGet.Config"),config)
            run destination ["tool";"restore";"--configfile";"NuGet.Config"]
            printfn "Created %s using installed Convex-for-F# %s." destination selectedVersion
            if isComponent then printfn "Mount this component from a parent application's F# declaration and manifest, then build the parent graph."
            else
                printfn "Backend: dotnet convex-dotnet dev convex.project.json"
                printfn "Browser: dotnet run --project Web/%s.Web.fsproj" name
        finally
            if Directory.Exists localHome then Directory.Delete(localHome,true)

    let create name destination = createFromTemplate "convex-fsharp" false name destination
    let createComponent name destination = createFromTemplate "convex-fsharp-component" true name destination
