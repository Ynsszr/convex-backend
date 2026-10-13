# Selecting and consuming a release

Set `CONVEX_FSHARP_RELEASE` to the **release** directory inside the artifact
pack, not to the outer artifact directory. This explicitly selects both the
local feed used for creation and the default runtime used by the local tool.

```sh
export CONVEX_FSHARP_RELEASE=/absolute/path/to/artifacts/convex-fsharp/0.3.2/release
dotnet "$CONVEX_FSHARP_RELEASE/runtime/cli/Convex.DotNet.Cli.dll" runtime inspect "$CONVEX_FSHARP_RELEASE/runtime"
dotnet "$CONVEX_FSHARP_RELEASE/runtime/cli/Convex.DotNet.Cli.dll" new MyApp /absolute/new/MyApp
cd /absolute/new/MyApp
dotnet convex-dotnet build convex.project.json
dotnet convex-dotnet dev convex.project.json --runtime "$CONVEX_FSHARP_RELEASE/runtime"
```

The last command starts a loopback backend and watches edits. `--once` performs
startup/build/deploy then stops; `watch` only builds/regenerates. API/site
defaults are 3210/3211; choose unused ports explicitly and update the web
endpoint if changing API port. The template configures a local NuGet feed and
local tool manifest; its package source mapping keeps `Convex.*` on that feed.
Moving the release requires updating that feed path and the release selection.

Alternatively run the release's `Install.fsx` for a verified per-user
installation. An explicit installation directory still needs
`CONVEX_FSHARP_RELEASE` set to it. Do not replace an application's active
runtime or migrate its state merely to install a package. Checksums establish
consistency, not publisher authenticity.

Linux x64 is the tested runtime target. The worker needs .NET 10, bubblewrap
0.9+ with `--size`, and working user/mount/network namespaces. Do not disable
isolation to bypass an unsupported host. Host cgroups and capacity planning
remain needed.

The native source pins SDK 10.0.401 and FSharp.Core 10.1.401 for 0.3.2. Verify
the selected release's requirements before overriding an application's SDK. Keep
the app's shared contracts handler-free. Application ProjectReferences are
allowed; ProjectReferences into the fork are not a package consumption strategy.
