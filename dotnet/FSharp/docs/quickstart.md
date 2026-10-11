# Full-stack native F# quickstart

Install a standalone Convex-for-F# release with `dotnet fsi Install.fsx`. Use its `bin/convex-dotnet` executable to create a project:

```sh
/path/to/installed/release/bin/convex-dotnet new MyApplication
cd MyApplication
dotnet convex-dotnet dev convex.project.json
```

In a second terminal:

```sh
dotnet run --project Web/MyApplication.Web.fsproj
```

The application restores ordinary NuGet packages from the installed release; no source checkout or product repository is required. The local dev command starts a loopback native backend, compiles/generates/deploys functions, watches source edits and preserves application-local data across restarts. `watch` is the separate build-only command.

The template contains shared Contracts, native Backend functions, console Client and WebAssembly Web projects. The browser uses deduplicated shared reactive list/count queries and ordinary typed mutations. HTTP `/health` uses the separate site endpoint. The example is anonymous; add your application's authorization policies before exposing it publicly.

Read [DEVELOPMENT](../../DEVELOPMENT.md) and [RELEASE_SCOPE](../../RELEASE_SCOPE.md) for all commands, runtime prerequisites and remaining advanced component/code-generation and optimistic-client gaps. These packages target a modified native backend, not unmodified Convex Cloud.
