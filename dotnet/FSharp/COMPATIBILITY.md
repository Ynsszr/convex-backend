Current limits and status: [STATUS.md](STATUS.md) and [implementation status](../IMPLEMENTATION_STATUS.md).

# Convex.FSharp native compatibility

The current implementation is **native F# on the selected .NET Convex backend**. Its runtime, SDK, product consumers, generators and browser application are authored in F#/.NET. The earlier first-party Fable, JavaScript and TypeScript execution paths are retired. Upstream Convex system implementations and third-party runtime dependencies retain their own languages.

**The inventory below is the dated source baseline from checkpoint `8b9d5971ca13411e9d613da5b96c3ae9e7579f82`.** It is not the 0.3.1 release record. Exercised checks are in [implementation status](../IMPLEMENTATION_STATUS.md) and [CAPABILITIES.md](../CAPABILITIES.md). Historical Fable, emulator, and earlier native totals must not be reused as acceptance for later source.

## Capability authority

[STATUS.md](STATUS.md), [the capability navigation](docs/compatibility-matrix.md) and [CAPABILITIES.md](../CAPABILITIES.md) describe capability owners, limits and unsupported surfaces. Historical auditor inputs are not part of this tree.

Each capability has two separate fields:

- `sourceStatus: source-implemented` means an owning source path and consuming flow are authored. It does not mean the compiler or runtime has accepted them.
- `acceptanceStatus: execution-acceptance-deferred` records the original source-only phase; current human execution selection supersedes that deferral.
- `sourceStatus: unsupported` identifies a retired or excluded surface, with its reason. Unsupported behavior must not be silently presented as an untested supported feature.

The ledger classifies every known limit and unsupported surface as intentional owner scope, original-owner responsibility, optional research, a superseded direction, an external condition or deferred execution acceptance. An actual accepted source gap must be labeled `accepted-source-gap` with its concrete missing behavior and owner; it cannot be hidden under those categories. These classifications do not establish runtime parity.

The ledger records no tested semantics at this source checkpoint. Its per-capability completion conditions keep the remaining acceptance work concrete without executing it or inventing a success label.

## Runtime and source ownership

The dependency direction is visible in the real projects:

| Owner | Responsibility |
| --- | --- |
| [Convex.Runtime](../Runtime/src/Convex.Runtime) | Language-neutral CLR execution interfaces, kinds, declarations and structured error ABI |
| [Convex.DotNet](../Execution/src/Convex.DotNet) | Native deployment capsules/catalogues, assembly loading and admission, invocation protocol and deployment receipt workflow |
| [Convex.FSharp](src/Convex.FSharp) | F# values/codecs, canonical validators, typed function/table/index references, capability contexts and native frontend adapters |
| [Convex.DotNet.Host](../Execution/src/Convex.DotNet.Host/Program.fs) | Thin host composition that selects the F# frontend and checks the owning handler contract |
| This repository | Original transactions, OCC/read sets, identity, persistence, indexes, scheduling, storage, reactivity and native worker ownership |
| [Convex.Client](../Client) | Typed HTTP reads/effects and bounded query subscriptions |
| [dotnet/tools](../tools) | Packaging, consumer checks and release preparation |

The runtime ABI contains CLR enums/interfaces, `Delegate`, `JsonElement` and `Task`; it does not expose F# application records, options or SDK context types. The F# frontend owns those representations. Query/mutation database operations remain inside the original Convex transaction; the managed worker cannot supply a commit decision or fabricated read set.

Physical project names have changed while assembly identities remain compatible. Shared contract assemblies contain the canonical contract types, and generated client assemblies depend on those contracts rather than backend handler projects.

## Supported source families

The ledger covers typed public/internal functions and HTTP actions; exact values, absence and errors; canonical custom/derived contracts; transactions and querying; search/vector/schema; identity and deterministic invocation environment; nested calls and components; scheduling; managed actions; storage/HTTP streams; native logs; worker admission/cancellation; deployment/receipt recovery; native client generation; HTTP/subscription clients; native browser consumers; build/package ownership; and preserved upstream/system implementations.

All these entries remain **source inventory entries whose current execution acceptance requires separate receipts**. The navigation page links the exact ledger IDs and owning files; the JSON contains the detailed limits and completion conditions.

## Deliberate boundaries

The current target is a selected first-party native execution policy. Queries and mutations receive deterministic capabilities. Managed actions have a broader admitted managed surface. Frozen dependency bytes, transitive IL/capture admission and the selected Linux process sandbox enforce additional boundaries; a collectible `AssemblyLoadContext` is not itself a security sandbox.

The current source does not claim arbitrary customer IL or unmanaged plugin execution, unmodified managed Convex Cloud runtime admission, a full browser Web API, unbounded HTTP streams, every CLR type's automatic codec, or the entire JavaScript optimistic-client ecosystem. Exact limits and the distinction between intentional policy and separate implementation/provider requirements belong to the ledger.

Persistent local IPC currently uses bounded length-prefixed JSON and a serialized syscall channel. Binary/multiplexed/batched IPC, mandatory NativeAOT and performance conclusions were discussed as possible engineering choices; this source inventory does not claim they are implemented or measured.

## Historical records

Earlier Fable toolchain notes, distribution instructions, and proof totals are historical. They are not the 0.3.1 acceptance record. A later backend or deployment upgrade still needs its own release and checks. This inventory does not change a live installation.
