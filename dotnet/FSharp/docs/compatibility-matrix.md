Current limits and status: [STATUS.md](../STATUS.md) and [implementation status](../../IMPLEMENTATION_STATUS.md).

# Native capability navigation and acceptance boundaries

This page groups the native capability families. [CAPABILITIES.md](../../CAPABILITIES.md) and [STATUS.md](../STATUS.md) record supported behavior and known limits. The source checkpoint recorded with the original inventory is `8b9d5971ca13411e9d613da5b96c3ae9e7579f82`. Historical audit matrices are not part of this tree.

**The labels `source-implemented` and `execution-acceptance-deferred` belong to that dated inventory.** They are not a statement that 0.3.1 was never built or tested. Exercised checks are in [implementation status](../../IMPLEMENTATION_STATUS.md) and [CAPABILITIES.md](../../CAPABILITIES.md). A source path in this inventory is evidence of authored implementation at the recorded checkpoint.

## Native source families

| Ledger ID | Capability | Owning behavior |
| --- | --- | --- |
| `neutral-runtime-abi` | Language-neutral CLR execution and frontend composition | CLR ABI below the F# frontend; host composition owns language selection. |
| `typed-function-kinds` | Public/internal query, mutation, action and HTTP registration | Kind, visibility and canonical argument/result contracts survive the native boundary. |
| `values-and-presence` | Exact Convex values, absence, document IDs and commit timestamps | Exact Convex value/wire representations and table ownership; no JSON approximation. |
| `canonical-codecs-and-validators` | Canonical custom/derived contracts and exact boundary validation | Canonical codec provenance and exact validators, including custom contracts. |
| `transaction-owner` | Original Convex transactions, writes, OCC and reactive dependencies | Original Convex transaction, OCC, commit and reactive dependency ownership. |
| `kind-capabilities` | Separate read, mutation and managed action capabilities | Read, mutation and action capabilities are separate; trusted adapter bypasses are denied. |
| `querying-and-pagination` | Indexes, filters, document metadata and bounded cursor traversal | Original indexes/filters/query streams, document metadata and bounded cursor traversal. |
| `search-and-vector` | Full-text and vector indexes and search | Original search/vector operations and validated finite vector/filter inputs. |
| `schema-and-analysis` | Native schema, authentication, cron and module analysis | Native function/schema/auth/cron/router/component declaration ownership. |
| `auth-and-metadata` | Original identity, component, environment and transaction metadata | Original identity, component scope, environment and transaction metadata. |
| `deterministic-environment` | Owner-supplied transaction time and randomness | Owner-supplied time/randomness; ambient transactional effects are refused. |
| `nested-calls-and-handles` | Typed nested functions, component exports and durable handles | Exact callable kind, component export/reexport and durable handle identity. |
| `scheduling-and-crons` | Transactional scheduling, cancellation and cron declarations | Mutation/action-only targets and original scheduling/cancellation ownership. |
| `managed-actions` | Native managed actions with independently owned effects | Admitted managed effects and independently owned calls; terminal worker retirement. |
| `storage` | Original storage capabilities and bounded native streams | Original storage identity/drivers with invocation-scoped bounded stream ownership. |
| `native-http` | Native HTTP routing, bodies and bounded streaming | Original router/streamer with bounded native request/response/queue behavior. |
| `components` | Native component graphs, exports, arguments and initializers | Native graph/import/typechecking/initializers and preserved durable addresses. |
| `errors-and-logs` | Structured application errors and owning native diagnostics | Deliberate error message/data and bounded owning diagnostics, without masking outcomes. |
| `worker-protocol-and-pool` | Persistent bounded native workers and invocation-scoped IPC | Persistent bounded processes and strict invocation/request-correlated local frames. |
| `admission-and-sandbox` | Hash-bound IL/capture admission and selected Linux process sandbox | Frozen IL/capture admission plus selected Linux process boundaries. |
| `deployment-artifacts` | Native manifests, immutable capsules and assembly dependency identity | Runtime-discriminated pinned manifests/capsules and admitted dependency identity. |
| `deployment-lifecycle` | Native deployment preparation, schema phase and receipt recovery | Preparation/schema/activation/receipt states preserve the original attempt. |
| `canonical-client-generation` | Shared handler-free contracts and native F# client catalogues | Actual shared contract owners and canonical native F# reference generation. |
| `native-http-clients` | Typed native HTTP reads/effects and explicit delivery outcomes | Typed public/internal authority and Completed/Refused/Uncertain delivery outcomes. |
| `reactive-native-client` | Native query subscription version and timestamp semantics | Typed query subscription versions, timestamps, authentication and errors. |
| `native-browser-consumers` | Native F# browser consumers and proof-scoped private views | Native F# application flows and proof-scoped private view lifetimes. |
| `native-build-and-package-ownership` | Native source, owned build phases and optional examples | F# tooling, owned build phases and optional native examples. |
| `upstream-mixed-graphs` | Preserved upstream/system implementations and small fork boundary | Original upstream/system routes and a separately pinned native fork delta. |

Managed SDK paths live under `dotnet/`. The Rust backend in this repository owns transactions and the native worker. Shared SDK, runtime and handler-free contract projects are separate from application code. Historical candidate binary hashes remain historical.

