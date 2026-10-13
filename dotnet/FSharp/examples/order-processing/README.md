# Order processing

Order and inventory decisions through queries, transactional mutations and separately owned actions.

This optional example is native F#/.NET. [Domain.fs](fsharp/Domain.fs) owns DTOs/table identities, [Functions.fs](fsharp/Functions.fs) owns backend functions, and [NativeDeclarations.fs](fsharp/NativeDeclarations.fs) owns native schemas/aliases/exports. The `fsharp/Contracts/` and `fsharp/ClientContracts/` projects keep generated clients handler-free.

From this repository root:

```sh
dotnet build Convex.slnx -c Release
```

The solution builds both example backends and the packaging producer before their client-contract projects. These build-order dependencies do not add backend assembly references to clients. The producer uses the selected build configuration and writes generated capsules and client contracts under `artifacts/native-examples/order-processing/`. That directory is build output. Deleting it does not remove the example.

To generate only the example packages and contracts, run `dotnet fsi dotnet/tools/PackageExamples.fsx` (Release by default; pass `Debug` to select Debug). It builds its own prerequisites, so no previous solution build is required.

A successful build is compilation evidence. Exercised runtime behavior is recorded in [implementation status](../../../IMPLEMENTATION_STATUS.md).
