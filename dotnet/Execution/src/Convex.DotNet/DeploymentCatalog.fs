namespace Convex.DotNet.Execution

open System
open Convex.Runtime

/// Native declaration catalogues contain language-neutral contracts. The
/// selected frontend supplies validators and wire data; Convex owns admission.
module DeploymentCatalog =
    let describeSelected (deployment: DeploymentAssembly) (moduleKind: string option) (export: string option) =
        match moduleKind, export with
        | None, None | Some "functions", None -> ()
        | Some ("schema" | "auth" | "http" | "crons" | "component"), Some selected when
            not(String.IsNullOrWhiteSpace selected) && selected.Contains(':') -> ()
        | _ -> invalidOp "A supported module kind and matching export selector are required"
        let functionCandidate (propertyType: Type) =
            propertyType=typeof<obj> || typeof<IRegisteredFunction>.IsAssignableFrom propertyType
        let select identity propertyType =
            match moduleKind, export with
            | None, None -> true
            | Some "functions", None -> functionCandidate propertyType
            | Some ("schema" | "auth" | "crons" | "component"), Some selected -> identity=selected
            | Some "http", Some selected -> identity=selected || functionCandidate propertyType
            | _ -> false
        let declarations=deployment.Declarations(select) |> Array.sortBy fst
        if declarations.Length>10_000 then invalidOp "Deployment catalogue exceeds its declaration bound"
        match moduleKind, export with
        | Some ("schema" | "auth" | "crons" | "http" | "component" as kind), Some selected ->
            let value =
                declarations |> Array.tryFind(fun (identity, _) -> identity=selected)
                |> Option.map snd
                |> Option.defaultWith(fun () -> invalidOp "The selected native declaration is not exported")
            match value with
            | :? IDeclaration as definition when ContractNames.declarationKind definition.Kind=kind -> ()
            | _ -> invalidOp "The selected native declaration differs from the admitted module kind"
        | _ -> ()
        let functions =
            declarations |> Array.choose(fun (path, value) ->
                match value with
                | :? IRegisteredFunction as definition ->
                    Some(RuntimeJson.objectValue [
                        "functionPath", box path
                        "kind", box (ContractNames.functionKind definition.Kind)
                        "visibility", box (ContractNames.visibility definition.Visibility)
                        "arguments", box definition.Arguments
                        "returns", box definition.Returns
                    ])
                | _ -> None)
        let selected kind =
            if moduleKind.IsSome && moduleKind<>Some(ContractNames.declarationKind kind) then [||]
            else
                declarations |> Array.choose(fun (path, value) ->
                    match value with
                    | :? IDeclaration as definition when definition.Kind=kind ->
                        Some(RuntimeJson.objectValue ["export", box path; "value", box (definition.Export())])
                    | _ -> None)
        let http =
            if moduleKind.IsSome && moduleKind<>Some "http" then [||]
            else
                declarations |> Array.choose(fun (identity, value) ->
                    match value with
                    | :? IHttpRouterDefinition as router ->
                        let routes =
                            router.Routes |> Seq.map(fun route ->
                                let matches =
                                    declarations |> Array.choose(fun (identity, value) ->
                                        match value with
                                        | :? IRegisteredFunction as handler when
                                            handler.Kind=FunctionKind.HttpAction && Object.ReferenceEquals(handler, route.Handler) -> Some identity
                                        | _ -> None)
                                if matches.Length<>1 then
                                    invalidOp "A native HTTP route requires one unambiguous exported registered HTTP action"
                                RuntimeJson.objectValue [
                                    "method", box route.Method
                                    "path", box (route.Path + (if route.Prefix then "*" else ""))
                                    "functionPath", box matches[0]
                                ]) |> Seq.toArray
                        Some(RuntimeJson.objectValue ["export", box identity; "routes", box routes])
                    | _ -> None)
        RuntimeJson.objectValue [
            "version", box 1
            "functions", box functions
            "schemas", box (selected DeclarationKind.Schema)
            "authConfigs", box (selected DeclarationKind.Authentication)
            "httpRouters", box http
            "crons", box (selected DeclarationKind.CronJobs)
            "componentDefinitions", box (selected DeclarationKind.Component)
        ]

    let describe deployment = describeSelected deployment None None
