# Convex for F# release notes

## 0.3.2 — locally validated hardening candidate

This candidate is not yet released or installed. Existing 0.3.1 artifact hashes
and installed acceptance evidence remain version-scoped. Unlike 0.3.1, this
candidate changes the Rust backend and requires a newly built engine alongside
matching managed packages, CLI, host and templates.

- Reactive reconnect/status changes retain the server snapshot and apply each
  optimistic overlay once. Removing the last subscription drops its cached
  value; re-adding it waits for the new registration. A normal WebSocket close
  is treated as a reconnectable transport loss.
- Deterministic native admission uses explicit DateTime/DateTimeOffset member
  rules. Implicit date parsing, local timezone conversions/formatting and
  related Convert/JSON paths are refused. Explicit calendar arithmetic, supplied
  offsets and Unix timestamp operations remain available; actions retain date
  parsing. Refusals explain the alternatives.
- Backend startup and embedded admin-key generation accept a protected
  `--instance-secret-file`. Local development uses it and requires private state
  directories, avoiding secret contents in process arguments.
- Restricted workers split their configured memory budget between processes and
  bounded `/tmp` and `/dev/shm` mounts. The default 256 MiB selection gives 192
  MiB to processes and 32 MiB to each mount; bubblewrap 0.9+ with `--size` is
  required.
- Local development journals follow durable instance identity across port
  changes. Unresolved legacy URL-scoped receipts block migration; recorded
  history and existing application data are preserved. The CLI refuses
  mismatched runtime versions.
- Source watching covers evaluated external project/source directories and
  periodically refreshes previously empty external globs.
- Receipt reconciliation accepts an explicit local instance identity. Missing
  identities over retained state and malformed legacy journal entries fail
  closed. Local Just recipes also use protected secret files.
- The solution owns example contract generation in the selected build
  configuration. Native acceptance generates fresh fixtures, and CI now executes
  Rust proof tests and the live backend acceptance suite.

Upstream main was merged through `5a633b428`: the upstream CLI and MCP
environment listing now return names by default, and the project-creation
deployment hint was corrected. The root toolchain remains SDK 10.0.401 with
FSharp.Core 10.1.401 and the .NET 10.0.12 runtime baseline; the
[official .NET release record](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.12/10.0.12.md)
did not require a compiler upgrade. Candidate verification and artifact identity
are recorded in IMPLEMENTATION_STATUS.md and candidate-artifacts-0.3.2.json.

## 0.3.1 — lifecycle and development-loop corrections

A backward-compatible managed SDK/tooling patch to 0.3.0. The native Rust
backend is unchanged; the runtime distribution deliberately includes the same
optimized `fsharp-0.3.0` engine bytes with matching 0.3.1 managed packages and
host. Distribution and engine versions are recorded separately rather than
rebuilding or relabeling unchanged code.

- Source watch captures its input snapshot before a build. Edits saved during
  compilation or deployment now trigger another iteration rather than being
  treated as already built.
- Shared and paginated clients use the same serialized, revision-aware observer
  delivery. An initial callback cannot overlap subsequent callbacks; stale
  pagination inputs cannot move a view backward.
- Disposed clients/subscriptions deactivate queued observer notifications. A
  callback already selected for delivery may finish; disposal never waits on a
  reentrant callback.
- Authentication/reconnect/disposal cancel connections outside the client-state
  lock. An error from a superseded connection cannot clear a replacement
  identity's pending queue.
- Local F# build failures now retain stdout compiler diagnostics as well as
  stderr, while leaving the last successfully deployed application in place.

The watch race, pagination callback overlap and shared-client disposal defect
were reproduced by failing regression tests before correction. Verification
receipts are recorded separately after package-only and installed-release
checks. Existing application pins and installed 0.3.0 remain unchanged.

## 0.3.0

Introduced the documented native feature baseline: nested components, multiple
handler assemblies, authored codecs, optimistic mutation/replay handling,
reactive pagination, operations/backup tools and the optimized installed
runtime. Its exact checks and bounded scope remain available in Git history and
the 0.3.0 release receipts. Feature coverage was not a guarantee of zero
defects; 0.3.1 corrects lifecycle cases outside those original tests.
