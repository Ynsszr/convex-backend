namespace ConvexApp.Backend

open Convex.FSharp
open ConvexApp

module Declarations =
    let schema = Schema.define [TableSchema.defineForTable Tables.messages]
    let app = NativeComponentDefinitions.app [] [] NativeComponentDefinitions.emptyExports None
