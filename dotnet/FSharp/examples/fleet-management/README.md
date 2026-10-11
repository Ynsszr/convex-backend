# Computer fleet management

Computer desired/observed state, heartbeat, indexed fleet reads, logs and reconciliation.

This optional example is native F#/.NET. [Domain.fs](fsharp/Domain.fs) owns DTOs/table identities, [Functions.fs](fsharp/Functions.fs) owns backend functions, and [NativeDeclarations.fs](fsharp/NativeDeclarations.fs) owns native schemas/aliases/exports. The `fsharp/Contracts/` and `fsharp/ClientContracts/` projects keep generated clients handler-free.

From this repository root, after building the example:

```sh
dotnet build dotnet/FSharp/examples/fleet-management/fsharp/Fleet.fsproj
dotnet fsi dotnet/tools/PackageExamples.fsx
```

`PackageExamples.fsx` writes generated capsules and client contracts under `artifacts/native-examples/fleet-management/`. That directory is build output. Deleting it does not remove the example.

A successful build is compilation evidence. Exercised runtime behavior is recorded in [implementation status](../../../IMPLEMENTATION_STATUS.md).
