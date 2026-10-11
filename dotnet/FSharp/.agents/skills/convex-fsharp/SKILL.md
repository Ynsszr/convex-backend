---
name: convex-fsharp
description: Develop native F#/.NET Convex backends, shared contracts and clients in this Convex backend fork.
---

# Native Convex.FSharp development

Read the repository's current AGENTS.md and this package's AGENTS.md, README.md and INTEGRATION.md before editing SDK or application boundaries. Current human scope controls execution and delegation. Historical Fable/esbuild/Node/schema.ts recipes are superseded; do not reintroduce them.

Use handler-free shared contract assemblies for DTOs, immutable table markers and exact argument/result codecs. Backend registrations, generated F# references and actual client consumers share these owners. Keep query/mutation/action kind and public/internal visibility through registration, nested calls and scheduling; erase only at the heterogeneous runtime catalog boundary.

Queries read; mutations own atomic writes and scheduling; actions own separately dispatched effects. Preserve original Convex transaction/read-set/OCC/identity/component/commit authority. HTTP composition does not substitute for transaction execution. Deterministic import/query/mutation admission includes frozen dependencies and actual reachable framework callbacks. Do not weaken admission or use ambient clock, task identity or scheduling to make a fixture pass.

Canonical values preserve exact field/discriminator names, int64/float/bytes distinctions, missing versus null, document IDs and pending commit timestamps. Distinct tables sharing a document shape need Table.defineWithMarker. Custom codec owners must be exported from shared contracts. Reject unsupported representations explicitly; preserve unknown extension data where its owning contract requires it.

Root owns SDK/package policy, graph and producers. Use `dotnet/DEVELOPMENT.md` and `dotnet/tools` for project creation, local development and example packaging. Native phases compile backends and contracts, package capsules, generate and compile clients and client hosts, then publish F# WebAssembly. Examples and fixtures stay outside the product closure.

Transport effects are sent once. Completed, Refused and Uncertain are distinct outcomes. Lost or malformed replies require original-attempt reconciliation, never automatic resend. Preserve iterator/stream cleanup, cancellation, bounded buffers, worker retirement, original receipts and restore fences.

Use disposable fixtures before retained migration. Record current source and artifact identities for actual results; do not inherit old totals. Keep provider and account effects and installed or device observations within the selected human scope. The native executor is opt-in through `CONVEX_DOTNET_MANIFEST` in this repository. Moving the default branch does not silently select another runtime.
