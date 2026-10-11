# Convex for F# 0.3.1 — installed lifecycle patch

## Result

Version 0.3.1 is built, packaged, installed, and exercised as an independent managed-client/developer-tool patch to the 0.3.0 feature baseline. It corrects three defects reproduced after that baseline's original tests: edits lost during a build, overlapping pagination notifications, and notifications delivered from a disposed client's queued observer list. Compiler failures now show their stdout diagnostics as well as stderr.

Package source checkpoint: `80312c01fd150f7d603d3dc14db3955dc684455e`. Packages were produced after committing the implementation with a clean working tree. Later receipt/docs commits do not change that artifact source identity. Exact hashes are in preview-artifacts.json.

## What changed

The source watcher records build inputs before executing the build. A subsequent edit cannot be swallowed by a post-build timestamp capture. Shared and paginated clients use one revision-aware observer dispatcher: callbacks for one subscriber are serialized, pending views may coalesce, and disposed subscriptions drop queued views. An already selected callback may finish without disposal waiting on it. Pagination refuses older input revisions.

Authentication changes, reconnect and disposal request cancellation outside the client-state lock. A superseded connection's error cannot clear another identity's new queue. Local build errors preserve compiler stdout, redact local credentials, and leave the last valid deployed application unchanged.

## Executed patch checks

| Check | Result |
| --- | --- |
| F# SDK tests | 192 passed |
| Native execution/deployment tests | 111 passed |
| Developer/runtime/operations tests | 45 passed |
| Client/mutation/pagination tests | 28 passed |
| Total managed tests | 376 passed |
| Negative reproductions against pre-fix code | Watch overlap, pagination callback overlap, disposed-client notifications failed as expected |
| Advanced consumer using installed package feed | Built nested graph and handler-free generated client |
| Installed full-stack application | Native deployment, F# WebAssembly, two browser tabs, coherent list/count, source reload and restart passed |
| Intentional compiler error | FS0039 appeared in dev output; last valid endpoint and data remained available; source recovery redeployed |
| Installed advanced graph | Nested component isolation/rollback, multiple assemblies, authored codecs, optimistic mutation, real lost-reply deduplication and reactive pagination/reconnect passed |
| Distribution integrity | 77 release-file and 63 runtime-file hashes verified |
| Idempotent reinstall | Passed |

The external receipt files from that check are not stored in this repository. Package and archive identities are in `preview-artifacts.json`. The unrelated 23-check broad backend acceptance and snapshot export/import scenarios were not rerun for this managed patch; their 0.3.0 evidence remains explicitly version-scoped, not claimed as fresh 0.3.1 results.

## Installed outputs and engine provenance

Installed release: `~/.local/share/convex-fsharp/releases/0.3.1`.

Archive: `~/.local/share/convex-fsharp/archives/convex-fsharp-0.3.1-linux-x64.tar.gz` (70,451,735 bytes). The archive contains packages/runtime/docs/installer, not private application state or Git history.

This distribution deliberately reuses the byte-identical optimized Rust engine from 0.3.0 (backend source `606f0395adbbafbf2c2a16c1dbe9fd1e8cd4993f`, SHA256 `8cfede68077ab956551f6fd766c4fb56624969fe8b4cd17457df5af2e0a5d184`). The engine still reports `local_backend fsharp-0.3.0`; the installed distribution and all six managed package versions are 0.3.1. No Rust source changed and no backend rebuild was needed. Versions are not relabeled to imply a new engine build.

The 0.3.1 check used a package-only full-stack application. Its frontend configuration uses local API port 3210. Existing 0.3.0 releases and applications were not migrated or overwritten.

## Scope

CAPABILITIES.md and RELEASE_SCOPE.md retain the native Linux x64 support boundary. This is a verified patch, not a declaration of zero defects, official managed Convex Cloud support, container qualification, a public release channel or universal production security. Applications consume installed packages/runtime without importing the fork; dependency maintenance does not reopen application architecture.

Installed application services were not edited or built. No public push or registry publication occurred. Temporary application, browser, graph-test and backend processes were stopped.
