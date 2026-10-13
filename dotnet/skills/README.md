# Agent skills for native Convex

These skills target this community F#/.NET fork and its self-hosted runtime. They
are maintained independently of the official Convex plugin. Install the entire
pack into a native application's `.agents/skills`; start with `$convex-native`.
The installer adds a bounded routing section to `AGENTS.md`, preserves existing
instructions, and refuses to replace different skills. It never edits the
installed plugin or global agent configuration.

The distributable pack includes versioned platform documentation. Build it with
`dotnet/tools/assemble_artifacts.py`; source skill folders alone are not a release.
`upstream-review.json` inventories all 33 local Convex skills and eight installed
Convex plugin 2.0.1 skills reviewed on 2026-10-13, including individual hashes and
the native replacement for every workflow. `COMPATIBILITY.md` explains the changes.

| Skill | Task |
| --- | --- |
| convex-native | Identify the fork, select a release, create an application, route work |
| convex-native-develop | Typed functions, contracts, schemas, indexes, HTTP, scheduling |
| convex-native-client | Reactive F# clients, Blazor, authentication and optimistic state |
| convex-native-auth | OIDC integration and application authorization |
| convex-native-components | Native component graphs and external integrations |
| convex-native-operations | Deployment, reconciliation, secrets, logs, hosting |
| convex-native-data | Backup, restore, schema migration and seeding |
| convex-native-review | Evidence-based audits, focused tests and readiness |
| convex-native-maintain | Version updates, release production and safe cleanup |

The pack carries original fork-specific instructions, not copies of upstream
skills or their remote bootstrap scripts. The repository's existing licensing
and attribution boundaries apply; this is not an official Convex skill release.
