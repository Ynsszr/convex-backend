---
name: convex-native-client
description: "Build or fix Convex.Client F# and Blazor applications: shared reactive queries, pagination, optimistic mutations, reconnect, identity changes and subscription disposal against the native Convex fork."
---

# Native clients

Read the selected [client guide](../convex-native/references/platform/dotnet/Client/README.md)
and [development examples](../convex-native/references/platform/dotnet/DEVELOPMENT.md).
Use the generated public `Api` catalog plus the application's shared contracts.
Internal/component references are not browser-callable APIs.

- Share one `Reactive.Client` per endpoint and identity scope. Start `RunAsync`
  with its lifetime token. Watch typed queries and read one immutable snapshot
  after each transition. Represent Loading, Ready and Failed explicitly.
- Dispose query watches/observers on navigation; keep the shared client alive
  until its owning scope ends. Dispose pagers when filters change. Do not build
  another polling cache around already reactive queries.
- Use pure optimistic snapshot transformations. They rebase over new server
  state and settle after a commit becomes visible. Do not append the same
  optimistic result again in a reconnect callback or manually repeat a mutation.
- The sync protocol can replay a mutation with its original session/request
  identity. Actions and direct HTTP operations do not gain that guarantee.
  Cancellation or a lost reply after dispatch may mean an uncertain effect.
- Feed verified provider tokens through `SetAuthentication(Some token)` on
  sign-in/refresh and `SetAuthentication(None)` on logout. Identity changes clear
  old private snapshots/cursors. Never put admin credentials in Blazor/Web assets.
- Use `Paginated.Client` with the actual generated pagination contract. Keep
  loaded ranges subscribed; render the pager's coherent results/status. Do not
  interpret a split replacement or Loading state as an empty authoritative list.
- Blazor WebAssembly is included in the native template. Keep network work out
  of rendering and use the UI framework's dispatcher for observer-driven renders.
  Browser code remains F#/.NET; a separate TypeScript client is not required.

Build the affected client project and exercise the changed lifecycle once. For
auth/reconnect work, include logout or a dropped connection in that focused check.
Use two tabs only when proving cross-client updates; do not rerun the entire
platform acceptance suite for a UI-only edit.
