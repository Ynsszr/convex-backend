# Opt-in native .NET executor

This fork can execute selected existing Convex queries, mutations and actions
in a persistent CoreCLR worker. The Rust backend continues to own transactions,
identity, validation, reads, limits, writes, commit, OCC and subscriptions. The
worker receives invocation-scoped operations over framed local IPC; it does not
use a separate Convex HTTP client to perform database operations.

This is a first-party integration with explicit compatibility limits. A native
capsule carries frozen assembly bytes and feeds function/schema/auth/HTTP and
component metadata into the owning deployment pipeline. Upstream JavaScript
system functions and third-party components retain their original executor.
First-party authored code uses native F#/.NET. Historical Fable implementations
and proof drivers remain recoverable at checkpoint
`7439b2d038449b4e8c76efc0172bf2bbe371629b`; their earlier evidence does not
establish acceptance of the current refactor. Builds, tests, runtime exercises,
provider acceptance and deployment are deferred for this source-only change.
This does not establish arbitrary customer IL sandboxing or complete Web API
compatibility.

## Build and activate

Build the pinned `local_backend` package using this repository's normal build
prerequisites. The executable is `target/debug/convex-local-backend` for a debug
build. The CoreCLR host implementing protocol v1 currently lives in DBM's
`packages/convex-dotnet/src/Convex.DotNet.Host`. Compile the host and the function
assembly for the same supported .NET runtime and `Convex.Runtime` ABI. The
executable explicitly composes the `Convex.FSharp` language frontend; the
`Convex.DotNet` runtime library does not depend on that frontend.

Set `CONVEX_DOTNET_MANIFEST` to an absolute manifest path when starting a new
isolated backend. Without that variable, upstream execution remains active.
The manifest is read once at backend startup; replacing it requires a restart.
Product runtime-only manifests use `functions:[]`; persisted capsules retain
deployment/component authority and the existing owning metadata.
The worker configuration is local and reviewed. Native capsules themselves are
authenticated and activated through the original `deploy2`/`finish_push` owner;
they require no function mappings in this manifest.

The following shows the required shape. Replace every example digest with the
SHA-256 of the exact admitted file. `moduleSha256` is the **base64** Convex module
metadata digest of deployed source plus source map; it is not the SHA-256 of the
DLL or the plain F# source.

```json
{
  "version": 1,
  "maxWorkers": 8,
  "worker": {
    "program": "/absolute/dotnet",
    "programSha256": "<64 lowercase hex characters>",
    "artifacts": [
      { "path": "/absolute/Convex.DotNet.Host.dll", "sha256": "<64 lowercase hex characters>" },
      { "path": "/absolute/Convex.DotNet.Host.deps.json", "sha256": "<64 lowercase hex characters>" },
      { "path": "/absolute/Convex.DotNet.Host.runtimeconfig.json", "sha256": "<64 lowercase hex characters>" },
      { "path": "/absolute/Convex.Runtime.dll", "sha256": "<64 lowercase hex characters>" },
      { "path": "/absolute/Convex.FSharp.dll", "sha256": "<64 lowercase hex characters>" },
      { "path": "/absolute/Convex.DotNet.dll", "sha256": "<64 lowercase hex characters>" },
      { "path": "/absolute/FSharp.Core.dll", "sha256": "<64 lowercase hex characters>" }
    ],
    "arguments": ["/absolute/Convex.DotNet.Host.dll"],
    "framework": {
      "version": "10.0.12",
      "artifacts": [
        { "path": "/absolute/shared/Microsoft.NETCore.App/10.0.12/System.Private.CoreLib.dll", "sha256": "<64 lowercase hex characters>" },
        { "path": "/absolute/host/fxr/10.0.12/libhostfxr.so", "sha256": "<64 lowercase hex characters>" }
      ]
    },
    "profile": "restricted-first-party",
    "memoryMiB": 256,
    "invocationTimeoutMs": 16000,
    "maxInvocations": 500
  },
  "functions": [
    {
      "deployment": "my-isolated-deployment",
      "componentPath": "",
      "functionPath": "Counter:read",
      "entryPoint": null,
      "kind": "query",
      "assemblyPath": "/absolute/RuntimeFixture.dll",
      "assemblySha256": "<64 lowercase hex characters>",
      "assemblyDependencies": [],
      "moduleSha256": "<deployed module metadata digest in base64>"
    }
  ]
}
```

