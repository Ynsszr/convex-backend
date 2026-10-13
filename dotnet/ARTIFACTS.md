# Shared release artifacts

The 0.3.2 delivery is assembled under the workspace's sibling
`artifacts/convex-fsharp/0.3.2` directory. Its `README.md` is the consumer entrypoint.
It contains the validated six-package feed, optimized Linux x64 runtime, original
release archive, nine project-installable native agent skills, the 41-entry
upstream skill compatibility review, licenses and sanitized validation receipts.

Consumers set `CONVEX_FSHARP_RELEASE` to the delivery's `release` directory. The
native CLI can create an app directly from that directory, without installing a
global tool or referring to the fork checkout. The app uses ordinary NuGet
references, a local tool manifest and an explicit runtime path. Its generated
NuGet.Config maps `Convex.*` packages to the selected local feed.

Run `python3 /absolute/delivery/install_skills.py /absolute/native/project` to
install the whole pack into `.agents/skills`. This also records the selected
release and adds a bounded native routing section to the project's `AGENTS.md`.
It verifies the delivery and release inventories before writing, preserves
unrelated files and refuses different existing skill content. The upstream
Convex plugin remains unchanged for upstream applications.

## Maintainer production

The existing release and package producers build the immutable runtime/package
payloads. Once their acceptance receipt is complete, assemble the outer delivery:

```sh
python3 -B dotnet/tools/assemble_artifacts.py \
  .local/release-candidates/0.3.2-20261013 \
  ../artifacts/convex-fsharp/0.3.2
```

The producer refuses an existing destination, verifies package/archive/runtime
hashes against `candidate-artifacts-0.3.2.json`, and copies only curated release,
documentation, skills, licensing and validation inputs. `artifact.json` pins all
delivered files. Python 3.10+ is used only for artifact/skill installation; the
application/runtime still uses .NET 10 and the native backend.

Source skill instructions live in `dotnet/skills`. The producer adds versioned
platform references; source skill directories alone are not the distributable
pack. Package bytes and the original archive remain unchanged when delivering
agent documentation. A later changed delivery uses a new destination/versioned
skill-pack path rather than silently overwriting existing artifacts.

## Scope and retention

This is local distribution plus GitHub source publication, not a NuGet.org upload
or an official Convex release. The complete fork retains its root FSL license;
the F# frontend preserves its separate metadata and notices. Runtime/platform
requirements and deployment-specific acceptance still apply.

Build caches and duplicate candidate staging may be removed after checking the
shared delivery's inventory and the absence of active users of those paths.
Retain installed releases, application database/storage/identity/journals,
`.local/recovery`, user IDE settings and unrelated repository state. Historical
candidate receipts preserve the original build locations even after staging is
removed; the shared delivery is the consumer copy.
