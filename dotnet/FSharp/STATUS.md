# Historical source inventory (2026-10-06)

This page records the source inventory from that phase. It is not the 0.3.1
release record. Exercised behavior is in [CAPABILITIES](../CAPABILITIES.md) and
[implementation status](../IMPLEMENTATION_STATUS.md). Entries below describe
authored source at that checkpoint. They do not add or remove the later release
checks.

## Implemented source

| Capability                   | Current source and ownership                                                                                                                                                                           |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Neutral runtime ABI          | Convex.Runtime owns language-neutral invocation, declaration and application-error interfaces. Convex.DotNet's runtime library depends on it; the host composes the F# frontend.                       |
| Function declarations        | Six public/internal query/mutation/action builders, Async/Task handlers and typed contextual authority. Kind and visibility survive registration and reference construction.                           |
| Canonical callable contracts | One checked argument/result codec pair, explicit custom owner exports, handler-free shared contract assemblies and F# generated client catalogues.                                                     |
| Values and documents         | Exact records/unions, typed IDs, document metadata, int64/float/bytes distinctions, null/absence and pending commit timestamps.                                                                        |
| Database queries             | Table-scoped reads, indexed ranges, filters, full-text search, bounded collections, iterators, cleanup and advanced pagination through the original provider.                                          |
| Mutation semantics           | Table-scoped writes, field assignment/removal, nested transactional calls, read-set/OCC/commit ownership and atomic scheduling remain in Convex.                                                       |
| Action semantics             | Original TaskExecutor callbacks, separately owned query/mutation/action calls, admitted .NET libraries and retirement of managed workers before terminal delivery.                                     |
| Scheduling and crons         | Typed mutation/action references, cancellation, durable handles and original transaction-versus-action scheduling semantics.                                                                           |
| Schemas and indexes          | Canonical native table/index/search/vector declarations, staged declarations and document validators entering the original schema owner.                                                               |
| Components                   | Native root/child definitions, arguments, exports/reexports, mounting, initializer callbacks and original component identity/typechecking; mixed upstream component graphs remain supported.           |
| Auth and metadata            | Standard/custom identity claims, function/deployment/request metadata and owner-supplied clock/random/environment inputs.                                                                              |
| Vector and managed services  | Filtered vector search; finite ServiceName.AiGateway token and URL APIs using original owner syscalls.                                                                                                 |
| Storage                      | Original URL/metadata/upload/delete ownership, modern versus legacy ID contracts, buffered/streamed action reads/writes and explicit close/abort/commit behavior.                                      |
| HTTP                         | Native exact/prefix/HEAD routing, checked requests, headers, binary/JSON bodies, buffered responses and bounded streaming through the original HTTP owner.                                             |
| Logs and errors              | NativeLog and auditLog owner operations, exact safe application message/data, bounded CLR fault diagnostics and native runtime provenance.                                                             |
| Native HTTP clients          | Kind/visibility-specific calls, shared codecs, explicit administrative internal calls, bounded transport and Completed/Refused/Uncertain outcomes.                                                     |
| Query subscriptions          | Public query references, canonical decoding, structured owner errors and query version/timestamp state; effects never enter subscription APIs.                                                         |
| Native tooling and examples  | Frozen capsules, F# API/schema/provenance generation, native deployment tool, client-host consumers and the fleet-management and order-processing examples with handler-free contract/client projects. |

Relevant source owners are `src/Convex.FSharp`, `../Runtime/src/Convex.Runtime`,
`../Execution` and this repository's `crates/dotnet_executor`,
`crates/isolate/src/environment/{udf,action}/dotnet.rs` and
`crates/function_runner/src/server/dotnet.rs`. Exact feature/source anchors and
delivered revisions are recorded in [INTEGRATION](INTEGRATION.md) and the native
handoff (archived in Git history).

## Representation decisions

