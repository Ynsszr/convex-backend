namespace ConvexApp.Backend

open Convex.FSharp

module HttpEndpoints =
    // HTTP routes run on the backend's site endpoint, not its query API port.
    let health = Convex.httpActionRequestResponse (fun _ _ -> async {
        return Http.ok "ok"
    })

    let router = HttpRouter.create [
        HttpRoute.Exact(HttpMethod.Get, "/health", health)
    ]
