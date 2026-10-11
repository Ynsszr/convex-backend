namespace Convex.DotNet.Development

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

/// A bounded application-owned graph of native component manifests. Graph paths
/// are deployment identities; filesystem paths are local build inputs only.
module ApplicationGraph =
    type Node = {
        Manifest: string
        Root: string
        Configuration: JsonElement
        DefinitionPath: string option
        Dependencies: string array
    }
    type Graph = { Root: Node; Components: Node array }

    let text name (value: JsonElement) = value.GetProperty(name:string).GetString()
    let optional name (value: JsonElement) =
        match value.TryGetProperty(name:string) with
        | true, field when field.ValueKind<>JsonValueKind.Null -> Some field
        | _ -> None
    let validModulePath value =
        not(String.IsNullOrWhiteSpace value) && Regex.IsMatch(value,@"^[A-Za-z_][A-Za-z0-9_]*(/[A-Za-z_][A-Za-z0-9_]*)*$")
    let private fields allowed (value:JsonElement) =
        if value.ValueKind<>JsonValueKind.Object then invalidOp "Expected a native project configuration object."
        for property in value.EnumerateObject() do
            if not(Set.contains property.Name allowed) then invalidOp ("Unknown project field: "+property.Name)
    let private unique (value:JsonElement) =
        let rec visit depth (element:JsonElement) =
            if depth>32 then invalidOp "Native project configuration nesting exceeds its bound."
            match element.ValueKind with
            | JsonValueKind.Object ->
                let seen=Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                for property in element.EnumerateObject() do
                    if not(seen.Add property.Name) then invalidOp ("Duplicate project field: "+property.Name)
                    visit (depth+1) property.Value
            | JsonValueKind.Array -> for child in element.EnumerateArray() do visit (depth+1) child
            | _ -> ()
        visit 0 value
    let load path =
        let path=Path.GetFullPath path
        let info=FileInfo path
        if not info.Exists || info.Length>65536L || info.LinkTarget<>null then
            invalidArg "path" "Select a bounded regular convex.project.json."
        use document=JsonDocument.Parse(File.ReadAllBytes path,JsonDocumentOptions(MaxDepth=32))
        let value=document.RootElement.Clone()
        unique value
        if value.GetProperty("version").GetInt32()<>1 then invalidOp "Unsupported native project version."
        fields (Set.ofList ["version";"project";"configuration";"output";"schema";"component";"auth";"crons";"http";"contracts";"clientNamespace";"modules";"components"]) value
        for moduleConfig in value.GetProperty("modules").EnumerateArray() do
            for property in moduleConfig.EnumerateObject() do
                if not(List.contains property.Name ["path";"type";"aliases";"assembly"]) then invalidOp ("Unknown module field: "+property.Name)
            if not(validModulePath(text "path" moduleConfig)) then invalidOp "Invalid durable module path."
        for name in ["schema";"component";"auth";"crons";"http"] do
            match optional name value with
            | Some selector when selector.ValueKind=JsonValueKind.Object -> fields (Set.ofList ["assembly";"export"]) selector
            | Some selector when selector.ValueKind<>JsonValueKind.String -> invalidOp "A declaration selector must be a string or an assembly/export object."
            | _ -> ()
        Path.GetDirectoryName path,value

    let read path =
        let definitions=Collections.Generic.Dictionary<string,Node>(StringComparer.Ordinal)
        let definitionSources=Collections.Generic.Dictionary<string,string>(StringComparer.Ordinal)
        let visiting=Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let manifests=Collections.Generic.Dictionary<string,string>(StringComparer.Ordinal)
        let rec visit depth definition source =
            if depth>32 then invalidOp "Native component graph depth exceeds its bound."
            let source=Path.GetFullPath source
            if not(visiting.Add source) then invalidOp "Native component manifest graph contains a cycle."
            try
                let root,config=load source
                let children = optional "components" config |> Option.map(fun values -> values.EnumerateArray() |> Seq.toArray) |> Option.defaultValue [||]
                if children.Length>32 then invalidOp "A native component has at most 32 direct dependencies."
                let direct=Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                let dependencies=[| for child in children do
                                        fields (Set.ofList ["definitionPath";"manifest"]) child
                                        let key=text "definitionPath" child
                                        if String.IsNullOrWhiteSpace key || key.Length>512 || Path.IsPathRooted key || key.Contains('\\') || Seq.exists Char.IsControl key then
                                            invalidOp "Component definition paths must be bounded relative deployment identities."
                                        if not(direct.Add key) then invalidOp "Duplicate component dependency."
                                        let childSource=Path.GetFullPath(text "manifest" child,root)
                                        match definitionSources.TryGetValue key with
                                        | true, source when source<>childSource -> invalidOp "One component definition path cannot select different manifests."
                                        | _ -> definitionSources[key]<-childSource
                                        if definitionSources.Count>32 then invalidOp "A native application supports at most 32 component definitions."
                                        match definitions.TryGetValue key with
                                        | true, existing when existing.Manifest=childSource -> ()
                                        | true, _ -> invalidOp "One component definition path cannot select different manifests."
                                        | _ ->
                                            match manifests.TryGetValue childSource with
                                            | true, other when other<>key -> invalidOp "One component manifest must have one canonical definition path."
                                            | _ -> manifests[childSource]<-key
                                            if definitions.Count>=32 then invalidOp "A native application supports at most 32 component definitions."
                                            let node=visit (depth+1) (Some key) childSource
                                            definitions.Add(key,node)
                                        yield key |]
                {Manifest=source;Root=root;Configuration=config;DefinitionPath=definition;Dependencies=dependencies}
            finally visiting.Remove source |> ignore
        let root=visit 0 None path
        if definitions.Count>32 then invalidOp "A native application supports at most 32 component definitions."
        {Root=root;Components=definitions.Values |> Seq.sortBy(fun node -> node.DefinitionPath.Value) |> Seq.toArray}

    let nodes graph = Array.append [|graph.Root|] graph.Components
    let project node = Path.GetFullPath(text "project" node.Configuration,node.Root)
    let configuration node = optional "configuration" node.Configuration |> Option.map _.GetString() |> Option.defaultValue "Release"
