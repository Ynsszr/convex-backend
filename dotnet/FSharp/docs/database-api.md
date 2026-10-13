# Database and pagination

QueryCtx exposes read authority; MutationCtx exposes transactional writes;
ActionCtx performs database work through typed nested query/mutation calls.
Preserve Convex's original transaction, read set, OCC and commit owners.

Define tables through Table.define or Table.defineWithMarker. Document metadata
is separate from document payload; use the native document-aware APIs instead of
inventing optional `_id` fields to imply insertion authority. Distinct tables
sharing a shape need distinct immutable markers. Canonical patches distinguish
removal from assignment to null.

Indexes and search declarations belong to the schema owner. A typed index
carrier does not by itself prove that its name and fields match a declared
index. Read [Database.fs](../src/Convex.FSharp/Database.fs) and
[Schema.fs](../src/Convex.FSharp/Schema.fs) for actual overloads and exact
contracts. The fleet example's
[declarations](../examples/fleet-management/fsharp/NativeDeclarations.fs) and
[functions](../examples/fleet-management/fsharp/Functions.fs) are current
consumers.

Use bounded reads or cursor traversal. Retain completion and split metadata; a
first page or Take limit is not the whole collection. Await iterator Close after
success, early exit, cancellation and decoding failure. Current tests and real
native provider acceptance have separate evidence identities.
