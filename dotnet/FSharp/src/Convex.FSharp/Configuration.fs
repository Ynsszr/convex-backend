namespace Convex.FSharp

open System

[<RequireQualifiedAccess>]
type HttpMethod = Get | Post | Put | Patch | Delete | Options

[<RequireQualifiedAccess>]
type HttpRoute =
    | Exact of method:HttpMethod * path:string * handler:Convex.Runtime.IHttpActionRegistration
    | Prefix of method:HttpMethod * pathPrefix:string * handler:Convex.Runtime.IHttpActionRegistration

/// Native routing retains the actual registered handlers. The deployment
/// evaluator resolves them against exported HTTP actions before admission.
type NativeHttpRouterDefinition internal (routes: HttpRoute list) =
    member _.Routes = routes
    interface Convex.Runtime.IDeclaration with
        member _.Kind = Convex.Runtime.DeclarationKind.HttpRouter
        member _.Export() = Convex.Runtime.RuntimeJson.nullValue
    interface Convex.Runtime.IHttpRouterDefinition with
        member _.Routes =
            routes |> List.map (fun route ->
                let method, path, prefix, handler =
                    match route with
                    | HttpRoute.Exact(method, path, handler) -> method, path, false, handler
                    | HttpRoute.Prefix(method, path, handler) -> method, path, true, handler
                let method =
                    match method with
                    | HttpMethod.Get -> "GET" | HttpMethod.Post -> "POST"
                    | HttpMethod.Put -> "PUT" | HttpMethod.Patch -> "PATCH"
                    | HttpMethod.Delete -> "DELETE" | HttpMethod.Options -> "OPTIONS"
                let action=handler.Function
                if action.Kind<>Convex.Runtime.FunctionKind.HttpAction then
                    invalidArg "handler" "A native HTTP action registration is required"
                Convex.Runtime.HttpRoute(method, path, prefix, action))
            |> List.toArray :> System.Collections.Generic.IReadOnlyList<Convex.Runtime.HttpRoute>

module HttpRouter =
    let methodName = function
        | HttpMethod.Get -> "GET" | HttpMethod.Post -> "POST" | HttpMethod.Put -> "PUT"
        | HttpMethod.Patch -> "PATCH" | HttpMethod.Delete -> "DELETE" | HttpMethod.Options -> "OPTIONS"
    let validate routes =
        let mutable seen=Set.empty
        for route in routes do
            let prefix,method,path,handler =
                match route with
                | HttpRoute.Exact(method,path,handler) -> false,method,path,handler
                | HttpRoute.Prefix(method,path,handler) -> true,method,path,handler
            if isNull path || not(path.StartsWith("/",StringComparison.Ordinal)) || (prefix && not(path.EndsWith("/",StringComparison.Ordinal))) then
                invalidArg "routes" "HTTP paths start with '/'; prefix routes also end with '/'"
            if path.StartsWith("/.files/",StringComparison.Ordinal) || (not prefix && path="/.files") then
                invalidArg "routes" "Convex storage owns the /.files path"
            if isNull(box handler) || handler.Function.Kind<>Convex.Runtime.FunctionKind.HttpAction then
                invalidArg "routes" "A registered HTTP action is required"
            let key=prefix,methodName method,path
            if seen.Contains key then invalidArg "routes" "The same HTTP method and route was registered twice"
            seen<-seen.Add key
        routes
    let create routes =
        let routes=validate routes
        NativeHttpRouterDefinition(routes)

[<RequireQualifiedAccess>]
type JwtAlgorithm = Rs256 | Es256

[<RequireQualifiedAccess>]
type AuthenticationProvider =
    | Oidc of domain: string * applicationID: string
    | CustomJwt of issuer: string * jwks: string * algorithm: JwtAlgorithm * applicationID: string option

/// The owning backend validates provider URLs, algorithms and auth semantics.
/// Exporting this declaration does not authenticate a user or grant authority.
type AuthenticationConfiguration internal (providers: AuthenticationProvider list) =
    member _.Providers = providers
    member _.Export() =
        let quote = ValidatorJson.quote
        let provider = function
            | AuthenticationProvider.Oidc(domain, applicationID) ->
                "{\"domain\":" + quote domain + ",\"applicationID\":" + quote applicationID + "}"
            | AuthenticationProvider.CustomJwt(issuer, jwks, algorithm, applicationID) ->
                let algorithm = match algorithm with JwtAlgorithm.Rs256 -> "RS256" | JwtAlgorithm.Es256 -> "ES256"
                "{\"type\":\"customJwt\",\"issuer\":" + quote issuer + ",\"jwks\":" + quote jwks +
                ",\"algorithm\":" + quote algorithm +
                (applicationID |> Option.map (fun value -> ",\"applicationID\":" + quote value) |> Option.defaultValue "") + "}"
        "{\"providers\":[" + (providers |> List.map provider |> String.concat ",") + "]}"
    interface Convex.Runtime.IDeclaration with
        member _.Kind = Convex.Runtime.DeclarationKind.Authentication
        member this.Export() = Convex.Runtime.RuntimeJson.parse(this.Export())
    member this.ToNative() = this

module Authentication =
    let define providers = AuthenticationConfiguration(providers)

/// Component environment variables are strings, string literals or unions thereof.
type EnvironmentDeclaration = {Name:string;Validator:Validator;Optional:bool}
[<RequireQualifiedAccess>]
type EnvironmentBinding = Literal of string | FromParent of string

module ComponentEnvironment =
    let rec stringLike = function
        | Validator.String | Validator.LiteralString _ -> true
        | Validator.Union members -> not members.IsEmpty && List.forall stringLike members
        | _ -> false
    let validate declarations =
        SchemaNames.unique "component environment" (declarations |> List.map(fun declaration -> declaration.Name))
        for declaration in declarations do
            if not(stringLike declaration.Validator) then invalidArg "environment" "Component environment validators must be string-like"
        declarations
    let required name validator = {Name=name;Validator=validator;Optional=false}
    let optional name validator = {Name=name;Validator=validator;Optional=true}

/// Decode an explicit environment projection supplied by the owning invocation.
/// No environment variables are read from the developer's process.
module RuntimeEnvironment =
    let inline decode<'T> (environment:obj) : 'T = Codec.decode<'T> environment
    let get name (environment:obj) =
        let value=NativeValue.getProperty environment name
        if NativeValue.isUndefined value then None else Some(Codec.decode<string> value)
