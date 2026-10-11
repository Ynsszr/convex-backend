namespace ConvexComponent
open Convex.FSharp
type Counter = { Value: int }
type Increment = { Amount: int }
module Tables =
    let counters = Table.define<Counter> "counters"
