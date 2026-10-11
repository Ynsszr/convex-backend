namespace Convex.FSharp

/// Ordered workflows inside the invocation's existing lifetime. Unlike the
/// compiler's Async.Sequential expansion, this does not start child workflows
/// through Async.Parallel. Failure or cancellation stops before the next item.
[<RequireQualifiedAccess>]
module ConvexAsync =
    let sequence (workflows:seq<Async<'T>>) : Async<'T array> = async {
        let values = ResizeArray<'T>()
        for workflow in workflows do
            let! value = workflow
            values.Add value
        return values.ToArray()
    }
