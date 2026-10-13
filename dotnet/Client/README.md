# Convex.Client

Native .NET/F# client. Applications reference the `Convex.Client` NuGet package and `open Convex.Client`.

- `Reactive.Client` shares one WebSocket connection across typed queries, deduplicates equivalent subscriptions, and publishes complete server transitions as immutable snapshots. It supports dynamic subscriptions, reconnect, session-scoped mutation replay through the backend's deduplication protocol, and pure optimistic updates that rebase on server results. Actions are never automatically replayed.
- `Paginated.Client` keeps loaded cursor ranges subscribed, replaces split pages once both children arrive, and discards previous pages and cursors when authentication changes.
- `ConvexHttp` exposes typed query, mutation, and action calls with one delivery attempt and explicit uncertain outcomes for effects whose replies are lost.
- `ConvexSubscriptions` exposes bounded, typed single-query subscriptions. The caller owns reconnection; a peer closing the socket is reported as `Unavailable`.

Run the shared client's `RunAsync` owner loop and dispose query leases, observers, and clients when they are no longer needed. Observers receive monotonically increasing revisions; a slow observer may coalesce pending views. Authentication replacement clears the previous identity's query values and pending request authority. Releasing a query's last lease discards its cached server value, so a later subscription starts loading again.

The client supports native .NET and Blazor WebAssembly. Synchronous HTTP wrappers require a runtime that permits blocking; use the asynchronous APIs in browser applications. Application identity policy, credentials, authorization, and reconciliation of uncertain effects remain application responsibilities. See [CAPABILITIES.md](../CAPABILITIES.md) and [RELEASE_SCOPE.md](../RELEASE_SCOPE.md) for the supported platform and verification boundaries.
