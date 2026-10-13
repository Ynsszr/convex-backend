---
name: convex-native-auth
description:
  "Implement or audit identity and authorization for native F# Convex: OIDC
  declarations, JWT clients, tenant/document ownership, public function policy
  and webhook authentication."
---

# Identity and authorization

Read the selected development guide's authentication section, manifest `auth`
declaration, provider configuration and actual public functions. Preserve the
existing identity provider and application's ownership model.

1. Native authentication declarations register the intended issuer/audience with
   the owning backend. Resolve deployment environment in its declaration
   context; offline code generation must not bake secrets into capsules or
   client bundles. Inspect `Authentication` in the selected SDK for exact
   declaration signatures.
2. Resolve the verified caller using `ctx.Auth.GetUserIdentity()`. Treat missing
   identity explicitly. Scope durable identity by verified issuer/token identity
   as appropriate; a display name or self-asserted email is not a grant.
3. Trace four access paths: caller identity accepted as arguments, missing row
   ownership, public reads by supplied ID, and writes into a parent/container.
   Verify membership and resource ownership before both reads and writes. A
   parent write must authorize the parent, not merely the new row's creator.
4. Check list/index predicates actually constrain tenant/user scope, and project
   only permitted result fields. A valid ID and an authenticated user are not
   sufficient authority. Keep internal helpers internal where no public contract
   is intended; do not break intentionally anonymous APIs based on a blanket
   rule.
5. Pass only necessary, checked authority across component boundaries.
   Components have separate tables; they do not inherit arbitrary parent storage
   access. A users table is optional when the application's verified claims and
   membership model already support its policy. Do not invent a duplicate
   session system.
6. Clients refresh tokens and clear identity on logout. Provider UI, passkeys or
   OAuth flows must use an actually supported provider integration. The upstream
   TypeScript passkey component is not implicitly a native F# package.
7. Verify owner success, another principal's refusal and anonymous behavior for
   the changed protected operation. Include a mixed-tenant list when applicable.
   Use .NET tests and disposable signed JWT/native backend fixtures where
   needed; regex matches or compilation alone cannot establish authorization.

Use `convex-native-operations` for environment changes. Never print secret
values, generate production provider identities arbitrarily, or weaken
assertions to make an authorization test pass.
