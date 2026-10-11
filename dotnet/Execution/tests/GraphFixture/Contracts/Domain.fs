namespace GraphContracts
open Convex.FSharp
type Counter = { value: int }
type Amount = { amount: int }
type Row = { name:string; value:int }
type Code = Code of string
module Codecs =
    let code = Contract.create (V.string()) (fun (Code value) -> box ("code/"+value)) (fun raw -> Code((unbox<string> raw).Substring(5)))
module Tables =
    let counters = Table.define<Counter> "counters"
    let rows = Table.define<Row> "rows"
