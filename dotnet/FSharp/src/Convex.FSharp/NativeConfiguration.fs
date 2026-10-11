namespace Convex.FSharp

open System

/// A component import binds a native declaration to its owning graph path.
/// The backend resolves and validates that path; CLR assembly names are not
/// component namespaces and are never inferred as deployment authority.
type NativeComponentImport = {
    DefinitionPath:string
    Definition:NativeComponentDefinition
}

module ComponentImport =
    let create definitionPath (definition:NativeComponentDefinition) =
        if String.IsNullOrWhiteSpace definitionPath || definitionPath.StartsWith("/",StringComparison.Ordinal) ||
           definitionPath.Contains('\\') ||
           (definitionPath.Split('/') |> Array.exists(fun part -> part="" || part="." || part="..")) then
            invalidArg "definitionPath" "A normalized relative component definition path is required"
        match definition.Kind with
        | NativeComponentKind.App -> invalidArg "definition" "An application root cannot be imported as a child component"
        | NativeComponentKind.Child _ -> {DefinitionPath=definitionPath;Definition=definition}

/// Native configuration construction performs no deployment or handler call.
/// ToNative supplies the exact declaration consumed by the component evaluator.
type ApplicationConfiguration internal (componentName:string option,environment:EnvironmentDeclaration list,httpPrefix:string option) =
    let environment=ComponentEnvironment.validate environment
    let mounts=ResizeArray<NativeComponentMount>()
    let mutable exports=NativeComponentDefinitions.emptyExports
    do
        componentName |> Option.iter(fun name -> SchemaNames.unique "component" [name])
        httpPrefix |> Option.iter(fun prefix ->
            if not(prefix.StartsWith("/",StringComparison.Ordinal)) then
                invalidArg "httpPrefix" "HTTP mount prefixes start with '/'")
    member _.Environment=environment
    member _.MountCount=mounts.Count
    member _.Env(name:string) =
        if not(environment |> List.exists(fun declaration -> declaration.Name=name)) then
            invalidArg "name" "Declare the parent environment variable before referencing it"
        EnvironmentBinding.FromParent name
    member private this.AddMount(mounted:NativeComponentMount, environment, httpPrefix) =
        let bindings=defaultArg environment mounted.Environment
        SchemaNames.unique "component mount environment" (bindings |> List.map fst)
        if mounts |> Seq.exists(fun previous -> previous.Name=mounted.Name) then
            invalidArg "name" "Component mount names must be unique"
        for _,binding in bindings do
            match binding with
            | EnvironmentBinding.FromParent source -> this.Env source |> ignore
            | EnvironmentBinding.Literal value when isNull value ->
                invalidArg "environment" "Environment values must be strings"
            | EnvironmentBinding.Literal _ -> ()
        mounts.Add {mounted with Environment=bindings;HttpPrefix=Option.orElse mounted.HttpPrefix httpPrefix}
        this
    member this.Use(source:NativeComponentImport, ?name:string, ?environment:(string*EnvironmentBinding) list, ?httpPrefix:string) =
        let source=ComponentImport.create source.DefinitionPath source.Definition
        let defaultName =
            match source.Definition.Kind with
            | NativeComponentKind.Child(name,_) -> name
            | NativeComponentKind.App -> invalidArg "source" "A mounted component must be a child definition"
        let mounted=NativeComponentDefinitions.mount (defaultArg name defaultName) source.DefinitionPath
        this.AddMount(mounted,environment,httpPrefix)
    member this.Use(source:NativeComponentMount, ?name:string, ?environment:(string*EnvironmentBinding) list, ?httpPrefix:string) =
        this.AddMount({source with Name=defaultArg name source.Name},environment,httpPrefix)
    member this.WithExports(value:NativeComponentExport) =
        exports<-value
        this
    member _.ToNative() =
        let children=mounts |> Seq.toList
        match componentName with
        | None -> NativeComponentDefinitions.app environment children exports httpPrefix
        | Some name ->
            if httpPrefix.IsSome then invalidArg "httpPrefix" "Only the root application declares its own HTTP prefix"
            NativeComponentDefinitions.child name [] environment children exports
    member this.Export() = this.ToNative().Export()

module Configuration =
    let app() = ApplicationConfiguration(None,[],None)
    let appWithEnvironment declarations = ApplicationConfiguration(None,declarations,None)
    let appWithOptions declarations httpPrefix = ApplicationConfiguration(None,declarations,httpPrefix)
    let defineComponent name = ApplicationConfiguration(Some name,[],None)
    let componentWithEnvironment name declarations = ApplicationConfiguration(Some name,declarations,None)
