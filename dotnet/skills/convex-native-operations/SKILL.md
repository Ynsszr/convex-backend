---
name: convex-native-operations
description: "Operate this self-hosted F# Convex runtime: local dev, deployment/reconciliation, environment variables, logs, monitoring, TLS/domains and incident triage using convex-dotnet."
---

# Operate a selected native instance

Read [operations and runtime commands](../convex-native/references/platform/dotnet/DEVELOPMENT.md)
and [deployment checklist](../convex-native/references/platform/dotnet/RELEASE_SCOPE.md).
Before a mutation identify the origin, durable instance, database/storage,
journal, runtime version/hash and principal. State the target briefly. Continue
within the user's existing authorization; obtain missing production/destructive
scope only after the concrete change is prepared. Read-only requests remain so.

## Command surface

From a project with the matching local tool, prefix these with
`dotnet convex-dotnet`. Uppercase credential arguments name environment variables
already supplied through a secure channel; they are never secret values.

```text
build convex.project.json
review-project ABSOLUTE_NATIVE_PROJECT_JSON
deploy-project ORIGIN ABSOLUTE_NATIVE_PROJECT_JSON ADMIN_KEY_VARIABLE
deploy-graph ORIGIN ABSOLUTE_COMPILED_GRAPH_JSON ADMIN_KEY_VARIABLE
reconcile ORIGIN ADMIN_KEY_VARIABLE [--deployment-identity NAME]
run ORIGIN query|mutation|action MODULE:FUNCTION ARGUMENTS_JSON_FILE ADMIN_VARIABLE
env list ORIGIN ADMIN_VARIABLE
env set ORIGIN NAME VALUE_VARIABLE ADMIN_VARIABLE
env remove ORIGIN NAME ADMIN_VARIABLE
logs ORIGIN CURSOR ADMIN_VARIABLE
tables ORIGIN ADMIN_VARIABLE
```

Use numeric log cursors and bounded observation windows. List environment names
only. Check process ownership and actual endpoint behavior; a listening port or
systemd unit alone is not acceptance. Keep sanitized receipts, not raw tokens,
request bodies or provider logs containing personal data.

## Uncertain outcomes and retained state

Exit 0 confirms the operation; 1 is invalid input, 2 a refusal, 3 uncertainty.
Do not retry a mutation/import/deployment simply because its reply was lost.
Inspect the owning journal and use `reconcile`. Local 0.3.2 history lives under
`.convex/dev/deployment-by-instance`; select it with
`CONVEX_DOTNET_STATE_DIRECTORY` and pass the exact `instance` from `identity.json`
as `--deployment-identity`. A local port change does not create a new instance.
Legacy URL-scoped receipts need their original owning target; missing or unresolved
receipts leave the fence intact. Preserve data, identity, storage and journals as
one retained set. Never delete the journal or mint a new identity to clear a fence.

Backend/keygen secret files must be owner-only regular files containing 64 hex
characters, optionally followed by LF/CRLF. Use `--instance-secret-file`, not
secret values on argv. Retain private state permissions. Runtime inspection
requires actual directories; linked/tampered payloads are refused.

## Hosting, observability and repair

Self-hosted API and HTTP-site endpoints are separate. Use the selected reverse
proxy/TLS configuration and WebSocket forwarding; DNS records and provider
verification depend on that host. Do not invent a mandatory Convex Cloud TXT
record or assume a `*.convex.app` site. Match OIDC/browser origins to actual TLS
endpoints. Validate the effect of a domain change, including auth and subscriptions.

Use existing host logs, application telemetry and bounded native log reads.
Cloud insights, a Sentinel npm component or a `wait_for_event` MCP tool may be
unavailable. Report measurement gaps explicitly; add bounded/redacted .NET
instrumentation only within scope. Monitoring later requires an authorized
scheduler; do not keep an unrequested infinite watch running.

For incidents correlate error, function, time and revision; distinguish config,
transient transport and code defects. Reproduce/fix in a disposable target, then
promote within authorized scope and check the error signature again. Deployment
is not implied by preparing a code fix.
