namespace Convex.FSharp

open System
open System.Text
open System.Collections.Generic
open System.Reflection
open Microsoft.FSharp.Reflection
open Convex.Runtime

/// Build-time metadata comes from exact compiled registrations. The emitted
/// client source contains contracts and references, never registration handlers.
type NativeClientFunction = {
    Address: string
    Kind: FunctionKind
    Visibility: FunctionVisibility
    Contract: ICallableContract
}

type NativeClientComponent = {
    DefinitionPath: string
    Declaration: NativeComponentDefinition
    Functions: NativeClientFunction array
}

/// A non-callable component argument export remains a resource descriptor. It
/// cannot be passed to RunQuery, RunMutation, RunAction, or the scheduler.
type ComponentValueExport = {
    Component: string
    Attributes: string list
    Argument: string list
}

module NativeClientCatalog =
    type private Entry =
        | Callable of NativeClientFunction * (string*string list) option
        | ValueExport of ComponentValueExport
    type private Node = Branch of Map<string,Node> | Leaf of Entry

    let private add segments value root =
        let rec insert = function
            | [],_ -> invalidOp "Native client namespace collision"
            | [name],Branch values when not(values.ContainsKey name) -> Branch(values.Add(name,Leaf value))
            | name::tail,Branch values when not tail.IsEmpty ->
                let child=values.TryFind name |> Option.defaultValue(Branch Map.empty)
                Branch(values.Add(name,insert(tail,child)))
            | _ -> invalidOp "Native client namespace collision"
        insert(segments,root)

    let functionOfRegistration address (definition:IRegisteredFunction) =
        match definition with
        | :? ICallableContract as contract when definition.Kind<>FunctionKind.HttpAction ->
            {Address=address;Kind=definition.Kind;Visibility=definition.Visibility;Contract=contract}
        | _ -> invalidArg "definition" "A native callable must preserve its F# contracts"

    let private exports (root:NativeComponentDefinition) (components:NativeClientComponent array) =
        if components.Length>32 then invalidArg "components" "Native client component graph exceeds its bound"
        let definitions=components |> Array.map(fun value -> value.DefinitionPath,value) |> Map.ofArray
        if definitions.Count<>components.Length then invalidArg "components" "Duplicate native component definitions"
        let child (owner:NativeComponentDefinition) name =
            let mount=owner.Children |> List.tryFind(fun value -> value.Name=name)
                      |> Option.defaultWith(fun () -> invalidOp "An export names an undeclared component child")
            definitions.TryFind mount.DefinitionPath
            |> Option.defaultWith(fun () -> invalidOp "An export component is absent from the packaged graph")
        let rec select depth attributes node =
            if depth>64 then invalidOp "Native component export depth exceeded"
            match attributes,node with
            | [],_ -> node
            | name::tail,NativeComponentExport.Branch children ->
                let node=children |> List.tryFind(fun (key,_) -> key=name) |> Option.map snd
                         |> Option.defaultWith(fun () -> invalidOp "Missing component export attribute")
                select (depth+1) tail node
            | _ -> invalidOp "A component export path crosses a value leaf"
        // Empty export trees use Convex's conventional file-based public API.
        // Explicit native exports retain their exact aliases and reexport tree.
        let exportsOf (owner:NativeClientComponent) =
            match owner.Declaration.Exports with
            | NativeComponentExport.Branch [] ->
                let rec insert path reference = function
                    | NativeComponentExport.Leaf _ -> invalidOp "Component export namespace collision"
                    | NativeComponentExport.Branch entries ->
                        match path with
                        | [] -> invalidOp "An exported function needs a path"
                        | [name] when not(List.exists(fun(key,_) -> key=name) entries) ->
                            NativeComponentExport.Branch(entries@[name,NativeComponentExport.Leaf reference])
                        | name::tail when not tail.IsEmpty ->
                            let child=entries |> List.tryFind(fun(key,_) -> key=name) |> Option.map snd |> Option.defaultValue(NativeComponentExport.Branch [])
                            let next=insert tail reference child
                            NativeComponentExport.Branch((entries |> List.filter(fun(key,_) -> key<>name))@[name,next])
                        | _ -> invalidOp "Component export namespace collision"
                owner.Functions
                |> Array.filter(fun fn -> fn.Visibility=FunctionVisibility.Public)
                |> Array.fold(fun tree fn ->
                    let address=fn.Address.Split(':')
                    if address.Length<>2 then invalidOp "A component function needs a module:function address"
                    insert ((address[0].Split('/') |> Array.toList)@[address[1]]) (NativeComponentReference.Function fn.Address) tree) (NativeComponentExport.Branch [])
            | explicit -> explicit
        let rec resolve depth (owner:NativeClientComponent) reference =
            if depth>64 then invalidOp "Native component export reference depth exceeded"
            match reference with
            | NativeComponentReference.Function address ->
                let matches=owner.Functions |> Array.filter(fun value -> value.Address=address)
                if matches.Length<>1 then invalidOp "A component export must identify one compiled function"
                if matches[0].Visibility<>FunctionVisibility.Public then invalidOp "A component cannot export an internal function"
                Choice1Of2 matches[0]
            | NativeComponentReference.Argument attributes -> Choice2Of2 attributes
            | NativeComponentReference.Child(name,attributes) ->
                let nested=child owner.Declaration name
                match select (depth+1) attributes (exportsOf nested) with
                | NativeComponentExport.Leaf value -> resolve (depth+1) nested value
                | NativeComponentExport.Branch _ -> invalidOp "A component leaf reference must resolve to a leaf export"
        let entries=ResizeArray<string list*Entry>()
        let rec visit depth componentName (owner:NativeClientComponent) attributes node =
            if depth>64 || entries.Count>=10_000 then invalidOp "Native component export catalogue exceeds its bound"
            match node with
            | NativeComponentExport.Branch children ->
                for name,value in children do visit (depth+1) componentName owner (attributes@[name]) value
            | NativeComponentExport.Leaf(NativeComponentReference.Child(name,path)) ->
                let nested=child owner.Declaration name
                let selected=select (depth+1) path (exportsOf nested)
                visit (depth+1) componentName nested attributes selected
            | NativeComponentExport.Leaf reference ->
                match resolve (depth+1) owner reference with
                | Choice1Of2 fn -> entries.Add(componentName::attributes,Callable(fn,Some(componentName,attributes)))
                | Choice2Of2 argument -> entries.Add(componentName::attributes,ValueExport {Component=componentName;Attributes=attributes;Argument=argument})
        for mount in root.Children do
            let definition=child root mount.Name
            visit 0 mount.Name definition [] (exportsOf definition)
        entries |> Seq.toArray

    /// Public/internal kinds come from registrations. Component aliases follow
    /// each direct child's actual export tree, including reexports from nested
    /// components. Value resources stay explicitly non-callable.
    let render (namespaceName:string) contractAssemblies contractOwners root components (functions:NativeClientFunction array) =
        let contract value=Declarations.contractSource contractAssemblies contractOwners value
        let functionExpression (value:NativeClientFunction) mounted =
            // A component's public exports become server-only references in
            // its parent, matching the original Convex component boundary.
            let visibility = if Option.isSome mounted then FunctionVisibility.Internal else value.Visibility
            let constructor =
                match value.Kind,visibility with
                | FunctionKind.Query,FunctionVisibility.Public -> "publicQuery"
                | FunctionKind.Mutation,FunctionVisibility.Public -> "publicMutation"
                | FunctionKind.Action,FunctionVisibility.Public -> "publicAction"
                | FunctionKind.Query,FunctionVisibility.Internal -> "internalQuery"
                | FunctionKind.Mutation,FunctionVisibility.Internal -> "internalMutation"
                | FunctionKind.Action,FunctionVisibility.Internal -> "internalAction"
                | _ -> invalidOp "A native client reference requires a query, mutation, or action"
            let expression="FunctionReference."+constructor+" "+Declarations.quote value.Address+
                           " ("+contract value.Contract.Arguments+") ("+contract value.Contract.Returns+")"
            match mounted with
            | None -> expression
            | Some(componentName,attributes) ->
                "FunctionReference.atComponentExport "+Declarations.quote componentName+" ["+
                String.concat "; " (List.map Declarations.quote attributes)+"] ("+expression+")"
        let output=StringBuilder()
        output.AppendLine("// Generated from exact native contracts and packaged addresses. Do not edit.") |> ignore
        output.Append("namespace ").AppendLine(namespaceName) |> ignore
        output.AppendLine().AppendLine("open Convex.FSharp") |> ignore
        // A cold handler-free consumer has not evaluated any backend table
        // declaration. Preserve the exact marker bindings observed while
        // projecting the admitted canonical contracts, before their owner
        // getters or generated codecs can derive nested Id values.
        let seen=HashSet<Type>()
        let tableTypes=Dictionary<Type,string>()
        let rec collect depth (value:Type) =
            if depth>64 || seen.Count>10000 then invalidOp "Native client table identity graph exceeds its bound"
            if seen.Add value then
                if value.IsGenericType && value.GetGenericTypeDefinition()=typedefof<Id<_>> then
                    let marker=value.GetGenericArguments()[0]
                    let name=TableRegistry.getName marker
                    tableTypes[marker]<-name
                elif value.IsArray then collect (depth+1) (value.GetElementType())
                else
                    if value.IsGenericType then for argument in value.GetGenericArguments() do collect (depth+1) argument
                    let flags=BindingFlags.Public ||| BindingFlags.NonPublic
                    if FSharpType.IsRecord(value,flags) then
                        for field in FSharpType.GetRecordFields(value,flags) do collect (depth+1) field.PropertyType
                    elif FSharpType.IsUnion(value,flags) then
                        for case in FSharpType.GetUnionCases(value,flags) do
                            for field in case.GetFields() do collect (depth+1) field.PropertyType
                    elif FSharpType.IsTuple value then
                        for element in FSharpType.GetTupleElements value do collect (depth+1) element
        for fn in Array.append functions (components |> Array.collect _.Functions) do
            collect 0 fn.Contract.Arguments.ValueType
            collect 0 fn.Contract.Returns.ValueType
        output.AppendLine().AppendLine("module private TableIdentities =").AppendLine("    let ensure () =") |> ignore
        if tableTypes.Count=0 then output.AppendLine("        ()") |> ignore
        for KeyValue(marker,name) in tableTypes |> Seq.sortBy(fun pair -> pair.Key.AssemblyQualifiedName) do
            output.Append("        Table.register<").Append(Declarations.typeSource contractAssemblies marker)
                .Append("> ").AppendLine(Declarations.quote name) |> ignore
        let rec write depth node =
            let padding=String.replicate depth "    "
            match node with
            | Leaf _ -> invalidOp "A catalogue root must be a namespace"
            | Branch values when values.IsEmpty -> output.Append(padding).AppendLine("do ()") |> ignore
            | Branch values ->
                for KeyValue(name,value) in values do
                    match value with
                    | Branch _ ->
                        output.Append(padding).Append("module ").Append(Declarations.identifier name).AppendLine(" =") |> ignore
                        write (depth+1) value
                    | Leaf(Callable(fn,mounted)) ->
                        output.Append(padding).Append("let ").Append(Declarations.identifier name).Append(" = ").AppendLine(functionExpression fn mounted) |> ignore
                    | Leaf(ValueExport value) ->
                        output.Append(padding).Append("let ").Append(Declarations.identifier name).Append(" : ComponentValueExport = { Component = ")
                            .Append(Declarations.quote value.Component).Append("; Attributes = [")
                            .Append(String.concat "; " (List.map Declarations.quote value.Attributes)).Append("]; Argument = [")
                            .Append(String.concat "; " (List.map Declarations.quote value.Argument)).AppendLine("] }") |> ignore
        let group (name:string) entries =
            output.AppendLine().Append("module ").Append(name).AppendLine(" =") |> ignore
            output.AppendLine("    do TableIdentities.ensure()") |> ignore
            let tree=entries |> Array.fold(fun tree (path,value) -> add path value tree) (Branch Map.empty)
            write 1 tree
        let rootEntries visibility =
            functions |> Array.filter(fun value -> value.Visibility=visibility)
            |> Array.map(fun value ->
                let address=value.Address.Split(':')
                if address.Length<>2 then invalidOp "A callable address must identify its module and function"
                (address[0].Split('/') |> Array.toList)@[address[1]],Callable(value,None))
        group "Api" (rootEntries FunctionVisibility.Public)
        group "Internal" (rootEntries FunctionVisibility.Internal)
        group "Components" (exports root components)
        output.ToString()
