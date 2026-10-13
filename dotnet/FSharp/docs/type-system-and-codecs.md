# Canonical native values and codecs

[Values.fs](../src/Convex.FSharp/Values.fs),
[Codecs.fs](../src/Convex.FSharp/Codecs.fs),
[Reflection.fs](../src/Convex.FSharp/Reflection.fs) and
[Contracts.fs](../src/Convex.FSharp/Contracts.fs) own native representations.
Checked argument/result contracts are shared by registration, generation and
clients.

| Value                          | Preserved meaning                                                                                                                                   |
| ------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------- |
| int64 / float                  | Full-width integer and floating representations remain distinct; reject overflow, fractions or precision loss when integral conversion is required. |
| byte[]                         | Convex tagged bytes, separate from ordinary JSON strings or arrays.                                                                                 |
| Id                             | String wire identity with immutable table ownership.                                                                                                |
| Records / DUs                  | Exact declared field names and discriminators; unsupported or unknown boundary fields are checked by the owning contract.                           |
| option / NullOr / OptionalNull | Absence, explicit null and value remain distinct where representable; ambiguous nested presence is rejected.                                        |
| unit arguments                 | Object envelope `{}`; ordinary unit values remain distinct from argument-envelope encoding.                                                         |
| CommitTimestamp                | Owner-supplied value/placeholder semantics; no authored JavaScript placeholder implementation.                                                      |

Custom create/map codecs must be exported from a handler-free owner so the
generator binds their actual mapping. Validator JSON alone cannot reconstruct
one. Automatic recursive schemas, unsupported CLR collections and unsafe enum
metadata fail explicitly; choose normalized types or a bounded explicit
contract.

Convex wire encoding and ordinary JSON projections are separate boundaries.
Preserve original serialized/hash inputs for journals and retained data.
[Compatibility](compatibility-matrix.md) records limits. Current codec behavior
is described in [STATUS.md](../STATUS.md).
