namespace Convex.FSharp

open System

/// References name resources that the owning component evaluator resolves.
/// Definition paths and namespaces are assigned by that evaluator, not by CLR.
[<RequireQualifiedAccess>]
type NativeComponentReference =
    | Function of functionPath:string
    | Child of name:string * attributes:string list
    | Argument of attributes:string list

[<RequireQualifiedAccess>]
type NativeComponentExport =
    | Leaf of NativeComponentReference
    | Branch of (string * NativeComponentExport) list

type NativeComponentMount =
    { Name:string
      DefinitionPath:string
      Arguments:(string * ConvexValue) list
      Environment:(string * EnvironmentBinding) list
      HttpPrefix:string option }

[<RequireQualifiedAccess>]
type NativeComponentKind =
    | App
    | Child of name:string * arguments:(string * Validator) list

/// This is the pinned component definition ABI, before the owner injects path.
/// It carries declarations only; importing, cycles, instantiation, environment
/// resolution, namespace creation and deployment remain backend operations.
type NativeComponentDefinition internal
    (kind:NativeComponentKind,environment:EnvironmentDeclaration list,
     children:NativeComponentMount list,exports:NativeComponentExport,httpPrefix:string option,
     initializers:(string * (Map<string,ConvexValue> -> Map<string,ConvexValue>)) list) =
    let quote=ValidatorJson.quote
    let array encode values="["+(values |> List.map encode |> String.concat ",")+"]"
    let tuple key value="["+quote key+","+value+"]"
    let exact owner (value:string) =
        if String.IsNullOrWhiteSpace value || value.Trim()<>value || value.Length>2048 ||
           (value |> Seq.exists Char.IsControl) then
            invalidArg owner "An exact bounded component declaration name is required"
    let segment owner value =
        exact owner value
        if value.Contains('/') || value.Contains('\\') then invalidArg owner "Component names and reference attributes are single path segments"
    let prefix (value:string) =
        exact "httpPrefix" value
        if not(value.StartsWith("/",StringComparison.Ordinal)) || value.Contains('*') || value.Contains('?') || value.Contains('#') then
            invalidArg "httpPrefix" "HTTP mount prefixes must be paths without wildcards, queries or fragments"
        if value.EndsWith("/",StringComparison.Ordinal) then value else value+"/"
    let environment=ComponentEnvironment.validate environment
    let reference = function
        | NativeComponentReference.Function path ->
            exact "functionPath" path
            if path.StartsWith("/",StringComparison.Ordinal) || not(path.Contains(':')) then
                invalidArg "functionPath" "A component function reference requires its module and export"
            "_reference/function/"+path
        | NativeComponentReference.Child(name,attributes) ->
            segment "child" name
            attributes |> List.iter(segment "attribute")
            "_reference/childComponent/"+String.concat "/" (name::attributes)
        | NativeComponentReference.Argument attributes ->
            if attributes.IsEmpty then invalidArg "attributes" "A component argument reference requires an attribute"
            attributes |> List.iter(segment "attribute")
            "_reference/componentArgument/"+String.concat "/" attributes
    let exportTree value =
        let mutable nodeCount=0
        let rec encode depth value =
            nodeCount<-nodeCount+1
            if depth>64 || nodeCount>10_000 then invalidArg "exports" "Component export tree exceeds its declaration bound"
            match value with
            | NativeComponentExport.Leaf value -> "{\"type\":\"leaf\",\"leaf\":"+quote(reference value)+"}"
            | NativeComponentExport.Branch entries ->
                SchemaNames.unique "component exports" (entries |> List.map fst)
                entries |> List.iter(fun(name,_) -> segment "export" name)
                "{\"type\":\"branch\",\"branch\":"+array (fun(name,value) -> tuple name (encode (depth+1) value)) entries+"}"
        encode 0 value
    let environmentBinding = function
        | EnvironmentBinding.Literal value ->
            if isNull value then invalidArg "environment" "A component environment literal must be a string"
            "{\"type\":\"value\",\"value\":"+quote value+"}"
        | EnvironmentBinding.FromParent name ->
            if not(environment |> List.exists(fun declaration -> declaration.Name=name)) then
                invalidArg "environment" "Declare the parent environment variable before referencing it"
            "{\"type\":\"envVar\",\"name\":"+quote name+"}"
    let validateMount (mount:NativeComponentMount) =
        segment "child" mount.Name
        exact "definitionPath" mount.DefinitionPath
        if mount.DefinitionPath.StartsWith("/",StringComparison.Ordinal) || mount.DefinitionPath.Contains('\\') then
            invalidArg "definitionPath" "Component definition paths must be relative owner dependency paths"
        SchemaNames.unique "component arguments" (mount.Arguments |> List.map fst)
        SchemaNames.unique "component environment" (mount.Environment |> List.map fst)
        mount.Arguments |> List.iter(fun(name,_) -> segment "argument" name)
        mount.Environment |> List.iter(fun(name,_) -> segment "environment" name)
        mount
    let children=children |> List.map validateMount
    do
        if children.Length>1000 || environment.Length>1000 then invalidArg "definition" "Component declaration exceeds its bound"
        SchemaNames.unique "child components" (children |> List.map(fun child -> child.Name))
        SchemaNames.unique "child initializers" (initializers |> List.map fst)
        if initializers.Length>1000 then invalidArg "initializers" "Child initializers exceed their declaration bound"
        for name,callback in initializers do
            if isNull(box callback) then invalidArg "initializers" "A child initializer is required"
            match children |> List.tryFind(fun child -> child.Name=name) with
            | Some child when child.Arguments.IsEmpty -> ()
            | _ -> invalidArg "initializers" "An initializer requires an exact mounted child without fixed arguments"
        if not initializers.IsEmpty then
            match kind with
            | NativeComponentKind.Child _ -> ()
            | NativeComponentKind.App -> invalidArg "initializers" "The pinned initializer lifecycle belongs to child component definitions"
        SchemaNames.unique "HTTP mounts" (children |> List.choose(fun child -> child.HttpPrefix |> Option.map prefix))
        match exports with
        | NativeComponentExport.Branch _ -> ()
        | NativeComponentExport.Leaf _ -> invalidArg "exports" "A component export tree must have a branch at its root"
        match kind,httpPrefix with
        | NativeComponentKind.Child _,Some _ -> invalidArg "httpPrefix" "Only the root application declares its own HTTP prefix"
        | _ -> ()
    member _.Kind=kind
    member _.Children=children
    member _.Exports=exports
    /// Pure argument computation runs only when the owning deployment typechecker
    /// requests this exact child. It receives no runtime context or effect APIs.
    member _.WithInitializer(name,callback) =
        NativeComponentDefinition(kind,environment,children,exports,httpPrefix,initializers@[name,callback])
    member _.Initialize(name,args:Map<string,ConvexValue>) =
        let callback=initializers |> List.tryFind(fun (child,_) -> child=name) |> Option.map snd
                     |> Option.defaultWith(fun () -> invalidOp "Callback is not a function")
        if isNull(box args) || args.Count>1000 then invalidArg "args" "A bounded component argument object is required"
        let result=callback args
        if isNull(box result) || result.Count>1000 then invalidOp "A child initializer must return a bounded argument object"
        result
    member _.Export() =
        let declaration = function
            | NativeComponentKind.App -> "{\"type\":\"app\"}"
            | NativeComponentKind.Child(name,arguments) ->
                segment "component" name
                if arguments.Length>1000 then invalidArg "arguments" "Component arguments exceed their declaration bound"
                SchemaNames.unique "component argument declarations" (arguments |> List.map fst)
                arguments |> List.iter(fun(name,_) -> segment "argument" name)
                "{\"type\":\"childComponent\",\"name\":"+quote name+",\"args\":"+
                array (fun(name,(validator:Validator)) -> tuple name ("{\"type\":\"value\",\"value\":"+quote(validator.ToJson())+"}")) arguments+"}"
        let child (mount:NativeComponentMount) =
            "{\"name\":"+quote mount.Name+",\"path\":"+quote mount.DefinitionPath+
            ",\"args\":"+(if initializers |> List.exists(fun (name,_) -> name=mount.Name) then "null" else array (fun(name,value) -> tuple name ("{\"type\":\"value\",\"value\":"+quote(ConvexJson.toJson value)+"}")) mount.Arguments)+
            ",\"env\":"+array (fun(name,value) -> tuple name (environmentBinding value)) mount.Environment+"}"
        let mounts=children |> List.choose(fun child -> child.HttpPrefix |> Option.map(fun path ->
            quote(prefix path)+":"+quote(reference(NativeComponentReference.Child(child.Name,[])))))
        let envVar (declaration:EnvironmentDeclaration) =
            tuple declaration.Name ("{\"type\":\"value\",\"value\":"+quote(declaration.Validator.ToJson())+
                (if declaration.Optional then ",\"optional\":true" else "")+"}")
        "{\"definitionType\":"+declaration kind+",\"childComponents\":"+array child children+
        ",\"httpMounts\":{"+String.concat "," mounts+"},\"exports\":"+exportTree exports+
        (if environment.IsEmpty then "" else ",\"envVars\":"+array envVar environment)+
        (httpPrefix |> Option.map(fun path -> ",\"httpPrefix\":"+quote(prefix path)) |> Option.defaultValue "")+"}"

    interface Convex.Runtime.IDeclaration with
        member _.Kind = Convex.Runtime.DeclarationKind.Component
        member this.Export() = Convex.Runtime.RuntimeJson.parse(this.Export())
    interface Convex.Runtime.IComponentDefinition with
        member _.IsApplication =
            match kind with NativeComponentKind.App -> true | NativeComponentKind.Child _ -> false
        member _.Initializers =
            initializers
            |> List.map (fun (_, callback) -> System.Func<Map<string, ConvexValue>, Map<string, ConvexValue>>(callback) :> System.Delegate)
            |> List.toArray :> System.Collections.Generic.IReadOnlyList<System.Delegate>
        member this.Initialize(child, arguments) =
            let decoded = ConvexWire.decode<Map<string, ConvexValue>>(arguments.GetRawText())
            this.Initialize(child, decoded) |> ConvexWire.encode |> Convex.Runtime.RuntimeJson.parse

module NativeComponentDefinitions =
    let emptyExports=NativeComponentExport.Branch []
    let mount name definitionPath =
        {Name=name;DefinitionPath=definitionPath;Arguments=[];Environment=[];HttpPrefix=None}
    let app environment children exports httpPrefix =
        NativeComponentDefinition(NativeComponentKind.App,environment,children,exports,httpPrefix,[])
    let child name arguments environment children exports =
        NativeComponentDefinition(NativeComponentKind.Child(name,arguments),environment,children,exports,None,[])

    let withInitializer child callback (definition:NativeComponentDefinition) =
        definition.WithInitializer(child,callback)
