# Convex.FSharp

Native F# language frontend for the Convex reactive backend. Functions compile to .NET assemblies; the Convex backend and native executor own their execution. There is no Fable compilation route.

The complete native platform is owned by this repository. Open `../../Convex.slnx`; `../../global.json` and the central root package/build policies select the toolchain. `../Runtime` owns neutral CLR contracts; `../Execution` owns assembly admission, managed execution, deployment and runtime distribution.

The package version in `Directory.Build.props` is `0.3.1`. Generic packages, the `dotnet new` template and developer CLI are documented in [the developer guide](../DEVELOPMENT.md). The installed release record is [implementation status](../IMPLEMENTATION_STATUS.md). Local installation is not a public registry release.

The generic client is a separate `Convex.Client` package in `../Client`.

## Ownership and layers

| Layer | Responsibility |
| --- | --- |
| [Convex.Runtime](../Runtime/src/Convex.Runtime) | Language-neutral .NET invocation and declaration ABI, function kinds, visibility and safe application-error contract |
| Convex.FSharp | Typed F# values, codecs, references, registrations, contexts, schemas, components and native frontend adapters |
| [Convex.DotNet](../Execution/README.md) | Frozen assembly admission, discovery, capsules, deployment and native protocol support; its runtime library references Convex.Runtime |
| Convex.DotNet.Host | Explicit composition of the neutral runtime with the F# frontend |
| Convex backend fork | Original transaction/action/deployment owners, bounded native workers and upstream system/component execution |
| Native client contracts | Shared handler-free contracts and generated F# function catalogues; Convex.Client supplies HTTP calls and shared reactive subscriptions |

The runtime extension has no application supervision or installation policy.
Application authorization remains with the owning application.

## One callable contract

Put public DTOs and their exact boundary contracts in an assembly that has no
backend handler or storage dependency. Backend registrations and clients share
that owner. A small contract module can declare:

```fsharp
module Example.MessageContracts

open Convex.FSharp

type EchoArguments = { Message: string }

let arguments = Contract.arguments<EchoArguments>()
let returns = Contract.derived<string>()

let echo =
    FunctionReference.publicQuery "messages:echo" arguments returns
```

The backend uses the same objects:

```fsharp
module Example.Messages

open Convex.FSharp
open Example

let echo =
    Convex.queryWithContracts
        MessageContracts.arguments
        MessageContracts.returns
        (fun _ args -> async { return args.Message })
```

Packaging binds the durable address to the exact compiled export. A reference
alone does not register or authorize a function. The six public/internal
query/mutation/action builders retain kind, visibility, arguments and result
types. Typed handles, nested calls and schedulers preserve those distinctions.
Only the heterogeneous native registry erases registrations to
`Convex.Runtime.IRegisteredFunction`.

`Contract.derived` and `Contract.arguments` use the canonical codec.
`valueWithValidator` and `argumentsWithValidator` retain that codec with an exact
validator. `refine`, `nullable` and `array` retain their source contract.
`create` and `map` express deliberate custom representations; export those
contracts from the shared owner so generation can bind their actual codecs.

## Native composition and generated clients

Native declarations expose schemas, auth, HTTP routes, crons and component
definitions without invoking registered handlers. Durable function aliases are
explicit; filesystem or assembly renames do not choose deployed identities.

`ComponentImport.create` binds a child declaration to its exact graph path.
`Configuration.app().Use(...)` composes mounts. The original component evaluator
retains namespaces, arguments, dependencies, environment and initializer
ownership. Generated F# catalogues include actual component exports and
reexports as well as root public/internal references.

The generic native libraries and example producers live in this
repository. Start with the [native platform guide](../README.md).

Ordinary applications use the installed `convex-dotnet` CLI described in
[the developer guide](../DEVELOPMENT.md). `dotnet/tools/PackageExamples.fsx`
packages the compiled examples in this repository and generates their F#
client contracts. It is an example producer. Its output under `artifacts/`
is generated and is not source.

Convex capsule filenames may retain `.js` because the original module envelope
requires those paths. Their contents are versioned `convex-dotnet-capsule` JSON
with the `dotNet` environment, pinned bytes and exact metadata.

## Database and execution capabilities

Table identities are immutable. Use `Table.defineWithMarker` when distinct
tables share a document shape. IDs retain their table type, and Convex checks
actual ownership. Table-scoped CRUD and `DocumentPatch` keep field removal
distinct from a null assignment.

Query APIs include indexed ranges, filters, full-text search, document metadata,
bounded reads, iterators and advanced pagination. Traversal helpers await
cleanup after completion, early exit, decode failure or cancellation; callers
that acquire an iterator directly own its `Close()`.

Queries have read authority. Mutations have transaction writes and atomic
scheduling. Actions have separately owned nested calls, storage, vector search
and managed service capabilities; they do not hold a database transaction.
Only transactional nested calls offer the owning transaction-limit/snapshot
options. Scheduling from an action is separately owned.

Managed actions and HTTP actions may use admitted .NET networking, streams,
filesystem operations, cryptography and task scheduling. Each action worker is
retired before its terminal result is published. Queries, mutations and imports
retain deterministic admission, tracked clock/random/environment inputs and
original Convex transaction semantics. See [STATUS](STATUS.md) for exact limits.

## Values, errors and clients

Canonical codecs preserve field names, tagged unions, int64, bytes, special
floating-point values, absence versus null and pending commit timestamps.
Ambiguous or unsupported CLR representations fail explicitly.
`ConvexWire.encode/decode` use Convex's tagged value protocol; `Http.json` uses
ordinary JSON, and `Http.convexJson` selects the tagged protocol deliberately.

The separate `Convex.Client` package owns reusable typed HTTP calls and shared
reactive query subscriptions. Calls distinguish `Completed`, safe `Refused` and
uncertain delivery outcomes. The shared client deduplicates subscriptions and
publishes whole server transitions as immutable snapshots; authentication changes
clear data from the previous identity. Effects are not resent merely because a
reply is missing. Native owning contexts also accept component references and
typed handles; the ordinary client currently selects public root references.

## Deployment and current status

The native deployment tool is
`dotnet/Execution/cli/Convex.DotNet.Cli.fsproj`. Its review and deployment
commands consume frozen capsules or a reviewed complete component graph.
Receipt reconciliation reads historical activation authority; it does not infer
current activation from an uncertain transport reply.

Use the installed release and [developer guide](../DEVELOPMENT.md) for ordinary
application creation, local development and deployment. The F# WebAssembly
starter, shared client and installed local runtime have been exercised together.
Read [release scope](../RELEASE_SCOPE.md) and [implementation status](../IMPLEMENTATION_STATUS.md)
for the exact checks, platform limits and remaining advanced-feature gates.
Dated STATUS/NEXT reports describe their recorded revisions, not a blanket
current-parity claim. Public distribution remains separate from local releases.

Historical documentation (archived in Git history)
preserves earlier instructions and execution reports without making them current
evidence. LICENSE, NOTICE and [PROVENANCE](PROVENANCE.md) retain the imported
package's provenance and distribution terms.