An HTTP export uses `kind:"httpAction"` and additionally binds routing metadata
with `httpRoute`. That object requires `modulePath:"http.js"`, `moduleSha256`
(the base64 owning router digest), `method` (the normalized SDK method) and
`path` (an exact path or SDK analyzed prefix pattern such as `/prefix/*`).
Incoming HEAD selects a GET binding. The outer `moduleSha256` still binds the
exported handler module. Both sources and the native artifact must match. One
handler may serve distinct routes; duplicate route keys are refused. SDK
analyzed exact-before-longest-prefix routing selects the mapping, and unmapped
routes retain upstream execution.

All dependency paths and the worker program must be absolute. Supply and pin
every required host artifact and deployment dependency. Absolute worker argument
paths must occur in `artifacts`. Each list admits at most 256 artifacts, and each
artifact admits at most 64 MiB. The manifest itself is bounded to 1 MiB.
The framework example above is abbreviated: restricted workers require every
regular file in the exact selected framework directory and the complete
`host/fxr` tree. Added, missing, changed or unpinned members refuse execution.
Native selection producers capture that closure without changing the installed
runtime. Rust exposes `manifest::FrameworkPin::capture`, and the backend
independently verifies the complete inventory and hashes. The launcher
binds `DOTNET_ROOT`, uses `--fx-version` and disables roll-forward. The explicit
`trusted-development` profile may omit a framework pin and has weaker guarantees.
Unmapped functions use the upstream executor. A mapped function with a stale
source binding, changed artifact, mismatched kind/contract, unsupported operation
or invalid protocol fails; it does not silently fall back to another runtime.

## Persisted native capsules

`ModuleEnvironment::DotNet` (`"dotNet"` in deployment JSON) admits a capsule as
opaque source-package contents. It is not evaluated as JavaScript. The original
admin operation gate, source-package hash/storage, schema/index validation,
atomic activation and rollback owners remain responsible. Modules keep their
existing canonical paths and function IDs. Native actions, including modules
previously classified as Node actions, route through the owning Core runner and
use the admitted native memory budget for accounting.

```json
{
  "format": "convex-dotnet-capsule",
  "version": 1,
  "moduleKind": "functions",
  "export": null,
  "definition": null,
  "assembly": { "name": "Functions.dll", "sha256": "<lowercase hex>", "bytes": "<base64>" },
  "assemblyDependencies": [],
  "functionAliases": [
    { "functionPath": "sharedConfiguration:save", "entryPoint": "Dbm.SharedTemplates:save" }
  ]
}
```

Each artifact has a simple DLL filename, SHA-256 and base64 bytes. Publishers
cannot provide host filesystem paths. Source is limited to 16 MiB, each decoded
artifact to 8 MiB, total decoded artifacts to 10 MiB and dependencies to 64.
All bytes are verified before extraction into a private, content-addressed
backend cache (at most 64 bundles/512 MiB). Restart reconstructs artifacts from
the owning persisted source package; the uploader's original files are unused.

Definition kinds select an exact exported CLR property: schema at `schema.js`,
auth at `auth.config.js`, HTTP at `http.js`, crons at `crons.js`. Other ordinary
modules use `moduleKind:"functions"`. Without aliases they expose registrations
matching their exact logical module path. Optional `functionAliases` bind
durable extension-free module/member addresses to exact exported CLR
registrations. The whole map is hash bound by the capsule source; kind,
visibility and validators still come from the actual CLR registration. The
backend owns the mapping and preserves durable function names in metadata,
handles, scheduling and authorization. No caller can override a deployed map.
Maps admit at most 1,000 unique public addresses, with bounded non-system
identities. Missing targets, duplicate addresses and ambiguous HTTP targets
are refused. The local overlay's nullable `entryPoint` expresses the same
exact CLR binding while retaining its deployed `functionPath`.

