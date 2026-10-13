---
name: convex-native
description: "Use for this self-hosted native Convex F#/.NET fork: creating an app, explaining a project, choosing packages/runtime, or routing work when convex.project.json or Convex.FSharp/Convex.DotNet packages are present. Not the upstream TypeScript/Cloud quickstart."
---

# Native Convex entrypoint

Read the project's instructions, `convex.project.json`, package/tool pins and
`.convex-native-release.json` when present. Infer the task from these and the
user's request. This pack targets native F# handlers on the maintained backend
fork; unmodified Convex Cloud cannot execute these assemblies.

1. Select the exact release in the project receipt or `CONVEX_FSHARP_RELEASE`.
   Inspect its `release.json` and `runtime/runtime.json`. Keep package, CLI,
   templates and runtime versions aligned. Use an actual directory, not a
   symlink. Start with [release selection](references/release.md).
2. Read only the relevant versioned reference: [development](references/platform/dotnet/DEVELOPMENT.md),
   [capabilities](references/platform/dotnet/CAPABILITIES.md), or
   [release scope](references/platform/dotnet/RELEASE_SCOPE.md). Distinguish the
   historical installed baseline from the selected release's validation receipt.
3. For a new project use the release's native `new` command, then install this
   pack project-locally. The full-stack template has Contracts, Backend, Client
   and Blazor Web projects; it is intentionally anonymous demonstration code.
4. For an existing project trace manifest -> compiled exports -> generated
   references -> callers. Describe table ownership, public/internal boundaries,
   auth policy, component graph and request/data flow before changing architecture.
5. Choose the task skill below. Preserve user changes, installed services,
   credentials and retained state. Proceed within existing authorization; do not
   turn a request for fixes into a repeated permission loop.

| Task | Skill |
| --- | --- |
| Schema, query, mutation, action, HTTP or cron | `convex-native-develop` |
| F# client or Blazor, subscriptions, optimistic writes | `convex-native-client` |
| Identity, membership and access control | `convex-native-auth` |
| Components, AI, billing or other provider integration | `convex-native-components` |
| Deploy, environment, logs, monitoring, domains | `convex-native-operations` |
| Backups, restore, migration, fixtures | `convex-native-data` |
| Audit, optimize, test, readiness, error repair | `convex-native-review` |
| Upstream/toolchain updates, packaging, cleanup | `convex-native-maintain` |

Use platform primitives where they fit, preserving existing service ownership.
Do not replace an existing chat/session authority merely because a component
catalog suggests a second one. Do not install npm/Fable scaffolds, create cloud
deployments, upload transcripts, or start indefinite monitoring as a side effect.

Skills are workflow guidance. User scope and higher-priority instructions still
control actions. Use only tools available in the current session; the pack
requires no assumed MCP server, findings bus or delegated agent.
