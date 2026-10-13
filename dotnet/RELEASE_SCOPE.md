# Convex for F# 0.3.x — supported release and closure boundary

## Release objective

Applications consume installed NuGet packages, templates, a native developer
tool and an optimized backend/runtime distribution. An ordinary application does
not need a checkout of this repository in its dependency graph.

The implementation gates raised in the earlier developer previews are addressed
in 0.3.0: nested component graphs, multiple handler assemblies, shared authored
codecs, coherent subscriptions, optimistic mutation handling, reconnect
deduplication, reactive pagination, native operational commands and an optimized
release build. `CAPABILITIES.md` records what each gate means and how it was
exercised. Artifact production and installed-only verification are recorded
separately in IMPLEMENTATION_STATUS.md and preview-artifacts.json.

## Supported baseline

- Community-maintained native F#/.NET execution in the modified Convex backend.
- Linux x64 runtime, tested on Ubuntu 26.04.1 LTS with .NET 10 CoreCLR and
  bubblewrap.
- F#/.NET WebAssembly full-stack template and a reusable native-component
  template.
- Versioned local/private NuGet feed and an installable archive; no source
  checkout required by application builds or runtime startup.
- Root and nested native component projects, canonical or explicitly exported
  shared codecs, typed generated function references, HTTP actions, auth
  declarations and crons.
- Shared client subscriptions and session-scoped mutation replay over the
  original Convex sync protocol. Actions and one-attempt HTTP/admin operations
  are never automatically replayed after uncertain delivery.

The generated component API remains server-only in its parent. Cross-component
authorization, application data validation and external-effect idempotency are
still application responsibilities. Query/mutation determinism and restricted
assembly admission remain runtime requirements; installing a NuGet package does
not authorize arbitrary unmanaged or effectful code inside transactions.

## What the release does not claim

This is not official Convex support or admission to managed Convex Cloud. It
does not certify all OS/CPU/container combinations, arbitrary hostile CLR
assemblies, every third-party NuGet package, every external JS component's
bundling workflow, or long-duration production behavior. Existing complete
compiled upstream graphs can still use the lower-level deploy-graph interface;
the native graph producer does not silently rewrite arbitrary npm build systems.

The Linux runtime still requires compatible system libraries, .NET 10 and
bubblewrap. An optimized binary is not a benchmark or production security
certification. The inherited Docker integration recipe has not been deployed or
qualified here; do not disable sandboxing merely to make a container start.

The retained backend license and notices remain unchanged. Public registries,
official endorsement, source-history publication, update hosting and supported
commercial operations are release/maintenance decisions, not facts implied by
local installation. Read PUBLISHING.md before public distribution.

## Retiring the local development project

After the installed-only application and advanced-graph checks pass and a
verified private source backup exists, the checkout can leave the everyday
application workspace. Retire the development checkout, not the dependency's
versioning, security maintenance or recovery history. Keep the installed release
and its archive outside the source tree.

## 0.3.1 patch

0.3.1 retains this supported surface and corrects identified client/dev-loop
defects. It reuses the byte-identical optimized fsharp-0.3.0 backend and
supplies newly versioned managed packages. It does not expand OS, container,
cloud, security-certification or public-publication claims. The previous
feature-closure label never meant future defect fixes were unnecessary.

## 0.3.2 source candidate

The candidate has passed local source, package and native runtime validation; it
is not installed or publicly released. It changes both managed code and the Rust
engine. Historical 0.3.1 receipts remain intact and do not establish acceptance
of the candidate.

The intended platform remains Linux x64 with .NET 10. Restricted worker startup
additionally requires bubblewrap 0.9+ with sized tmpfs support (`--size`). At
the default 256 MiB worker setting, sampled process-tree RSS and the GC limit
receive 192 MiB, while `/tmp` and `/dev/shm` each receive 32 MiB. Temporary
writes can fail with ENOSPC. These per-worker bounds do not replace host
controls for instantaneous resource use or capacity planning across concurrent
workers.

Calendar admission is stricter: implicit parsing, local timezone conversions and
date formatting must move before the transaction or into an action. Explicit
calendar/tick construction, supplied offsets and Unix timestamps remain
supported. Other compatibility changes include private local state directories,
protected instance-secret files, matching CLI/runtime versions, and durable
instance-scoped local deployment journals. An unresolved legacy delivery receipt
must be reconciled before its journal can migrate. See DEVELOPMENT.md for
application guidance.

## Deployment and upgrade checklist

This is an operator checklist for evaluating a self-hosted deployment, not a
production security certification.

1. Select an accepted release and pin its matching packages, templates, CLI,
   host and runtime. Verify its artifact inventory and recorded engine identity.
   Install versions side by side; do not overwrite historical release bytes or
   treat a source candidate as a released artifact.
2. Keep application identity, credentials, database, storage and deployment
   journals private to the service account. Use 0700 state directories and an
   owner-only `--instance-secret-file` with the candidate backend. Keep
   administrator credentials out of process arguments, logs, frontend
   configuration and source control.
3. Confirm Linux x64, compatible system libraries, the declared .NET runtime and
   functional bubblewrap sandboxing. For 0.3.2, verify `--size` support.
   Containers must permit the required namespaces and mounts; do not disable the
   sandbox to bypass a startup failure.
4. Export a backup including stored files where needed, retain a consistent copy
   of instance identity and delivery history, and prove restoration into an
   isolated instance before changing the active runtime. Reconcile uncertain
   deployment receipts before migration. A rollback must restore compatible data
   and identity as well as executable versions.
5. Budget for the backend, database/storage and concurrent native workers.
   Account separately for worker process memory and temporary mounts. Apply
   service/container CPU, memory and storage controls, and check timeout, ENOSPC
   and replacement-worker behavior under representative workloads.
6. Configure application authentication and per-function authorization,
   terminate public traffic with TLS, and restrict administrative endpoints and
   credentials. Verify public, authenticated and denied requests against the
   intended deployment before admitting traffic.
7. Monitor backend readiness, invocation errors, worker replacement, deployment
   receipts, disk capacity and backup outcomes. Exercise the application's
   critical transactions, subscriptions, external-effect handling and restore
   path after an upgrade; record these as new release-specific evidence.
