# Native Convex for F# and .NET

F# functions execute as CLR assemblies on the maintained Convex backend fork.
This is a community runtime, not an official Convex Cloud feature.

Packages: Convex.Runtime, Convex.FSharp, Convex.Client, Convex.DotNet,
Convex.DotNet.Cli, and Convex.Templates. Install the matching release, run
`convex-dotnet new MyApplication`, then use
`dotnet convex-dotnet dev convex.project.json` and the generated F# web project.
Create reusable components with `convex-dotnet component new`.

Native graphs support nested components, referenced handler libraries and shared
authored codecs. The typed client supports coherent subscriptions, optimistic
mutation overlays, original-protocol replay/deduplication and reactive
pagination. Actions are not replayed automatically. Native
run/env/logs/import/export commands remain explicit operations.

Linux x64 with .NET 10 CoreCLR and bubblewrap is the tested runtime baseline.
Queries/mutations retain determinism and restricted native admission; arbitrary
third-party assemblies are not automatically safe to load. Read the matching
release's capabilities and scope before production deployment. Retain each
package's license metadata and upstream notices.