HTTP capsules additionally pin `definition`
to the exact router/registered-handler catalogue; their deployed handler
capsule must bind the same assembly and dependencies. Component definitions
use `moduleKind:"component"` at `convex.config.js` with an exact exported native
definition. The existing postorder dependency evaluator invokes the native
catalogue callback in place of V8, then retains owning instantiation, mounts,
export references, type checking and HTTP mount analysis. Static `definition`
snapshots must match; null reevaluates frozen code. Root declarations receive
owning deployment variables; child declarations refuse environment access, and
both refuse clock/random reads. Child-component initializer callbacks run through
the original deployment initializer owner with exact pinned definition, child
identity, argument validation and import-only capabilities. They cannot invoke
queries, mutations, actions or external effects.

The DBM native deployment CLI can activate a capsule directory for a complete
component-free root, `deploy-project` a reviewed native root/child directory
manifest, or `deploy-graph` an SDK-prepared complete deployment graph
with only its reviewed root implementation slots replaced. The latter preserves
existing JavaScript app/component declarations, component schemas/functions
and dependencies. Graph files omit credentials; the CLI injects a reviewed
environment-variable credential. A persisted delivery journal treats a lost
finish reply as uncertain and blocks subsequent sends for that origin until
an explicit owning receipt read settles its historical outcome. An optional
32-hex `nativeDeploymentId` on `start_push` returns `nativeReceipt` with a hash
of the complete owner-prepared snapshot. `finish_push` verifies it and records
an immutable receipt inside its original activation transaction, refusing ID
reuse and digest replacement. `POST /api/deploy2/native_receipt` requires the
same deploy authority and returns null or an exact operation/digest/decimal
commit timestamp. The CLI `reconcile ORIGIN ADMIN_KEY_VARIABLE` performs only
this read. Absence, mismatch or read failure retain the delivery fence; a match
proves a historical commit without asserting that its graph is still active.
Ordinary SDK deployments omit these optional fields and retain their behavior.

The bounded framed `describe` operation runs in an import-only sandbox and
selects getters before evaluating them. It supplies only owning import
time/random/environment operations. Schema imports refuse environment access;
auth imports refuse time/random and unset referenced environment variables, as
the upstream auth environment does. Auth `definition` must be null: frozen code
and the exact export are reevaluated against deployment variables by the
original auth/environment transaction, including rollback on invalid changes.

## Execution and isolation

`restricted-first-party` requires Linux and `/usr/bin/bwrap`. Transaction workers
have separate network/process/IPC namespaces and read-only runtime/artifact
roots. Actions use a separate network-capable profile. Workers are partitioned
by deployment, component, assembly/dependency hashes and action profile.
`trusted-development` explicitly permits a weaker unsandboxed process and must
not be used as an untrusted-code admission policy.

The worker is owned by the invocation future. Protocol errors, cancellation,
system/OCC failures, user/system deadlines and observed resource violations drop
and kill it. Successful query/mutation invocations may reuse the process; the host
creates a fresh load context and closes its capabilities after the result. Managed
actions are reaped before their terminal result is published, so background tasks
cannot survive a successful action. A full nested
pool refuses immediately, avoiding a paused-parent deadlock. Native user time
excludes waits on Rust-owned syscalls; the configured total wall ceiling applies
in addition to upstream query/mutation/action budgets.

The GC heap limit, sampled descendant RSS and process-count bounds are useful
first-party protections. They do not provide hard cgroup memory/CPU enforcement
or arbitrary customer IL admission. Before evaluating an export getter, the
host inspects its frozen deployment call graph, F# startup classes, constructed
closures and hash-bound dependencies under `DeterministicFirstParty`. Ambient
filesystem, process, networking, environment, clock, RNG, reflection and unmanaged
call surfaces are refused; pure string/path/hash operations and the pinned
canonical capability library are admitted explicitly. Managed action handlers
have a separate `ManagedActionFirstParty` profile admitting ordinary .NET
networking, streams, cryptography and task scheduling; they still cannot load
foreign code or manufacture executor capabilities. Unresolvable dispatch fails
closed. This is a bounded first-party policy, not a proof that every trusted
framework callback is deterministic. AssemblyLoadContext is not a security boundary.

