# Native SDK troubleshooting

Start from this repository's SDK and package policy in `global.json`,
`Directory.Build.props` and `Directory.Packages.props`. A nested toolchain pin
or package-local npm/Fable lane is not a remedy.

| Failure                              | Owning check                                                                                                                                                   |
| ------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Frozen admission refusal             | Inspect the actual method, dependency, generic context and reachable callback; preserve import/query/mutation determinism and fail-closed unresolved dispatch. |
| Generated contract absent/incomplete | Run the canonical producer sequence; compile native shared contracts/backends, package, generate and compile clients/client-hosts before Browser.              |
| Invalid argument/result              | Compare actual canonical codec and validator ownership, exact fields, absence/null and callable kind/visibility.                                               |
| Missing dependency/runtime asset     | Inspect component map, project references and sealed closure rather than copying an arbitrary old DLL.                                                         |
| Mutation timeout/malformed response  | Retain Uncertain and reconcile the original operation; do not retry the effect automatically.                                                                  |
| Empty required proof selection       | Repair the actual registry/prerequisite selection; an empty required gate must fail.                                                                           |
| Source/artifact changed during proof | Preserve the original failed receipt and rerun against new exact inputs; do not edit it into a pass.                                                           |

[Integration](../INTEGRATION.md) and
[implementation status](../../IMPLEMENTATION_STATUS.md) own the current recipes.
A stale runtime file, PID, health response or source descriptor is not installed
acceptance.
