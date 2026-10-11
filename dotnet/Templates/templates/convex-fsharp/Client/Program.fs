module ConvexApp.Client

open Convex.Client
open ConvexApp.Generated

[<EntryPoint>]
let main arguments =
    match arguments with
    | [| origin |] ->
        let endpoint = ConvexHttp.endpoint origin
        match ConvexHttp.query endpoint ConvexHttp.Anonymous Api.messages.list () with
        | ConvexHttp.Completed messages -> printfn "%A" messages; 0
        | ConvexHttp.Refused reason -> eprintfn "Query refused: %A" reason; 1
        | ConvexHttp.Uncertain reason -> eprintfn "Unknown outcome: %A" reason; 2
    | _ -> eprintfn "Pass the URL of your native Convex backend."; 1
