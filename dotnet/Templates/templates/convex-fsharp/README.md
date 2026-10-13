# ConvexApp — full-stack native F#

Open `ConvexApp.slnx`. This application contains shared Contracts, native
Backend functions, a console Client and a WebAssembly Web frontend, all authored
in F#.

The installed Convex-for-F# CLI created NuGet.Config and restored its local
tool. Start the backend in one terminal:

```sh
dotnet convex-dotnet dev convex.project.json
```

Start the frontend in another:

```sh
dotnet run --project Web/ConvexApp.Web.fsproj
```

The frontend uses `Web/wwwroot/appsettings.json` for the Convex API URL. API and
HTTP-site endpoints are separate. This starter uses API port 3210 and site port
3211 unless dev flags override them.

The list and visible count subscribe over one shared reactive client. Open a
second browser tab and send a message; both tabs update. The queries
deliberately display at most 50 messages. The demo is anonymous and needs
application-specific authorization before publishing to untrusted users.

`.convex/dev` holds private local database, identity, logs and deployment state.
Do not commit it. Saving backend source rebuilds and deploys locally. Ctrl+C
stops the local runtime without deleting the database.
`dotnet convex-dotnet watch convex.project.json` only builds/regenerates without
starting a backend.

Advanced platform capabilities and restrictions are described in the installed
release's docs/RELEASE_SCOPE.md. This is the community native runtime, not
support for uploading .NET functions to unmodified Convex Cloud.
