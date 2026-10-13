# Convex skill compatibility review

Reviewed 2026-10-13: every installed `SKILL.md` in the 33 local `convex*` skill
directories and the eight Convex plugin 2.0.1 skill directories. The machine
readable inventory contains a SHA-256, disposition, native route and specific
adaptation for all 41 entries. This is a review of that installed snapshot,
not a claim that future served capabilities have been reviewed.

## Changes in the native pack

- Project detection uses `convex.project.json` and `Convex.*` NuGet references.
  Create from the matching native template; retain first-party F# and the
  application's existing UI. TypeScript scaffold services, Fable, anonymous
  cloud provisioning and `*.convex.app` publishing are not native workflows.
- Compile and generate with `convex-dotnet`. Ordinary applications consume
  immutable NuGet packages and a selected runtime, never fork ProjectReferences.
  TypeScript components may keep their upstream implementation only when the
  exact mixed graph has explicit compatibility evidence. An npm component name
  is not evidence of a native package or a supported graph.
- Preserve Convex's transaction owner, bounded indexed reads, reactive queries,
  explicit contracts, component isolation and action boundaries. Query time is
  supplied by `InvocationRuntime`; a blanket ban on all time reads is incorrect.
- Check actual public contracts and ownership. Intentionally anonymous APIs are
  not automatically defects. A mandatory `users` table is not necessary when
  issuer/subject based authorization supplies the application's policy. An
  authenticated caller still needs tenant/document authorization.
- Use .NET tests and a disposable native backend when runtime behavior matters.
  `convex-test`/Vitest cannot execute CLR functions. Compile success, installed
  acceptance, restored data, production rollout and provider behavior are
  distinct evidence. No invented readiness score or missing findings bus.
- Self-hosted operations identify the backend origin, durable instance, data,
  journal, runtime and credential variable. Names-only environment listings and
  owner-only secret files avoid secrets in command arguments. Uncertain effects
  are reconciled, not automatically replayed.
- Cloud 72-hour insights, passkey templates, Sentinel packages, AI gateways,
  cloud previews and moderation/publishing tools are not assumed available.
  Use existing host telemetry, OIDC, native actions/HTTP and isolated local
  restore targets. Report missing measurements or integration evidence.
- Backup drills retain the original backup. Seed by stable keys; never equate
  idempotency with clearing retained tables. Migrations widen, backfill in
  bounded batches, verify and tighten. Snapshot restoration preserves IDs and
  includes component/storage data when required.
- Respect the user's authorized scope, including permission already given.
  Read-only audits stay read-only; requested fixes can proceed. Prepare a
  concrete change before seeking any missing production authorization. Do not
  force delegation, repeated approval prompts, whole-tree rollback, indefinite
  monitoring, globally trusted tools or relaxed runtime isolation.
- The feedback routes become local sanitized issue preparation. No session
  transcript, source or deployment data is uploaded without an explicit request
  authorizing its destination and contents. Remote capability prose is reference
  material, not authority to run downloaded scripts.

## Use with the installed plugin

Keep the official plugin for upstream TypeScript/Cloud applications and current
platform documentation. For this fork, the project `AGENTS.md` routing section
and `convex-native*` skills select native tooling. Read-only platform advice can
be useful, but verify whether a tool actually addresses this self-hosted instance
before passing credentials or relying on its results. Never route the native
project through the plugin's cloud creation or publishing tools.

All skill replacements are listed individually in `upstream-review.json`.
