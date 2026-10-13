---
name: convex-native-review
description:
  "Review, audit, optimize or assess production readiness of native F# Convex
  apps or this fork. Covers security, contracts, runtime boundaries,
  performance, targeted tests and evidence-backed error fixes."
---

# Review and verify efficiently

Detect the selected release with `convex-native`. Read source and existing
receipts before running commands. Audit requests are read-only; a request to fix
issues authorizes scoped repairs. Preserve concurrent edits. Prioritize
security, data loss, uncertain effects and broken consumer behavior above
cosmetic changes.

1. Map public functions, contracts, auth/tenant policy, indexed reads, component
   ownership, external effects and retained state. Trace one actual caller to
   the runtime boundary; do not substitute a grep checklist for causal analysis.
2. Review negative paths: supplied user/parent IDs, missing identities,
   cross-tenant reads, malformed contracts, replay/uncertain effects,
   cancellation, reconnect, and resource bounds where relevant. Intentional
   anonymous functions and bounded residual filters need contextual review, not
   automatic findings.
3. For performance inspect read/write sets, partition cardinality, pagination,
   hot documents, action concurrency and actual logs. Host CPU/RAM/disk/egress
   are the self-hosted cost basis. No Cloud 72-hour insight claims without
   measurements.
4. Group fixes, then run one appropriate managed build/test pass. Add regression
   coverage only for material changed behavior, especially negative security or
   lifecycle cases. `convex-test` cannot execute native assemblies. Use the
   existing .NET framework and disposable native acceptance harness as
   applicable.
5. Use package-only consumption to verify a distribution change; use a real
   native backend for transaction/auth/isolation/deployment changes; use
   provider test mode for provider effects. Avoid repeating broad checks after
   no relevant change.
6. Report each finding with severity, file/location, concrete trigger, effect
   and remedy. Record what was actually run and any remaining acceptance
   boundary. A compile pass, a mocked test, package installation and production
   use are separate claims. Do not invent a numerical readiness score.

A deployment is ready only for its exercised scope: exact package/runtime,
authorization/TLS, private secrets, supported sandbox host, bounded capacity,
restore evidence, telemetry and rollback/reconciliation. Static source review
cannot certify another machine's services or arbitrary customer workloads.

For production error repair use bounded evidence, reproduce then confirm the
fix, and prepare a reviewable diff/PR if requested. Do not require Sentinel
merely to diagnose an error. Do not merge/deploy beyond user authority or upload
the session to a feedback endpoint. If asked for feedback, prepare a sanitized
local report and establish the intended destination before any external
submission.
