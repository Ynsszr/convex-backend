# Native F# integration contract

## Independent platform ownership

`Convex.Runtime` defines language-neutral invocation, declarations and safe
errors. `Convex.FSharp` defines typed contracts, schemas, function registration
and the F# frontend. `Convex.DotNet` owns admission, packaging and deployment.
`Convex.DotNet.Host` composes the neutral runtime with the F# frontend. The Rust
backend owns transactions, subscriptions, scheduling, storage and commit
decisions.

Product applications reference exact NuGet package versions. They must not
reference the platform's project files or require a particular checkout layout.
Product-specific backend handlers and DTOs remain with the product. Native
generic examples and SDK tests belong here.

## API and execution

Use the shared handler-free F# contracts for arguments, returns, function
references and client decoding. Do not rebuild codecs or schemas in a
product-specific bridge. Queries and mutations retain the Convex transaction
context; action effects are separately owned. Errors and unknown outcomes do not
grant permission to repeat effects.

First-party F# functions execute as .NET assemblies, not transpiled JavaScript.
The existing `.js` capsule carrier paths are a native protocol detail. Upstream
runtime internals may still use their own languages.

Source candidate 0.3.2 restricts deterministic date/time entrypoints to explicit
calendar/tick construction, calendar arithmetic, supplied offsets and Unix
timestamp operations. Do not read implicit current dates through parsing or use
local-timezone conversion/formatting inside declarations, queries or mutations.
Parse/format before invocation or in an action; use
`InvocationRuntime.nowMilliseconds()` for the owning transaction clock. See
[development guidance](../DEVELOPMENT.md#calendar-operations-in-source-candidate-032)
for the compatibility boundary. Admission is a reviewed first-party policy, not
a guarantee for arbitrary framework code.

## Packages and runtime artifacts

The current source package candidate is `0.3.2`, centrally defined in
`../../Directory.Build.props`; it is not yet released or installed. The recorded
installed release remains `0.3.1`. The root `Convex.slnx` is the only
managed-platform solution. `../tools/Release.fsx` produces NuGet packages and a
platform-specific runtime bundle when explicitly invoked. Runtime preparation is
provided by `Convex.DotNet.Execution.RuntimeDistribution`.

A runtime bundle contains the backend executable, managed host, deployment CLI,
descriptor and license notices. It requires the declared .NET runtime and Linux
sandboxing tools. The recorded 0.3.1 packages and checks are in
[implementation status](../IMPLEMENTATION_STATUS.md). Running the producer on
this newer tree creates a new build. It does not reproduce those recorded bytes.

The 0.3.2 candidate changes the Rust engine and requires matching new
managed/runtime artifacts. Its restricted workers require bubblewrap 0.9+ with
`--size`; the default 256 MiB selection is split into 192 MiB for processes and
32 MiB each for `/tmp` and `/dev/shm`. Keep final source, package and live
acceptance evidence distinct.

No compatibility with a former external `packages/` directory layout is
maintained.
