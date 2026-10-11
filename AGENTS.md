# Native Convex platform

This repository owns the Convex backend and its native .NET/F# integration. The managed source is in `dotnet`, with one root `Convex.slnx`. The backend retains the upstream Cargo workspace. The .NET toolchain, versions and packages are selected at this repository root.

Applications consume released NuGet packages and explicit runtime distributions. Do not import another product's policy, source-layout metadata, or lifecycle logic. Prefer idiomatic functional F# in managed code; upstream Rust and infrastructure retain their own implementation language.

Do not change installed services, credentials, or provider state. Public publication requires a prepared release and license/provenance review. Do not add private machine paths, credentials, or another product's source to this repository.

Keep upstream license and attribution records. `dotnet/SOURCE.md` records managed-source ownership. Report source changes without presenting them as compiled or runtime-accepted behavior.

## Standalone platform

The supported developer experience is package-only project creation, local development and deployment, coherent reactive clients, documented capability coverage, and independently installable artifacts. Application work should not require a checkout of this repository.

## Release state

Version 0.3.0 established the documented Linux x64 feature baseline; the current installed release is 0.3.1, correcting client lifecycle and development-loop defects. Normal application development consumes the installed release, not this checkout. See dotnet/IMPLEMENTATION_STATUS.md and dotnet/MAINTENANCE.md before reopening platform work. No public registry or managed-cloud admission is implied.
