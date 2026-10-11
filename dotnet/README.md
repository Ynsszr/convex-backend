# Convex for F# and .NET

Community-maintained native F# execution on the Convex backend, with ordinary NuGet packages and an independently installed runtime. Current source release: **0.3.1**.

Start with [application development](DEVELOPMENT.md). [Capabilities](CAPABILITIES.md) defines the implemented and exercised boundaries; [release scope](RELEASE_SCOPE.md) states the supported platform and non-claims. [Implementation status](IMPLEMENTATION_STATUS.md) and `preview-artifacts.json` record the actual shipped artifacts/checks.

| Directory | Consumer artifact |
| --- | --- |
| Runtime | Convex.Runtime |
| FSharp | Convex.FSharp |
| Client | Convex.Client: HTTP, shared reactive/optimistic client and pagination |
| Execution | Convex.DotNet and Convex.DotNet.Cli |
| Templates | Convex.Templates: full-stack app and reusable component |
| tools | Maintainer package, runtime, archive and consumer-check producers |

Open Convex.slnx only for dependency maintenance. Applications do not reference it. Native queries/mutations retain Convex's transaction owner; native actions own external effects. There is no Fable application path. Upstream Rust, V8 and JS implementation assets remain in their owning dependency, not copied into F# applications.

This is not official managed Convex Cloud support. Retain the original licenses and follow [publication guidance](PUBLISHING.md).
