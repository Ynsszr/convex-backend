# Convex for F# — application development

## Install and create

Extract the selected release archive and run `dotnet fsi Install.fsx`. The installer verifies its inventory and installs under `~/.local/share/convex-fsharp/releases/<version>`. Linux x64, .NET 10 and bubblewrap are the tested runtime prerequisites.

```sh
~/.local/share/convex-fsharp/releases/0.3.1/bin/convex-dotnet new MyApplication
cd MyApplication
dotnet convex-dotnet dev convex.project.json
```

In a second terminal, run `dotnet run --project Web/MyApplication.Web.fsproj`. The generated NuGet.Config and local tool manifest select the installed release; no source checkout or copied packaging script is required.

`dev` owns a loopback native backend and application-local `.convex/dev` state. It compiles, generates and deploys edits only to that local instance. It preserves data across restart. `watch` performs build/generation without deployment; `build` is one pass. `dev --once` runs the local startup/build/deployment pass and stops. Remote deployments always require an explicit deploy command and selected administrator credential variable.

## Application manifests and components

A root manifest declares version 1, `project`, `output`, `schema`, `component`, `contracts`, `clientNamespace`, and `modules`. Optional fields include `configuration`, `auth`, `crons`, `http`, and `components`. Unknown or duplicate fields are refused. Filesystem paths are build inputs; deployed function/component identities remain explicit.

```json
{
  "version": 1,
  "project": "Backend/MyApplication.Backend.fsproj",
  "output": "artifacts/convex",
  "schema": "MyApplication.Backend.Declarations:schema",
  "component": "MyApplication.Backend.Declarations:app",
  "contracts": ["MyApplication.Contracts"],
  "modules": [{"path":"messages", "type":"MyApplication.Backend.Messages"}],
  "components": [{"definitionPath":"../counter", "manifest":"Components/Counter/convex.project.json"}]
}
```

Create a reusable component with `convex-dotnet component new Counter Components/Counter`. Add its manifest to the parent and mount `NativeComponentDefinitions.mount "counter" "../counter"` in the parent's F# app declaration. Build/deploy the parent graph. Components may themselves depend on other components; shared definitions are deduplicated and cycles/conflicting identities fail before a build.

A module can add `"assembly":"MyApplication.Extra"` to select a referenced handler library. A declaration selector can be a string or `{"assembly":"Library","export":"Namespace.Declarations:schema"}`. ProjectReferences belong only inside the application; the SDK remains a package.

Canonical argument/result contracts and custom codec owners live in handler-free contract assemblies named by `contracts`. Export an authored `BoundaryContract` as a static F# module value. Backend registrations use that same instance; generation preserves its exact owner, including nested array/nullable/refined compositions. Do not duplicate validator/codec definitions across server and client.

Generated `Api` functions are public root functions. `Internal` and parent `Components` references are not browser-callable. Component tables belong to their own namespaces. Export a parent query/mutation with appropriate authorization when a browser needs a component operation.

## HTTP, authentication and crons

Set `http` to an exported native HttpRouter; the producer binds its routes to portable Convex addresses while retaining exact CLR handler identities. The starter serves `GET /health` on the site endpoint, normally port 3211. HTTP-only projects may use an empty modules array. HTTP handlers do not become ordinary client query references.

`auth` selects a native authentication declaration evaluated against the owning deployment's environment; values are not captured by offline code generation. `crons` selects a native cron declaration. Queries/mutations remain deterministic; external effects belong in actions or explicit workers.

## Shared reactive state and optimistic writes

```fsharp
let selected = Reactive.query Api.messages.list ()
use client = new Reactive.Client(endpoint)
use subscription = client.Watch selected
use observer = client.Observe(fun snapshot ->
    match Reactive.Snapshot.read selected snapshot with
    | Reactive.Value.Ready messages -> render messages
    | Reactive.Value.Loading -> renderLoading ()
    | Reactive.Value.Failed error -> renderError error)
let running = client.RunAsync(cancellation)
let! result = client.MutateAsync(Api.messages.send, args,
    optimisticUpdate = Reactive.Snapshot.update selected updateMessages)
```

Queries share one connection. Observers read immutable snapshots after the whole server transition is applied. Optimistic updates are pure `Snapshot -> Snapshot` transformations; they rebase over newer server data and disappear on refusal or observed commit. The sync client retransmits mutations with the same session/request identity, using Convex's deduplication. Actions and direct HTTP operations do not acquire replay semantics. Stop/dispose outcomes preserve uncertainty for effects already dispatched.

Use `SetAuthentication(Some token)` when the identity provider issues or refreshes a user token, and `SetAuthentication(None)` on logout. The client does not store a password or fabricate identities. Old snapshots, cursors and pending authority are cleared on identity changes. Identity-provider UI/refresh integration is application configuration.

## Reactive pagination

```fsharp
use pages = new Paginated.Client<Row>(client,
    (fun options -> Reactive.query Api.items.page { Filters = filters; Pagination = options }),
    initialNumItems = 25)
use changed = pages.Observe(fun view -> render view.Results view.Status)
pages.LoadMore() |> ignore
```

The query factory places `AdvancedPaginationOptions` in the application's typed arguments; the backend returns `AdvancedPaginationResult<Row>`. Loaded ranges remain reactive. Journals survive reconnect; required/recommended splits replace a parent only when both child ranges are available. Authentication changes discard private cursors. Dispose the pager when changing filters or leaving the view. The pager does not own the shared client's lifetime.

## Native operations

```text
convex-dotnet run ORIGIN query|mutation|action MODULE:FUNCTION ARGUMENTS_JSON_FILE ADMIN_VARIABLE
convex-dotnet env list ORIGIN ADMIN_VARIABLE
convex-dotnet env set ORIGIN NAME VALUE_VARIABLE ADMIN_VARIABLE
convex-dotnet env remove ORIGIN NAME ADMIN_VARIABLE
convex-dotnet logs ORIGIN CURSOR ADMIN_VARIABLE
convex-dotnet tables ORIGIN ADMIN_VARIABLE
convex-dotnet export ORIGIN NEW_ZIP_FILE ADMIN_VARIABLE [--include-storage]
convex-dotnet import ORIGIN zip - FILE requireEmpty|append|replace ADMIN_VARIABLE
convex-dotnet import ORIGIN jsonArray|jsonLines|csv TABLE FILE requireEmpty|append|replace ADMIN_VARIABLE
```

Credentials and new environment values come from variable names, not command-line secrets. Environment listing prints names only. Import mode is mandatory; use requireEmpty for a fresh restore and explicitly choose replace only when overwriting data is intended. Export never overwrites an existing file. Streaming import/export is bounded to 2 GiB per command. A lost response does not authorize an automatic repeated import or mutation.

## Runtime and releases

`runtime inspect` checks a complete distribution without execution. `runtime prepare` freezes selected runtime inputs. `runtime run DISTRIBUTION DATA_DIRECTORY -- BACKEND_ARGUMENTS` starts only the selected runtime. Inventory checks reject incomplete/tampered/unlisted files, links and incompatible architecture. Checksums are integrity records, not publisher signatures.

Maintainers use the dependency-owned Release.fsx and BundleRelease.fsx; applications do not. The release archive carries packages, runtime, installer and documentation. Source history and private acceptance data are not bundled. Read RELEASE_SCOPE.md, CAPABILITIES.md and PUBLISHING.md before deploying beyond the tested baseline.
