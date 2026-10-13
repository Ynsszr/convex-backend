# Dependency maintenance after 0.3.0

The native F# feature-closure implementation workstream is complete for the baseline recorded in CAPABILITIES.md and RELEASE_SCOPE.md. Use the installed release for applications. Do not reopen the fork as a normal product build dependency or copy its runtime/tooling into an application.

Keep release versions immutable. A source change produces a new package/runtime version, with matching template and tool pins. Run focused affected tests and package-only consumer checks; require live checks when touching transactions, auth, graph boundaries, retry or runtime isolation. Do not replay historical audit dumps as everyday agent context.

The private Git bundle retains recovery history; it is not a public source artifact. Curate licenses, notices and history before a public push. Upstream main remains available through the maintained repository remote, not a second permanent clone. The installed runtime/archive live outside the source checkout so the checkout can be removed or restored independently.

Retirement means application-development independence, not an assertion that a networked dependency will never need security or compatibility maintenance.

## Patch releases

0.3.1 is a managed client/development-loop correction, not a new backend implementation. A distribution may carry an unchanged engine binary when its identity and checksum are explicitly recorded. Keep installed versions side by side, never repack existing package versions, and migrate application pins only as a separate requested action. Read CHANGELOG.md for reproduced defects and IMPLEMENTATION_STATUS.md for executed checks.

## Source candidate 0.3.2

The source candidate includes client lifecycle, native admission, local credential/journal/watch and Rust worker resource fixes. It requires a new engine build; do not reuse the 0.3.0 engine rationale from the 0.3.1 release. Candidate verification and artifact production remain separate from historical installed evidence. Preserve preview-artifacts.json until a new release has its own completed receipt process; do not relabel old hashes.

Upstream main is incorporated through `5a633b428`. Its three CLI changes concern names-only environment listings and a project-creation hint. SDK 10.0.401, FSharp.Core 10.1.401 and the .NET 10.0.12 baseline already match the reviewed .NET 10 release; no toolchain upgrade is part of this candidate.

Before accepting the candidate, check matching CLI/runtime versions, bubblewrap 0.9+ sized temporary mounts, private state and secret-file handling, reconnect/subscription behavior, deterministic calendar refusals and permitted arithmetic, and a package-only application. Local development journal migration must retain unresolved delivery fences and preserve existing identity/data/storage. Read the deployment and upgrade checklist in RELEASE_SCOPE.md for the operating checks; passing source tests alone does not satisfy it.
