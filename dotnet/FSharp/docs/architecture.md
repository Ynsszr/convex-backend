# Native Convex.FSharp architecture guide

[The package architecture](../ARCHITECTURE.md) defines the current execution route and authority boundaries. [Integration](../INTEGRATION.md) defines canonical ownership and producer order. The former Fable/esbuild pipeline is retired.

The active source layer is organized around these owners:

| Source | Responsibility |
| --- | --- |
| `Values.fs`, `Codecs.fs`, `Reflection.fs`, `Contracts.fs` | Exact native values, derived representations and shared checked contracts |
| `FunctionReferences.fs`, `Functions.fs` | Callable kind, visibility, typed arguments/results and registrations |
| `Database.fs`, `Context.fs` | Query, transactional mutation and separately owned action capabilities |
| `Schema.fs`, `Declarations.fs`, `NativeConfiguration.fs` | Schema/auth/cron/router/component declarations |
| `NativeExecution/` | Invocation-scoped adaptation to the neutral runtime and original provider |
| Convex.DotNet | Frozen metadata, dependency and callback admission; deployment/capsule ownership |

IDs retain table markers. Two tables sharing one document shape require distinct explicit markers. Document patches keep field removal separate from assignment to null. Pagination must preserve continuation, completion and split metadata; a bounded first page is not proof that a collection is complete. Iterator owners close resources on completion, cancellation, decode failure and early exit.

Native handlers preserve Convex's original read set, OCC, transaction clock/randomness and commit decisions. Managed actions have separately admitted effects; their worker is retired before terminal delivery. Host callbacks, frozen dependencies and actual receiver dispatch participate in admission. The native worker is selected in this repository by `CONVEX_DOTNET_MANIFEST`. Upstream JavaScript execution remains the default when that variable is unset.

[The capability matrix](compatibility-matrix.md) records deliberate limits. Current semantic and installed gates are recorded in [implementation status](../../IMPLEMENTATION_STATUS.md). Historical test totals do not accept the current source.
