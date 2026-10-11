# Convex for F# release notes

## 0.3.1 — lifecycle and development-loop corrections

A backward-compatible managed SDK/tooling patch to 0.3.0. The native Rust backend is unchanged; the runtime distribution deliberately includes the same optimized `fsharp-0.3.0` engine bytes with matching 0.3.1 managed packages and host. Distribution and engine versions are recorded separately rather than rebuilding or relabeling unchanged code.

- Source watch captures its input snapshot before a build. Edits saved during compilation or deployment now trigger another iteration rather than being treated as already built.
- Shared and paginated clients use the same serialized, revision-aware observer delivery. An initial callback cannot overlap subsequent callbacks; stale pagination inputs cannot move a view backward.
- Disposed clients/subscriptions deactivate queued observer notifications. A callback already selected for delivery may finish; disposal never waits on a reentrant callback.
- Authentication/reconnect/disposal cancel connections outside the client-state lock. An error from a superseded connection cannot clear a replacement identity's pending queue.
- Local F# build failures now retain stdout compiler diagnostics as well as stderr, while leaving the last successfully deployed application in place.

The watch race, pagination callback overlap and shared-client disposal defect were reproduced by failing regression tests before correction. Verification receipts are recorded separately after package-only and installed-release checks. Existing application pins and installed 0.3.0 remain unchanged.

## 0.3.0

Introduced the documented native feature baseline: nested components, multiple handler assemblies, authored codecs, optimistic mutation/replay handling, reactive pagination, operations/backup tools and the optimized installed runtime. Its exact checks and bounded scope remain available in Git history and the 0.3.0 release receipts. Feature coverage was not a guarantee of zero defects; 0.3.1 corrects lifecycle cases outside those original tests.
