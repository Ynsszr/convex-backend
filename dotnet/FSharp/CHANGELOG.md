# Changelog

Release notes for the native SDK are in
[the platform changelog](../CHANGELOG.md). The current package version is 0.3.1.

## Historical 0.1.0 candidate

The notes below describe an earlier package candidate. They are not the 0.3.1
release record.

Added checked custom contracts and contextual Async/Task function variants;
table-qualified CRUD and compound set/remove patches; asynchronous iterator
cleanup; document-aware advanced pagination; filtered vectors; staged schemas;
standard/custom identity claims; metadata/nested-call limit adapters; Blob
storage; explicit HTTP/streaming/Convex JSON; component/environment
configuration and audit/service adapters.

Fixed loss of structured application errors at the native Error boundary, the
invalid modern-versus-legacy storage-ID equality assumption, native
request/response behavior, and permissive generated TypeScript field paths.

Added Node-action targeting, validator-derived declarations, staged
successful-build selection, explicit project/option validation, watch recovery
and complete owned-process-group shutdown. Independent .NET and Fable consumers
validate the actual NuGet assembly/source closure.

Replaced native example harnesses that depended on shared working directories
and a private capture with isolated applications and synthetic fixtures. Removed
stale generated application bundles and historic TestResults from source
control; tests regenerate outputs. Updated toolchain/current status, standard
license text and provenance.

No public registry release has been performed. The local 0.3.1 release is
recorded in [the platform changelog](../CHANGELOG.md) and
[implementation status](../IMPLEMENTATION_STATUS.md).
