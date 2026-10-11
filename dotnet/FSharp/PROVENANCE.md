# Source and distribution provenance

## Source ownership and preserved history

This repository owns the native F# frontend and the Convex backend fork. Git history records subsequent source changes. `INTEGRATION.md` records the current consolidation decisions.

Earlier Fable compilation was removed from the working tree on 2026-10-09. Historical Fable package statements describe that prior stage. They do not establish acceptance of the current native source.

The exact previous version of this provenance page is retained in the indexed historical record (archived in Git history). Its Fable package and earlier execution statements describe that prior stage. Historical transcripts, import manifests and reported evidence remain unchanged; none establishes acceptance of the current native source.

## Current native ownership

The native platform repository owns the F# frontend in `dotnet/FSharp`, neutral execution contracts in `dotnet/Runtime`, and managed admission, loader and host in `dotnet/Execution`. [SOURCE.md](../SOURCE.md) records that ownership. The neutral runtime layer sits below the F# frontend. This Convex backend fork owns original transaction, query tracking, scheduling, storage, component and HTTP execution integration. Its upstream attribution and license records remain with the fork.

First-party application and SDK source is authored in native F#/.NET. There is no current Fable compilation lane or first-party JavaScript/TypeScript package-source distribution. Handler-free canonical contracts and generated F# client catalogs are native artifacts; the current SDK instructions describe their producer and owning projects. The repository root `global.json`, `Directory.Build.props` and `Directory.Packages.props` own the .NET toolchain and dependency selection. The SDK has no separate npm package or .NET toolchain owner.

Upstream runtime and system assets, the thin Tauri shell and their separately pinned dependencies retain their own source ownership and licenses. Compatibility work does not transfer their ownership to Convex.FSharp. Runtime credentials and private fixture material are not package inputs.

## License and distribution record

The inherited NuGet metadata designates Apache-2.0. The repository `LICENSE` supplies that standard text, and `NOTICE` retains the accompanying attribution statement. Adding those files is not an independent determination that every historical contributor or imported file grants distribution rights. The project owner must confirm the applicable rights and any additional notices before public publication. No copyright owner or repository URL has been invented.

The current project declares its package identity and includes the existing repository `LICENSE`, `NOTICE`, README and provenance files in `src/Convex.FSharp/Convex.FSharp.fsproj`. Package contents and dependency closure must be assessed against that native project and the root dependency metadata. The historical consumer check for a complete Fable source list no longer describes the product.

Local 0.3.1 package identities are recorded in [preview-artifacts.json](../preview-artifacts.json). No public registry upload or transfer of publishing credentials is recorded. Current source and artifact boundaries are described in the [native platform guide](../README.md). Source provenance is not a substitute for the installed release receipt in [implementation status](../IMPLEMENTATION_STATUS.md).
