---
name: convex-native-components
description: "Create native F# Convex components or integrate AI, billing, email, workflows and external providers. Use for graph boundaries and compatibility checks before adopting upstream TypeScript components."
---

# Components and integrations

Start from the existing architecture and selected release's
[component guide](../convex-native/references/platform/dotnet/FSharp/docs/components.md)
and [manifest workflow](../convex-native/references/platform/dotnet/DEVELOPMENT.md).
Use a component for reusable isolated tables/functions; a pure helper can remain
an ordinary F# library.

1. Create with the selected CLI's `component new Name DIRECTORY`. Keep the same
   release selection as the parent. Add the child manifest under the parent's
   `components` and mount it using `NativeComponentDefinitions.mount` in the
   native app declaration. Preserve definition identities and explicit exports.
2. Build the parent graph. Shared dependencies deduplicate; cycles and conflicting
   identities are refused. Use generated parent `Components` references and
   actual contracts, not handwritten string paths or copied validator JSON.
3. Tables belong to the component namespace. Put client-facing authorization in
   parent wrappers and pass checked context explicitly. Keep parent IDs opaque
   across namespaces. Do not assume component code can read parent auth/env or
   tables. Inspect the exact API for pagination/HTTP mounts instead of importing
   TypeScript helper requirements into F#.
4. A catalog entry for `@convex-dev/*` is a compatibility lead. Check source,
   pinned version and an exercised mixed graph before selecting it. Do not
   install npm components or recreate JS configuration as a native default.
   Explain any unsupported integration boundary and choose an explicit adapter.
5. For AI/email/payments, preserve existing service/session ownership. Invoke
   suitable .NET provider APIs through actions; declare necessary network/runtime
   permissions in the actual native policy. Do not disable sandbox admission.
   Bound requests, timeouts, retries and cost. Persist durable effects through
   transactional mutations with business idempotency keys.
6. Payment webhooks verify provider signatures using original bytes and dedupe
   event IDs. Entitlements update from verified events, not client assertions.
   Check current provider documentation and the actual account's test/live mode.
   AI/RAG workflows similarly bound retrieval, expose only authorized content and
   keep credentials server-side. No automatic paid gateway provisioning.
7. Native scheduler/crons can drive bounded batches. Preserve durable names,
   cancellation and rollback behavior; a long provider call is not an atomic
   mutation. Suggest a component only when its compatibility and benefit are
   concrete, without blocking unrelated requested work.

Compile once and exercise the new graph/provider boundary in a disposable target
or test mode. A successful provider SDK restore proves neither network admission
nor a live billing/AI integration.