## Selected implementation limits

These are limits of the selected source contract. The JSON ledger supplies the owning source reference for each one. Its `knownLimitDispositions` and unsupported `disposition` records identify the reason and Auditor 1 matrix rows for each entry. `acceptedSourceGap: false` means that particular boundary is not an omitted mandatory implementation; it does not establish compilation or behavior. A real missing accepted feature must instead name `accepted-source-gap`, its owner and the absent flow.

| Boundary | Current selected source contract |
| --- | --- |
| Runtime values | Exact Convex values and explicit representable option/null shapes. Automatic recursive validators, wider-than-32-bit enums and undeclared enum values are rejected; custom canonical representations remain available. |
| Native IPC | Strict version-1 length-prefixed local JSON frames, at most 16 MiB, with one serialized invocation channel. Binary/multiplexed/batched IPC is not implemented here. |
| Worker pool | Configured 1–64 workers; explicit pool admission and retirement. Query/mutation reuse requires the source health/RSS/invocation-count policies. Managed action/HTTP workers are killed/reaped before terminal publication. |
| Worker policy | Selected first-party Linux/bubblewrap profiles, frozen managed inputs and IL/capture admission. Sampled RSS/GC/task/process/wall bounds are not hard cgroup enforcement or arbitrary hostile CLR isolation. |
| Artifacts | Runtime manifest at most 1 MiB; at most 256 artifacts per inventory, at most 64 MiB each; managed deployment loading admits at most 64 dependencies. |
| Capsule extraction | Fail-closed cache ceiling of 64 immutable bundles or 512 MiB per backend process. Unlimited hot revisions are not promised. |
| HTTP | Buffered native response at most 8 MiB; owning request and streamed response at most 20 MiB; bounded chunks up to 64 KiB and bounded queues/handles. |
| Vector input | Finite, nonempty vectors of at most 4096 dimensions; result limit 1–256, plus original backend index/operation limits. |
| Native logs | At most 256 messages and 4096 aggregate UTF-8 bytes in one log operation. Method/type/message stack diagnostics exist; portable-PDB source presentation is optional diagnostics work, and enterprise audit delivery needs its original entitlement. |
| Public HTTP clients | Named root public references; internal calls require explicit administrative authority. Component handles are invoked through the owning native context, without invented public HTTP routes. |
| Browser/native platform | Authored F# WebAssembly behavior uses upstream runtime interop. Package compilation, actual CSP/startup, asset/bridge integration and provider/device behavior remain current acceptance gates. |

Native schema/table/index declarations feed the original Convex schema deployment and migration owner. Its current staged schema/index execution acceptance requires a source-specific receipt. The native frontend is not expected to add a second migration lifecycle.

The kinds of context are intentional: native queries/mutations do not gain arbitrary network, filesystem, process, host-clock or random effects. Managed action policy admits a broader selected managed surface. A full .NET ecosystem goal does not mean bypassing the original Convex transaction or the selected execution authority.

## Retired and unsupported surfaces

These rows have `sourceStatus: unsupported` and `acceptanceStatus: not-applicable`. They must not be interpreted as accepted-but-unexecuted product features.

| Ledger ID | Reason |
| --- | --- |
| `fable-first-party-target` | Retired by the current owner instruction. Earlier Fable oracle/fallback recommendations are superseded, and no current application build may require that target. |
| `arbitrary-unmanaged-or-hostile-clr` | Selected first-party admission requires pinned managed dependencies and capability-limited code. Native unmanaged loading, dynamic runtime loading/process creation and ambient query/mutation effects are refused. This is an explicit selected policy; arbitrary multi-tenant CLR sandboxing remains a separate implementation project. |
| `unmodified-managed-convex-cloud` | The selected backend is a pinned source fork. Local source changes cannot enable a runtime in an unmodified managed provider. No provider agreement/admission or entitlement is implied. |
| `automatic-unrepresentable-clr-values` | Automatic reflection rejects recursive validator schemas, unsupported CLR types, enums wider than 32 bits and undeclared enum values. An explicit canonical representation with owning validation is required. Exact Convex semantics take precedence over automatic acceptance of every CLR type. |
| `unbounded-native-http-and-full-web-api` | The selected native HTTP contract uses bounded response/body/chunk/handle ownership. It does not promise unbounded SSE or all browser request/response APIs. |

The JSON also records implementation choices and external conditions. They include additional language frontends, NativeAOT as a possible future optimization, full JavaScript optimistic-client conveniences, source-mapped diagnostics, managed-provider runtime admission and upstream service entitlements. The auditor's commercial-license/upstream collaboration discussion is retained as a separate external follow-up; it is not a current legal conclusion or permission to contact another party.

## How to read this inventory

The deferred labels belong to checkpoint `8b9d5971ca13411e9d613da5b96c3ae9e7579f82`. They do not cancel the later 0.3.1 checks. Use [CAPABILITIES.md](../../CAPABILITIES.md) for the exercised surface and [release scope](../../RELEASE_SCOPE.md) for what that release does not claim.

Earlier Fable, Node, and TypeScript instructions and their proof totals are historical.

See the [package compatibility entry](../COMPATIBILITY.md) for owner boundaries. Earlier auditor traceability records are not part of this tree.
