# Native F# capability record — 0.3.x

This is a behavior-oriented release record, not a line-count or test-count claim. The applicable backend is the maintained Convex fork and the tested environment is Linux x64. A successful check establishes the listed scenario, not every possible deployment.

The table records the installed 0.3.0/0.3.1 capability baseline. Source candidate 0.3.2 is under verification and does not inherit installed acceptance merely by retaining these APIs. Its additional behavior is listed separately below.

| Capability | Implementation and exercised boundary |
| --- | --- |
| F# backend authoring | Typed queries, mutations, actions, contexts, schemas, validators, records/unions, canonical codecs and function handles. Application functions remain CLR assemblies, not Fable output. |
| Ordinary dependency consumption | Six NuGet packages, installed CLI, application/component templates, full runtime and transferable release archive. Package-only consumers must not reference the fork. |
| Root and nested components | Generic manifest DAG with cycle/conflict detection, per-component storage, explicit and conventional exports, parent references with internal visibility, nested calls and reexports. |
| Transaction behavior | Original Convex transaction owner: native/nested rollback, schema rejection, concurrent OCC increments and explicit exhausted-retry refusal. |
| Auth and environment | Native signed OIDC acceptance and rejection cases; user-token changes clear old query data and request authority; CLI set/list/remove uses named credential/value variables. |
| Multiple assemblies | Functions/declarations select an explicitly referenced assembly; frozen dependency bytes and a single shared contract identity prevent accidental bin-directory conflicts. |
| Authored codecs | Handler-free contract assemblies export their exact codec instances; composed codecs retain their owners in generated F# code. |
| Reactive client | One coherent connection, canonical query deduplication, reference counts, complete transition snapshots, chunk handling, dynamic subscriptions and reconnect. |
| Optimistic mutations | Pure overlays rebase in request order; refused writes roll back the overlay; success settles after its commit becomes visible in a query transition. |
| Lost reply behavior | A fault relay drops a real successful mutation reply; replay preserves session/request/arguments and the database write is not duplicated. Actions are not replayed. |
| Pagination | Basic and advanced backend cursors plus reactive multi-page client. Loaded ranges remain subscribed, split replacement waits for both children, identity changes discard private cursors, reconnect retains journals. |
| Search | Native full-text and vector operations use schema indexes through existing backend owners. |
| Scheduling | Native schedule/cancel and transaction rollback behavior; cron declarations are packaged by the same native producer. |
| Storage | Native action upload/read/delete metadata and bounded storage streaming. |
| HTTP | Native request bodies, clone semantics, headers, HEAD handling, route aliases, HTTP-only projects and streaming. |
| Operations | Native run/env/logs/tables plus snapshot ZIP export/import. Restore checks use a separately credentialed empty backend and verify nested component data. |
| Development loop | Installed runtime discovery, app-local identity/data, evaluated MSBuild source graph, linked/referenced source watch, local rebuild/deploy and unchanged-graph handling. |
| F# web client | Full-stack Blazor WebAssembly template, native mutations, coherent list/count updates across two actual browser tabs. |
| Runtime distribution | Optimized backend, managed host/CLI, exact inventory, architecture/link/tamper checks, staged publication and independently verified installation. |
| Resource failure | Bounded worker execution, rollback after a noncooperative mutation timeout and successful replacement-worker execution. |
| Existing JS/TS behavior | Native export metadata is explicitly tagged. Non-native definitions retain original file-based exports and runtime owners; this is not a replacement JavaScript backend. |

## Verification layers

Unit/protocol checks catch local invariants. Disposable live-backend checks establish the original transaction/auth/storage/sync boundaries. Package-only and installed-browser checks establish distribution independence. Public container/registry rollouts and long-duration production qualification are distinct and are not asserted by these layers.

## Design decisions

The application owns domain functions, data contracts and configuration; the dependency owns reflection/admission, package generation, deployment and runtime loading. One shared assembly context preserves authored-codec identity. Canonical deployed addresses are separate from CLR names. Immutable client snapshots and pure optimistic transformations fit F# without introducing a second application state authority. Reuse the backend's deduplication and transaction machinery instead of approximating it with HTTP retries. Preserve component storage/visibility boundaries rather than flattening a graph to fit a starter.

## Lifecycle patch

0.3.1 retains the 0.3.0 capability surface and corrects source-watch overlap, paginated observer serialization and client/subscription disposal. An observer may coalesce intermediate views, but callbacks for one subscription are serialized and advance monotonically in revision. Cancellation callbacks are invoked outside the client-state lock. Exact patch verification is recorded in IMPLEMENTATION_STATUS.md; the older baseline receipts do not substitute for it.

## Source candidate 0.3.2

The candidate corrects reconnect overlay duplication, stale values after removing/re-adding a subscription, and normal WebSocket-close handling. Its deterministic admission rules restrict ambient date/time operations while preserving explicit calendar/offset arithmetic. Protected secret files and private state directories replace secret-bearing local process arguments. Instance-scoped local journals survive port changes, with unresolved legacy receipts preventing migration. External-source watch coverage includes periodic discovery of new glob inputs.

Worker temporary files now consume explicitly bounded mounts within the configured budget: at 256 MiB, 192 MiB covers processes and 32 MiB each covers `/tmp` and `/dev/shm`. This requires bubblewrap `--size` support and does not replace host cgroups. These source boundaries still require the candidate's final package/runtime/live checks; they do not certify arbitrary CLR code or production deployments.
