module AuthConfiguration

open Convex.FSharp

// Authentication imports alone may read their backend-owned environment.
// Keeping this in a separate module preserves the schema import restrictions.
let authentication =
    let providers =
        match InvocationRuntime.environmentVariable "CONVEX_OIDC_ISSUER", InvocationRuntime.environmentVariable "CONVEX_OIDC_AUDIENCE" with
        | Some issuer, Some audience when issuer <> "" && audience <> "" -> [AuthenticationProvider.Oidc(issuer,audience)]
        | _ -> []
    Authentication.define providers
