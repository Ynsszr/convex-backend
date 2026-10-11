namespace ConvexComponent.Backend
open Convex.FSharp
open ConvexComponent
module Declarations =
    let schema = Schema.define [TableSchema.defineForTable Tables.counters]
    let component = NativeComponentDefinitions.child "ConvexComponent" [] [] [] NativeComponentDefinitions.emptyExports
