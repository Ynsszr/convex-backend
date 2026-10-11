# Native full-text and vector search

Search and vector indexes are native schema declarations. [Schema.fs](../src/Convex.FSharp/Schema.fs) owns field/index metadata, while [Database.fs](../src/Convex.FSharp/Database.fs) and [Context.fs](../src/Convex.FSharp/Context.fs) expose capability-specific operations. Match index names, search/vector fields and allowed filter fields to the selected schema.

Full-text queries retain the original backend search implementation and bounded result/traversal ownership. Vector search is an action capability with finite dimensions, values, result limit and filter inputs. Typed values do not authorize arbitrary index names or undeclared filter fields.

No Fable/esbuild bundling step is part of native search. Native packaging records the frozen declaration and handler closure. Real search ranking, vector filtering, schema migration and index readiness require current native provider tests; a source declaration or older isolate result does not establish them.
