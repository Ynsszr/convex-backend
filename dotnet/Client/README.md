# Convex.Client

Generic native .NET/F# client. Applications reference the NuGet package and `open Convex.Client`.
`ConvexHttp` exposes typed query/mutation/action calls and explicit uncertain outcomes.
`ConvexSubscriptions` exposes bounded, typed single-query WebSocket subscriptions.

This initial client does not claim multi-query atomic snapshot, optimistic-update, or React-client parity.
Product identity policy, credentials, authorization and effect reconciliation belong to the application.
No other product source is required to build this package.
