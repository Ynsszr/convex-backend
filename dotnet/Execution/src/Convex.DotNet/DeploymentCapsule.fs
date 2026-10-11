namespace Convex.DotNet.Execution

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Convex.DotNet

[<RequireQualifiedAccess>]
type NativeModuleKind = Functions | Schema | Auth | Http | Crons | Component

/// Durable Convex addresses are independent of CLR module/property names.
type FunctionAlias = {functionPath:string;entryPoint:string}

/// Filesystem paths are local build inputs only. The uploaded capsule contains
/// frozen bytes and digests; admission selects its own storage/cache paths.
module DeploymentCapsule =
    let private maximumArtifact=8*1024*1024
    let private maximumAggregate=10*1024*1024
    let private kindName = function
        | NativeModuleKind.Functions -> "functions" | NativeModuleKind.Schema -> "schema"
        | NativeModuleKind.Auth -> "auth" | NativeModuleKind.Http -> "http"
        | NativeModuleKind.Crons -> "crons" | NativeModuleKind.Component -> "component"

    let createWithAliases assemblyPath dependencyPaths kind selectedExport definition (aliases:FunctionAlias array) =
        if Array.length dependencyPaths>64 then invalidArg "dependencyPaths" "At most 64 capsule dependencies are supported"
        if aliases.Length>1000 then invalidArg "aliases" "A capsule has at most 1,000 durable function bindings"
        if aliases.Length>0 && kind<>NativeModuleKind.Functions && kind<>NativeModuleKind.Http then
            invalidArg "aliases" "Only functions and HTTP capsules can bind durable function addresses"
        let mutable identities=Set.empty
        let validIdentity (value:string) =
            not(String.IsNullOrWhiteSpace value) && value.Length<=512 && not(value.StartsWith("_system/",StringComparison.Ordinal)) &&
            value.IndexOf(':')>0 && value.IndexOf(':')=value.LastIndexOf(':') && not(value.EndsWith(':')) &&
            not(value |> Seq.exists Char.IsControl)
        for alias in aliases do
            if not(validIdentity alias.functionPath && validIdentity alias.entryPoint) || identities.Contains alias.functionPath then
                invalidArg "aliases" "Durable function bindings require unique exact addresses and CLR entry points"
            if alias.functionPath[..alias.functionPath.IndexOf(':')-1].EndsWith(".js",StringComparison.Ordinal) then
                invalidArg "aliases" "Durable function addresses omit module extensions"
            identities<-identities.Add alias.functionPath
        let mutable total=0
        let mutable names=Set.empty
        let artifact path =
            let info=FileInfo(path: string)
            let name=info.Name
            if info.LinkTarget<>null || not info.Exists || info.Length<=0L || info.Length>int64 maximumArtifact ||
               not(name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)) || name="." || name=".." then
                invalidArg "path" "A capsule requires a bounded regular DLL artifact"
            if names.Contains(name.ToLowerInvariant()) then invalidArg "dependencyPaths" "Capsule artifact names must be unique"
            names<-names.Add(name.ToLowerInvariant())
            use input=info.OpenRead()
            use frozen=new MemoryStream()
            let buffer=Array.zeroCreate<byte> 65536
            let mutable reading=true
            while reading do
                let count=input.Read(buffer,0,buffer.Length)
                if count=0 then reading<-false
                else
                    if frozen.Length+int64 count>int64 maximumArtifact then invalidArg "path" "Capsule artifact exceeded its byte bound"
                    frozen.Write(buffer,0,count)
            let bytes=frozen.ToArray()
            total<-total+bytes.Length
            if total>maximumAggregate then invalidArg "dependencyPaths" "Capsule artifacts exceed the 10 MiB aggregate bound"
            Convex.Runtime.RuntimeJson.objectValue [
                "name",box name;"sha256",box (SHA256.HashData bytes |> Convert.ToHexStringLower)
                "bytes",box (Convert.ToBase64String bytes)
            ]
        match kind,selectedExport,definition with
        | NativeModuleKind.Functions,None,None -> ()
        | NativeModuleKind.Functions,_,_ -> invalidArg "selectedExport" "A functions capsule has null export and definition"
        | (NativeModuleKind.Auth | NativeModuleKind.Component),Some export,None when not(String.IsNullOrWhiteSpace export) && export.Contains(':') -> ()
        | _,Some export,Some _ when not(String.IsNullOrWhiteSpace export) && export.Contains(':') -> ()
        | _ -> invalidArg "selectedExport" "A declaration capsule selects an exact native export and definition"
        let primary=artifact assemblyPath
        let dependencies=dependencyPaths |> Array.map artifact
        let value=Convex.Runtime.RuntimeJson.objectValue [
            "format",box "convex-dotnet-capsule";"version",box 1;"moduleKind",box (kindName kind)
            "export",(selectedExport |> Option.map box |> Option.defaultValue null)
            "definition",(definition |> Option.map (fun (value: JsonElement) -> box value) |> Option.defaultValue null)
            "assembly",primary;"assemblyDependencies",box dependencies
            "functionAliases",box (aliases |> Array.map(fun alias -> Convex.Runtime.RuntimeJson.objectValue [
                "functionPath",box alias.functionPath;"entryPoint",box alias.entryPoint]))
        ]
        let bytes=InvocationProtocol.encode value
        if bytes.Length>16*1024*1024 then invalidOp "Encoded capsule exceeds its source bound"
        Encoding.UTF8.GetString bytes

    let create assemblyPath dependencyPaths kind selectedExport definition =
        createWithAliases assemblyPath dependencyPaths kind selectedExport definition [||]
