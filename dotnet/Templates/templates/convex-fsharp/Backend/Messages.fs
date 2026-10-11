namespace ConvexApp.Backend

open Convex.FSharp
open ConvexApp

module Messages =
    let list = Convex.query (fun ctx () -> ctx.Db.Query(Tables.messages).Take(50))
    let count = Convex.query (fun ctx () -> async {
        let! messages = ctx.Db.Query(Tables.messages).Take(50)
        return messages.Length
    })
    let send = Convex.mutation (fun ctx (args: SendMessage) -> async {
        if System.String.IsNullOrWhiteSpace args.Text || args.Text.Length > 2000 then
            invalidArg "Text" "A message needs between 1 and 2,000 characters."
        let! id = ctx.Db.Insert(Tables.messages, { Message.Text = args.Text })
        return Id.value id
    })
