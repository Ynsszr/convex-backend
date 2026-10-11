namespace Convex.FSharp

/// Explicit component paths preserve the backend's export and scope boundary.
type ComponentReference internal (name: string) =
    member _.Name = name
    member _.Query(functionPath, arguments: BoundaryContract<'Args>, returns: BoundaryContract<'Result>) =
        FunctionReference.publicQuery functionPath arguments returns |> FunctionReference.inComponent name
    member _.Mutation(functionPath, arguments: BoundaryContract<'Args>, returns: BoundaryContract<'Result>) =
        FunctionReference.publicMutation functionPath arguments returns |> FunctionReference.inComponent name
    member _.Action(functionPath, arguments: BoundaryContract<'Args>, returns: BoundaryContract<'Result>) =
        FunctionReference.publicAction functionPath arguments returns |> FunctionReference.inComponent name
    member _.SubComponent(childName) = ComponentReference(name + "/" + childName)

module Component =
    let create name = ComponentReference name
