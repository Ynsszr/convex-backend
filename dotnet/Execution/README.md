# Convex.DotNet

Native .NET assembly admission, frozen capsule packaging, deployment and managed execution for the Convex backend. This component is owned by the native Convex repository.

`src/Convex.DotNet` is the reusable package; `src/Convex.DotNet.Host` is the managed worker; `cli` is the deployment tool. The sibling `../Runtime` and `../FSharp` projects supply neutral runtime contracts and the F# frontend. `RuntimeDistribution` prepares a selected runtime deployment from the installed runtime.

Open the root `../../Convex.slnx`. See `../README.md` for package and runtime distribution. Exercised release checks are recorded in [implementation status](../IMPLEMENTATION_STATUS.md).
