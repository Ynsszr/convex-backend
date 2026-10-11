# Order processing

Order and inventory decisions through queries, transactional mutations and separately owned actions.

This optional example is native F#/.NET. [Domain.fs](fsharp/Domain.fs) owns DTOs/table identities, [Functions.fs](fsharp/Functions.fs) owns backend functions, and [NativeDeclarations.fs](fsharp/NativeDeclarations.fs) owns native schemas/aliases/exports. The `fsharp/Contracts/` and `fsharp/ClientContracts/` projects keep generated clients handler-free.

From this repository root, after building the example:

```sh
dotnet build dotnet/FSharp/examples/order-processing/fsharp/OrderProcessing.fsproj
dotnet fsi dotnet/tools/PackageExamples.fsx
```

`PackageExamples.fsx` writes generated capsules and client contracts under `artifacts/native-examples/order-processing/`. That directory is build output. Deleting it does not remove the example.

A successful build is compilation evidence. Exercised runtime behavior is recorded in [implementation status](../../../IMPLEMENTATION_STATUS.md).
