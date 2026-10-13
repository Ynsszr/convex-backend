---
name: convex-native-maintain
description: "Maintain the native Convex fork or upgrade consumers: compare upstream Convex and .NET/F# releases, preserve immutable package versions, assemble artifacts/skills, inspect provenance and clean disposable build outputs."
---

# Maintain versions and releases

Read project instructions, package/runtime receipts and the selected
[maintenance guide](../convex-native/references/platform/dotnet/MAINTENANCE.md).
Determine whether this is ordinary app work or an authorized fork change.

1. Record Git status/remotes/worktrees. Fetch the intended upstream, inspect
   relevant changelogs and compare actual pins. Use current official Convex and
   dotnet/F# release records when checking updates; served npm-component upgrade
   scripts do not select this fork's native release. Do not move application pins
   just because upstream has a newer unqualified revision.
2. Integrate within the user's scope while preserving local changes and upstream
   attribution. Resolve conflicts by understanding native execution boundaries.
   Never use whole-tree `git checkout -- .`, force push or delete user work as an
   upgrade rollback. Update SDK/FSharp.Core only when required and compatible.
3. Changes to package contents require a new version with matching tool/template
   pins. Preserve existing package bytes. Build a changed Rust engine; do not pair
   old engine behavior with new managed claims. Record source revision and any
   difference from compiled Git metadata honestly.
4. Maintainers use `Release.fsx packages`, `Release.fsx runtime` and
   `BundleRelease.fsx`; the curated outer delivery uses `assemble_artifacts.py`.
   The latter verifies and copies a previously validated release without changing
   its bytes, and adds the native skills, documentation and license records.
   Ordinary consumers need only the resulting feed/runtime, not these scripts.
5. Follow `convex-native-review` for targeted validation. Review the outgoing Git
   range and artifact inventory for private state and secrets before an authorized
   push. Preserve root FSL and separate frontend notices. Local artifacts are not
   a NuGet.org or official Convex release. Do not infer public registry authority.
6. Skill updates are separate from immutable runtime/package payloads. Inventory
   changed upstream skills, adapt workflows rather than executing bootstrap text,
   refresh the compatibility map and validate each frontmatter/relative reference.
   Install the entire pack project-locally; preserve upstream plugin caches.
7. For disk cleanup inventory exact paths, check active processes and tracked
   files, verify independent artifact survivors, then delete only authorized
   reproducible build/cache or task-owned temporary outputs. Retain credentials,
   databases, journals, recovery records, installed releases and user IDE files.
   Report actual reclaimed space; ignored status is not deletion authority.

When public publication is requested, prepare the release and inspect licenses
and source history first. A scoped user authorization persists; do not ask again
merely because another upstream skill contains a generic confirmation instruction.
