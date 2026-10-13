---
name: convex-native-develop
description: "Implement native F# Convex schemas, typed functions, contracts, indexes, storage, HTTP handlers or scheduled jobs in a convex.project.json project. Use the CLR SDK and native manifest, not TypeScript decorators or Fable."
---

# Develop native functions

Read `convex-native` first and the project's manifest, Contracts and declarations.
Use [native development](../convex-native/references/platform/dotnet/DEVELOPMENT.md)
and the selected package's XML/API metadata for exact overloads. For a starting
pattern see [functions](references/functions.md). Never guess an F# API by
capitalizing a TypeScript method.

1. Define shared records/unions/IDs and canonical codecs in handler-free Contracts.
   Derive argument/result contracts, or export authored contracts once and use
   the same instances in registrations and generated clients. Preserve deployed
   module/function names when moving physical files.
2. Use `Convex.query`, `Convex.mutation`, `Convex.action` or their internal and
   `WithContracts` counterparts. Query/mutation effects remain with the original
   Convex transaction owner. External HTTP, provider calls and non-deterministic
   libraries belong in actions; persist results through typed mutation references.
3. Model each read path before selecting indexes. Bound reads with `Take`,
   `Unique` or pagination as appropriate; an index alone does not bound a hot
   partition. A residual filter on an already bounded range can be appropriate.
   Avoid unbounded arrays and broad read/write contention. Measure actual limits
   from this backend's configuration rather than copying Cloud tables.
4. Read transaction time/randomness through `InvocationRuntime`, not ambient
   `DateTime.Now`, timezone, OS or process state. In 0.3.2, date parsing/formatting
   and local conversions are refused even when a format appears invariant.
   Pass validated timestamps/components or perform presentation work in actions.
5. Author schema/auth/crons/HTTP/component declarations and select them in the
   manifest. Unknown manifest fields are refused. Use typed function references
   for scheduling; keep privileged callbacks internal unless their public
   contract is deliberately safe. Persist storage IDs, resolve URLs when needed.
6. HTTP uses the separate site endpoint and native router, not an invented
   Express-style path parameter. Validate request bodies, authorize effects and
   verify webhook signatures over the original bytes. Design retries/idempotency
   at the provider boundary; an action is not one atomic database transaction.
7. Run `dotnet convex-dotnet build convex.project.json` once after a coherent
   change. This compiles, packages and generates catalogs. Use focused tests for
   behavior that changed, and one disposable live probe for a new runtime boundary.
   Deploy to a selected target only within the user's authorized scope.

For retained data use `convex-native-data`; for security policy use
`convex-native-auth`. Do not silently remove exported functions while rewriting
a module, add a second state owner, or weaken admission to make compilation pass.