| Representation                                                                     | Deliberate contract                                                                                                                                             |
| ---------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| int64 and floating-point values                                                    | Convex's full-width integer and floating-point encodings remain distinct; integral conversion rejects fractions, overflow and precision loss.                   |
| Bytes                                                                              | Convex tagged binary values remain distinct from ordinary JSON strings or numeric arrays.                                                                       |
| Absence and null                                                                   | Record omission, explicit null, option-like wrappers and field removal follow canonical context-specific codecs; ambiguous nested presence shapes are rejected. |
| Record and union fields                                                            | Preserve exact field names and discriminator shape. An equivalent-looking default .NET JSON result is not a substitute for the canonical wire contract.         |
| IDs and table identity                                                             | IDs retain table markers; the backend checks actual ownership. Shared document shapes need explicit distinct-table markers.                                     |
| Commit timestamps                                                                  | Native Value/Placeholder retain the original pending-commit representation. The obsolete raw JavaScript placeholder case is removed.                            |
| Custom domain representations                                                      | Export checked Contract.create/map owners from a handler-free assembly. Generation does not infer their mappings from validators.                               |
| Recursive automatic validators                                                     | Refused. Use referenced/normalized records or an explicit bounded contract instead of silently emitting any.                                                    |
| Unsafe wide-enum metadata, nonportable CLR collections and arbitrary object graphs | Not silently approximated. Select a supported representation or a deliberate checked contract.                                                                  |
| Ordinary JSON and Convex JSON                                                      | Separate APIs select ordinary JSON or the Convex tagged protocol; storage hashes and retained requests keep their owning representation.                        |

## Resource and execution limits

The native executor implements a bounded first-party admission policy. It is not
arbitrary customer IL hosting.

- Query/mutation/import admission checks frozen code and dependencies before
  evaluation. Ambient effects, reflection/foreign-code bypasses and unresolved
  dispatch are refused. Managed actions have a separate, broader .NET capability
  profile.
- Restricted workers require Linux and bubblewrap. Transaction workers isolate
  networking; action workers have a separate network-capable profile. Runtime
  and artifact roots are read-only and hash pinned.
- The worker semaphore admits a configured 1..64 workers. Full nested capacity
  refuses immediately. Cancellation, protocol/resource failure and timeouts kill
  the owned process; actions are reaped before publishing terminal results.
- GC heap limits, sampled descendant RSS, process/task bounds and original
  user/system plus total wall budgets are implemented. Hard cgroup CPU/memory
  enforcement and arbitrary tenant isolation are not claimed.
- Immutable extracted capsules are bounded to 64 bundles/512 MiB per backend
  process. Capacity exhaustion refuses admission; restart reconstructs capsules
  from persisted source packages. Unlimited hot deployment revisions are not
  claimed.
- Framed IPC is capped at 16 MiB. HTTP/storage chunks are at most 64 KiB with
  bounded queues and invocation-owned handles. Buffered HTTP responses are at
  most 8 MiB; existing request/streamed-response ceilings remain 20 MiB.
- A native log call bounds aggregate message bytes to 4096. Fault diagnostics
  use that same owner and cannot replace the terminal outcome. Import logs and
  portable PDB/source-map presentation are outside the selected logging surface.
- Public HTTP follows upstream's named root public-reference contract. Component
  exports and handles use owning native Context calls. Full browser Web APIs,
  unbounded SSE and the entire JavaScript optimistic-client feature set are not
  implied by the selected native APIs.

## Upstream and environment boundaries

AI gateway tokens/URLs, entitled audit delivery and provider behavior remain
subject to the owning backend's configuration and service support. Native
self-hosted implementation does not enable the executor in managed Convex Cloud.
Upstream system functions and third-party JavaScript components retain their
original executor and authority.

Cross-platform packaging, sustained operational behavior, arbitrary plugin/agent
code admission and official upstream/cloud adoption require separately defined
work. The package's provenance and licensing records remain applicable; source
implementation does not constitute a public distribution or release.

## Current acceptance obligations

Current compilation, fixture migration and runtime acceptance are in progress.
Exact receipt pins and missing gates belong to the current follow-through
record. Selected semantic gates include exact wire/error compatibility,
transaction/OCC and reactive ranges, authority/components, scheduling,
interruption, storage/HTTP streams, deployment receipts and runtime switching.
Operational/resource and platform/provider gates follow their owning
environments. [NEXT](NEXT.md) records that order.

The historical index (archived in Git history) preserves earlier SDK
instructions, completion reports and totals. They remain evidence for their
named revisions only. No combined coverage figure or current parity percentage
is asserted here.
