# Minimal native pattern

The release template is the executable starting point. These declarations use
its 0.3.2 API. Real private data also requires the application's auth policy.

```fsharp
namespace MyApp
open Convex.FSharp

type Message = { Text: string }
type SendMessage = { Text: string }

module Tables =
    let messages = Table.define<Message> "messages"
```

In the backend assembly:

```fsharp
namespace MyApp.Backend
open Convex.FSharp
open MyApp

module Messages =
    let list = Convex.query (fun ctx () -> ctx.Db.Query(Tables.messages).Take(50))
    let send = Convex.mutation (fun ctx (args: SendMessage) -> async {
        if System.String.IsNullOrWhiteSpace args.Text || args.Text.Length > 2000 then
            invalidArg "Text" "A message needs between 1 and 2,000 characters."
        let! id = ctx.Db.Insert(Tables.messages, { Message.Text = args.Text })
        return Id.value id
    })

module Declarations =
    let schema = Schema.define [TableSchema.defineForTable Tables.messages]
    let app = NativeComponentDefinitions.app [] [] NativeComponentDefinitions.emptyExports None
```

The manifest's `modules` entry binds `messages` to `MyApp.Backend.Messages`.
`schema` and `component` select `MyApp.Backend.Declarations:schema` and `:app`.
The Contracts assembly contains only shared types and codec owners; Backend
references it. Generated clients reference Contracts and Convex.Client, never
the handler assembly. For custom wire shapes export `Contract.arguments<T>()`
and `Contract.derived<T>()` or authored codecs from Contracts, then use
`Convex.queryWithContracts arguments returns handler` (or mutation/action).
