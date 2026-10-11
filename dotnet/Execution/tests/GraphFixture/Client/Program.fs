module GraphClient
open System
open System.IO
open System.Text.Json
open Convex.Client
open GraphContracts
open GraphGenerated
let complete = function
    | ConvexHttp.Completed value -> value
    | other -> failwithf "Unexpected invocation result: %A" other
[<EntryPoint>]
let main args =
    try
        let endpoint=ConvexHttp.endpoint args[0]
        let query fn arguments=ConvexHttp.query endpoint ConvexHttp.Anonymous fn arguments |> complete
        let mutate fn arguments=ConvexHttp.mutate endpoint ConvexHttp.Anonymous fn arguments |> complete
        if query Api.root.readProbe ()<>0 || query Api.root.readLeaf ()<>0 then failwith "Fresh component state required"
        if mutate Api.root.writeProbe {amount=3}<>3 || mutate Api.root.writeLeaf {amount=5}<>5 then failwith "Nested component mutations failed"
        if query Api.root.readProbe ()<>3 || query Api.root.readLeaf ()<>5 then failwith "Component stores are not independent"
        match ConvexHttp.mutate endpoint ConvexHttp.Anonymous Api.root.rollback {amount=10} with
        | ConvexHttp.Refused _ -> ()
        | _ -> failwith "Cross-component rollback did not refuse"
        if query Api.root.readProbe ()<>3 || query Api.root.readLeaf ()<>5 then failwith "Cross-component transaction was not rolled back"
        if query Api.extra.square {amount=7}<>49 then failwith "Referenced handler assembly was not invoked"
        if query Api.extra.code ()<>Code "native" then failwith "Authored result codec failed"
        if query Api.extra.codes ()<>[|Code "one";Code "two"|] then failwith "Composed authored result codecs failed"
        if Components.probe.read.Address<>"_reference/childComponent/probe/read" || Components.probe.leaf.read.Address<>"_reference/childComponent/probe/leaf/read" then
            failwith "Generated component references were flattened"
        if mutate Api.extra.seed ()<>12 then failwith "Native pagination seed failed"
        let firstPage=query Api.extra.page {Convex.FSharp.PaginationOptions.NumItems=5;Cursor=None}
        let secondPage=query Api.extra.page {Convex.FSharp.PaginationOptions.NumItems=5;Cursor=Some firstPage.ContinueCursor}
        let thirdPage=query Api.extra.page {Convex.FSharp.PaginationOptions.NumItems=5;Cursor=Some secondPage.ContinueCursor}
        let rows=firstPage.Page@secondPage.Page@thirdPage.Page
        if firstPage.Page.Length<>5 || secondPage.Page.Length<>5 || thirdPage.Page.Length<>2 || not thirdPage.IsDone || (rows |> List.map _.value |> Set.ofList).Count<>12 then
            failwith "Native pagination has missing or duplicated rows"
        GraphPaginationProbe.run args[0] |> fun work -> work.GetAwaiter().GetResult()
        GraphFaultRelay.run args[0] |> fun work -> work.GetAwaiter().GetResult() |> ignore
        File.WriteAllText(args[1],JsonSerializer.Serialize {|nestedComponents=true;isolatedComponentStores=true;crossComponentRollback=true;multipleHandlerAssemblies=true;authoredCodec=true;composedCodec=true;generatedComponentReferences=true;lostReplyReplayDeduplicated=true;liveOptimisticUpdate=true;nativePagination=true;reactivePagination=true;paginationReconnect=true|})
        printfn "PASS native component graph, multi-assembly functions, shared authored codecs and rollback"
        0
    with error -> eprintfn "%s" error.Message;1
