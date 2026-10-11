namespace ConvexApp

open Convex.FSharp

type Message = { Text: string }
type SendMessage = { Text: string }

module Tables =
    let messages = Table.define<Message> "messages"
