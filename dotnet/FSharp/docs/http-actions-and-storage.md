# Native HTTP and storage

Native HTTP actions use the owning request/response types and routes declared through the native declaration owner. No authored HTTP TypeScript router is required. [Http.fs](../src/Convex.FSharp/Http.fs), [Declarations.fs](../src/Convex.FSharp/Declarations.fs) and [NativeConfiguration.fs](../src/Convex.FSharp/NativeConfiguration.fs) define the actual APIs.

Ordinary JSON and Convex tagged JSON are deliberate separate response formats. Preserve status, headers, body bytes, exact/prefix routing and HEAD semantics. Bounded streaming owns cancellation, backpressure and close/abort behavior; do not replace it with unbounded buffering.

[Storage.fs](../src/Convex.FSharp/Storage.fs) defines query-readable metadata/URLs, mutation/action write capabilities and action binary transfer. Storage IDs preserve their actual table/legacy ownership. Upload URL generation, Store, Delete and streaming completion are distinct effects. A missing terminal response does not authorize replay.

The original Convex storage and HTTP providers remain authoritative. Buffered/streamed ceilings and IPC bounds are recorded in [STATUS](../STATUS.md). Real routing, storage cleanup, interrupted streams and worker retirement need current native receipts.
