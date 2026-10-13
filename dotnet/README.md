# Convex for F# and .NET

For shared packages, runtime outputs and the native agent skill pack, see
[artifact consumption](ARTIFACTS.md).

Community-maintained native F# execution on the Convex backend, with ordinary
NuGet packages and an independently installed runtime. Current source candidate:
**0.3.2**. The recorded installed release remains **0.3.1**; the candidate has
passed local package/runtime validation and has not been publicly released or
installed.

Start with [application development](DEVELOPMENT.md).
[Capabilities](CAPABILITIES.md) defines the implemented and exercised
boundaries; [release scope](RELEASE_SCOPE.md) includes deployment and upgrade
checks. [Implementation status](IMPLEMENTATION_STATUS.md) and
`preview-artifacts.json` retain the versioned artifact evidence; source changes
do not update those historical receipts.

The 0.3.2 candidate hardens client reconnect/subscription lifetimes,
deterministic date admission, local credentials and deployment journals,
external-source watching, and worker temporary-storage limits. See
[release notes](CHANGELOG.md) for compatibility details. Its Rust changes
require a newly built engine; the unchanged engine reuse documented for 0.3.1
does not apply.

| Directory | Consumer artifact                                                     |
| --------- | --------------------------------------------------------------------- |
| Runtime   | Convex.Runtime                                                        |
| FSharp    | Convex.FSharp                                                         |
| Client    | Convex.Client: HTTP, shared reactive/optimistic client and pagination |
| Execution | Convex.DotNet and Convex.DotNet.Cli                                   |
| Templates | Convex.Templates: full-stack app and reusable component               |
| tools     | Maintainer package, runtime, archive and consumer-check producers     |

Open Convex.slnx only for dependency maintenance. Applications do not reference
it. Native queries/mutations retain Convex's transaction owner; native actions
own external effects. There is no Fable application path. Upstream Rust, V8 and
JS implementation assets remain in their owning dependency, not copied into F#
applications.

This is not official managed Convex Cloud support. Retain the original licenses
and follow [publication guidance](PUBLISHING.md).
