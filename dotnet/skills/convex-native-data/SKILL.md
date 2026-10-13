---
name: convex-native-data
description:
  "Back up, restore, migrate or seed a self-hosted native F# Convex database.
  Use for snapshot drills, schema/backfill changes, component data and
  retained-state recovery."
---

# Native data changes and recovery

Use `convex-native-operations` to identify the target and authorized scope
first. Read the selected release's native import/export commands and limits.
Production data and backups can contain secrets or personal data; retain private
permissions and keep them outside source, artifacts and public issues.

```text
dotnet convex-dotnet export ORIGIN NEW_ZIP_FILE ADMIN_VARIABLE --include-storage
dotnet convex-dotnet import ORIGIN zip - FILE requireEmpty ADMIN_VARIABLE
dotnet convex-dotnet import ORIGIN jsonArray|jsonLines|csv TABLE FILE requireEmpty|append|replace ADMIN_VARIABLE
```

Export refuses an existing destination. Choose `requireEmpty` for a fresh
restore; `replace` is an intentional overwrite, not a routine retry. Streaming
commands are bounded to 2 GiB in 0.3.2; check size before planning large
recovery. A lost reply can mean an applied effect; reconcile/inspect before
repeating it.

## Backup and restore drill

1. Record source instance, runtime/schema version, snapshot time, digest,
   storage inclusion and recovery objective. Securely retain the original
   snapshot.
2. Create a separate disposable backend with new state, identity, credentials
   and unused ports. Restore the snapshot there; never rehearse on the source
   database.
3. Verify IDs/references, important counts, representative authorized queries,
   nested component data and storage reads. A full snapshot is different from
   importing each table independently; do not assume IDs survive arbitrary
   imports.
4. Record duration and evidence. Remove only the specifically identified drill
   state after its receipt and retained backup are verified. Keep the actual
   recovery copy and the source's data/identity/journal set.
5. Configure a schedule only if requested, using the owner's actual scheduler,
   encryption and retention policy. A cron declaration alone is not a backup
   system.

## Migration and seed

Widen a populated schema first (for example optional new fields), deploy
compatible readers/writers, backfill through bounded cursor batches with durable
checkpoints, verify all retained shapes, then tighten. Make batches idempotent
by business key or progress state; bound OCC contention and concurrent writers.
Rehearse material changes on the snapshot-restored target. Code rollback cannot
undo a destructive data rewrite; specify the restore/cutover plan and expected
data loss window.

Use native internal mutations or an explicitly selected import mode for
fixtures. Upsert deterministic fixture keys and keep them separate from user
records. Never clear retained tables merely to make seeding repeatable. Validate
tenant scope and representative results rather than only counting rows.
`@convex-dev/migrations` and Vitest are not native CLR migration/test tools.