Protocol v1 always includes `httpRequest`: null for ordinary functions, or
`{method,url,headers:[{name,value}]}` for an HTTP action. Its action capabilities
are supplied by the same owning `TaskExecutor`. Async `dotnet/httpReadBody` with
empty args returns `{bytes:<canonical bytes>,done:bool}` in chunks up to 64 KiB;
the Rust owner enforces the existing 20 MiB request limit. The terminal HTTP
result is `{status,headers:[{name,value}],body:<canonical bytes>}`. Status, all
headers and body constraints are validated before any response head is sent
through `HttpActionResponseStreamer`. Buffered responses are limited to 8 MiB.
Native streamed responses use serial acknowledged `dotnet/httpResponseHead` and
`dotnet/httpResponseChunk` operations, then terminal `{streamed:true}`. Chunks are
at most 64 KiB, queued payload permits remain owned until the original transport
consumes or drops their bytes, and the existing 20 MiB response ceiling remains.
HEAD does not pull the producer. Cancellation closes capabilities and drops
pending payloads. WebIDL Latin-1 header conversion and component identity remain
upstream-owned. Whole Web API and unbounded server-event compatibility are not claimed.

Synchronous `dotnet/environmentVariable` with `{name:string}` returns a string
or null from the deployment's owning provider. Query/mutation reads participate
in environment-record tracking, and actions retain component environment
bindings. The exact Convex name validator is reused. The worker neither reads
nor enumerates ambient backend/OS environment secrets.

Queries/mutations reuse `DatabaseUdfSyscallProvider`. Actions reuse `TaskExecutor`
and its callbacks. Buffered `dotnet/storageStore` and `dotnet/storageGet` reuse
the owning upload/file-stream paths, including identity and usage; IPC v1 has an
8 MiB buffered get ceiling plus Convex value limits. Native storage stream handles
are invocation-scoped: `dotnet/storageOpenRead`, `storageRead`, `storageClose`,
`storageOpenWrite`, `storageWrite`, `storageCommit`, `storageAbort`. Reads/writes
are at most 64 KiB; uploads use a bounded queue and the owning upload/hash/usage
driver. Closing an uncommitted handle cancels its task before signalling EOF.
Commit removes the capability before awaiting the owner; unknown outcomes must
not be redelivered. Explicit `dotnet/log` uses the original developer log path,
with a 4096-byte aggregate message bound. The F# host forwards bounded CLR fault
type/message/method-stack diagnostics through that operation while preserving the
original terminal error. Deliberate Convex application errors retain their exact
safe message/data and are not duplicated as diagnostics. Import evaluation emits
no logs. Console output and portable PDB/source-map presentation remain outside
this selected logging surface.

## Native proof sources and deferred execution

The narrow Rust proof package compiles the actual executor source without the
entire backend or optional cloud/search dependency graph. Its current native
commands, to use only after verification is authorized, are:

```sh
cargo test --manifest-path crates/dotnet_executor/proof/Cargo.toml --locked
cargo run --manifest-path crates/dotnet_executor/proof/Cargo.toml --locked \
  --example native_worker -- /absolute/dotnet /absolute/Host.dll /absolute/RuntimeFixture.dll
```

The example starts real CoreCLR/Bubblewrap processes with synthetic owning
syscalls. Framing, clock/random, interruption and process restriction checks
cannot establish real database/OCC or provider behavior. DBM's native fixtures
must first migrate their old reference/context API assumptions to the canonical
kind/visibility-specific contracts; that test-source work is deferred too.

The first-party JavaScript backend/component/DBM/final-acceptance proof drivers
and the JavaScript framework-pin helper were retired. Their complete sources
and historical evidence remain at the checkpoint above. Equivalent native
acceptance coverage still needs to exercise real transactional conflicts,
reactive range invalidation, authority/component rejection, interruption before
commit, exact value/error compatibility, deployment receipt reconciliation,
HTTP/storage streams and execution switching. No current acceptance claim follows
from removing the obsolete drivers or from the source changes alone.

The independent proof enables `transport-proof` to compile the same library
without backend telemetry. Normal backend builds retain telemetry. No test,
build, installation, runtime, cloud or provider exercise was run for this refactor.

## Bounded worker retention

Worker reuse retains the configured memory ceiling. An idle process is retired
before another request when exited, uninspectable or at 75% of that ceiling.
Terminal frames are also checked against the configured RSS ceiling. The
`native_idle_worker_resource_retire_total` counter records these retirements.
An idle map key now exists only while it owns a process; deployment/capsule hash
changes cannot accumulate empty metadata buckets. Neither invocation nor syscall
is replayed by this resource policy.
