namespace Convex.DotNet.Execution

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions

/// Verify a complete runtime distribution before copying files or starting a process.
/// Hashes establish internal consistency, not trust in a publisher or behavioral parity.
module RuntimeBundle =
    type Verified = private {
        Directory: string
        Version: string
        RuntimeIdentifier: string
        BackendPath: string
        HostDirectory: string
        CliPath: string
    }

    let version bundle = bundle.Version
    let runtimeIdentifier bundle = bundle.RuntimeIdentifier
    let backendPath bundle = bundle.BackendPath
    let hostDirectory bundle = bundle.HostDirectory
    let cliPath bundle = bundle.CliPath

    let private noLinks (path: string) =
        let mutable current = Path.GetFullPath path
        while not (String.IsNullOrEmpty current) do
            let info: FileSystemInfo =
                if Directory.Exists current then DirectoryInfo current else FileInfo current
            if info.LinkTarget <> null || (info.Exists && info.Attributes.HasFlag FileAttributes.ReparsePoint) then
                invalidOp "Runtime artifacts cannot traverse symbolic links."
            current <- Path.GetDirectoryName current

    let private property name (value: JsonElement) =
        match value.TryGetProperty(name: string) with
        | true, field -> field
        | _ -> invalidOp ("Missing runtime descriptor field: " + name)

    let private text name value =
        let field = property name value
        if field.ValueKind <> JsonValueKind.String then invalidOp ("Invalid runtime descriptor field: " + name)
        let result = field.GetString()
        if String.IsNullOrWhiteSpace result || result.Length > 4096 || Seq.exists Char.IsControl result then
            invalidOp ("Invalid runtime descriptor text: " + name)
        result

    let private unique (value: JsonElement) =
        let rec visit (item: JsonElement) =
            match item.ValueKind with
            | JsonValueKind.Object ->
                let names = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                for field in item.EnumerateObject() do
                    if not (names.Add field.Name) then invalidOp "Duplicate runtime descriptor field."
                    visit field.Value
            | JsonValueKind.Array -> for child in item.EnumerateArray() do visit child
            | _ -> ()
        visit value

    let private relativePath root (relative: string) =
        if Path.IsPathRooted relative || relative.Contains('\\') || relative.Contains(':') ||
           relative.Split('/') |> Array.exists (fun part -> part = "" || part = "." || part = "..") then
            invalidOp "Runtime inventory contains a nonportable path."
        let absolute = Path.GetFullPath(relative, root)
        if not (absolute.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.Ordinal)) then
            invalidOp "Runtime inventory escapes its distribution."
        noLinks absolute
        absolute

    let private verifyElf rid path =
        use input = File.OpenRead path
        let header = Array.zeroCreate<byte> 20
        if input.Read(header, 0, header.Length) <> header.Length ||
           header[0..3] <> [|0x7fuy; 0x45uy; 0x4cuy; 0x46uy|] || header[4] <> 2uy || header[5] <> 1uy then
            invalidOp "A native Linux runtime requires a 64-bit little-endian ELF backend."
        let machine = int header[18] + 256 * int header[19]
        let expected = if rid = "linux-x64" then 62 else 183
        if machine <> expected then invalidOp "Backend architecture does not match the runtime identifier."
        let kind = int header[16] + 256 * int header[17]
        if kind <> 2 && kind <> 3 then invalidOp "Backend ELF is not an executable."

    /// Validate descriptor, complete inventory, links, file bytes and target architecture.
    /// This does not execute the backend, load handlers, or create any output directory.
    let read (directory: string) =
        let root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
        noLinks root
        if File.Exists(Path.Combine(root, ".incomplete")) then invalidOp "Runtime publication is incomplete."
        let descriptor = Path.Combine(root, "runtime.json")
        noLinks descriptor
        let info = FileInfo descriptor
        if not info.Exists || info.Length <= 0L || info.Length > 1024L * 1024L then
            invalidOp "Select a bounded runtime descriptor."
        use document = JsonDocument.Parse(File.ReadAllBytes descriptor, JsonDocumentOptions(MaxDepth = 16))
        let value = document.RootElement
        unique value
        if text "format" value <> "convex-native-runtime" then invalidOp "Select a complete native Convex runtime distribution."
        let inventory =
            match value.TryGetProperty "files" with
            | true, entries when entries.ValueKind = JsonValueKind.Array -> entries
            | _ -> invalidOp "A runtime distribution must provide its file digest inventory."
        if inventory.GetArrayLength() = 0 || inventory.GetArrayLength() > 1024 then
            invalidOp "Runtime file digest inventory must contain 1..1024 entries."
        let version = text "version" value
        let rid = text "rid" value
        if rid <> "linux-x64" && rid <> "linux-arm64" then invalidOp "Unsupported native runtime identifier."
        let backend = text "backend" value
        if backend <> "backend/convex-local-backend" || text "host" value <> "host/Convex.DotNet.Host.dll" ||
           text "cli" value <> "cli/Convex.DotNet.Cli.dll" then
            invalidOp "Runtime entry points must use the distribution contract paths."
        let expected = Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        let mutable total = 0L
        for entry in inventory.EnumerateArray() do
            let name = text "path" entry
            let digest = text "sha256" entry
            if name = "runtime.json" || name = ".incomplete" || not (Regex.IsMatch(digest, "\\A[0-9a-f]{64}\\z")) then
                invalidOp "Invalid runtime inventory entry."
            if not (expected.TryAdd(name, digest)) then invalidOp "Duplicate runtime inventory path."
            let absolute = relativePath root name
            let file = FileInfo absolute
            let limit = if name = backend then 512L * 1024L * 1024L else 64L * 1024L * 1024L
            if not file.Exists || file.Length > limit then invalidOp "Missing or oversized runtime artifact."
            total <- total + file.Length
            if total > 1024L * 1024L * 1024L then invalidOp "Runtime artifact inventory exceeds its size bound."
            use stream = file.OpenRead()
            if (SHA256.HashData stream |> Convert.ToHexStringLower) <> digest then invalidOp "Native runtime artifact digest mismatch."
        let required = [
            backend; "host/Convex.DotNet.Host.dll"; "host/Convex.Runtime.dll"; "host/Convex.DotNet.dll"
            "host/Convex.FSharp.dll"; "host/FSharp.Core.dll"; "host/Convex.DotNet.Host.deps.json"
            "host/Convex.DotNet.Host.runtimeconfig.json"; "cli/Convex.DotNet.Cli.dll"
            "cli/Convex.DotNet.Cli.deps.json"; "cli/Convex.DotNet.Cli.runtimeconfig.json"
        ]
        if required |> List.exists (expected.ContainsKey >> not) then
            invalidOp "Runtime inventory does not cover every required entry point and host dependency."
        let actual = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let rec scan depth folder =
            if depth > 16 then invalidOp "Runtime artifact directory nesting exceeds its bound."
            noLinks folder
            for file in Directory.GetFiles folder do
                noLinks file
                if file <> descriptor then
                    actual.Add(Path.GetRelativePath(root, file).Replace('\\', '/')) |> ignore
                    if actual.Count > 1024 then invalidOp "Runtime directory exceeds its file bound."
            for child in Directory.GetDirectories folder do scan (depth + 1) child
        scan 0 root
        if not (actual.SetEquals expected.Keys) then invalidOp "Runtime inventory does not match the complete distribution."
        let backendPath = relativePath root backend
        verifyElf rid backendPath
        { Directory = root; Version = version; RuntimeIdentifier = rid; BackendPath = backendPath
          HostDirectory = Path.Combine(root, "host"); CliPath = Path.Combine(root, "cli/Convex.DotNet.Cli.dll") }

    let requireCurrentPlatform bundle =
        let expected =
            match RuntimeInformation.ProcessArchitecture with
            | Architecture.X64 -> "linux-x64"
            | Architecture.Arm64 -> "linux-arm64"
            | _ -> invalidOp "Unsupported native runtime process architecture."
        if not (OperatingSystem.IsLinux()) || bundle.RuntimeIdentifier <> expected then
            invalidOp "The selected native runtime does not match this machine."
