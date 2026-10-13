# Native components and durable handles

Native component declarations use the canonical graph owner; root and child
definitions carry mount paths, arguments, schemas, dependencies,
exports/reexports and initializer metadata.
[NativeComponentDefinitions.fs](../src/Convex.FSharp/NativeComponentDefinitions.fs),
[NativeConfiguration.fs](../src/Convex.FSharp/NativeConfiguration.fs) and
[Component.fs](../src/Convex.FSharp/Component.fs) own these representations.

ComponentImport and Configuration composition retain exact graph paths.
Generated native catalogues bind actual exports/reexports and shared contracts;
do not recreate a JS configuration or infer codecs from validator JSON. A
component reference is a callable address with kind and argument/result
contracts, not permission to invoke it.

Typed durable handles retain original component identity. The native frontend
delegates actual namespace, environment, transaction and initializer behavior to
the original Convex component evaluator. Named root public references are the
public HTTP boundary; component handles use owning native contexts rather than
an invented public HTTP route.

Pinned upstream components may retain upstream JavaScript. This exception does
not restore authored TS/JS/Fable application code. Current root/child
deployment, initialization and native/upstream mixed-graph gates remain separate
from source declarations.
