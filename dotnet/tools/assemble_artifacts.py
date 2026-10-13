#!/usr/bin/env python3
"""Assemble an immutable native release plus project-consumable agent skills."""

import argparse
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

sys.dont_write_bytecode = True
from artifact_support import digest, files, no_links, read_json, verify, write_json


def assemble(candidate, output):
    repo = Path(__file__).resolve().parents[2]
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()
    candidate, output = no_links(candidate), no_links(output)
    if output.exists():
        raise ValueError("Choose a new output; existing artifact versions are immutable")
    release = candidate / "release"
    descriptor = verify(release, "release.json")
    verify(release / "runtime", "runtime.json")
    version = descriptor["version"]
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.]+)?", version):
        raise ValueError("Invalid release version")
    receipt = read_json(repo / "dotnet" / f"candidate-artifacts-{version}.json")
    archive = candidate / Path(receipt["archive"]["path"]).name
    if digest(archive) != receipt["archive"]["sha256"]:
        raise ValueError("Candidate archive differs from its validated receipt")
    for package in receipt["packages"]:
        if digest(release / "packages" / package["file"]) != package["sha256"]:
            raise ValueError("Candidate package differs from its validated receipt")
    if digest(release / "runtime" / "runtime.json") != receipt["runtime"]["descriptorSha256"]:
        raise ValueError("Candidate runtime differs from its validated receipt")
    output.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=".convex-delivery-", dir=output.parent))
    try:
        shutil.copytree(release, staging / "release")
        shutil.copy2(archive, staging / archive.name)
        shutil.copytree(candidate / "validation", staging / "validation")
        shutil.copy2(repo / "dotnet" / f"candidate-artifacts-{version}.json", staging / "validation" / "build-receipt.json")
        source = repo / "dotnet" / "skills"
        shutil.copytree(source, staging / "skills")
        skill_names = sorted(p.name for p in source.iterdir() if (p / "SKILL.md").is_file())
        docs = staging / "skills" / "convex-native" / "references" / "platform"
        selected = [repo / "dotnet" / name for name in (
            "DEVELOPMENT.md", "CAPABILITIES.md", "RELEASE_SCOPE.md", "CHANGELOG.md",
            "IMPLEMENTATION_STATUS.md", "MAINTENANCE.md", "PUBLISHING.md", "SOURCE.md",
            "Client/README.md", "FSharp/PROVENANCE.md")]
        selected += sorted((repo / "dotnet/FSharp/docs").glob("*.md"))
        portable_docs = set(p.resolve() for p in selected)
        for original in selected:
            target = docs / original.relative_to(repo)
            target.parent.mkdir(parents=True, exist_ok=True)
            def link(match):
                label, url = match.group(1), match.group(2)
                if "://" in url or url.startswith("#"):
                    return match.group(0)
                path, separator, anchor = url.partition("#")
                resolved = (original.parent / path).resolve()
                if resolved in portable_docs:
                    return match.group(0)
                if not resolved.is_relative_to(repo):
                    return match.group(0)
                remote = "https://github.com/Ynsszr/convex-backend/blob/" + commit + "/" + resolved.relative_to(repo).as_posix()
                return f"[{label}]({remote}{separator}{anchor})"
            target.write_text(re.sub(r"\[([^\]]+)\]\(([^)]+)\)", link, original.read_text()))
        for name in ("artifact_support.py", "install_skills.py"):
            shutil.copy2(repo / "dotnet/tools" / name, staging / name)
        licenses = staging / "licenses"
        licenses.mkdir()
        shutil.copy2(repo / "LICENSE.md", licenses / "backend-LICENSE.md")
        shutil.copy2(repo / "dotnet/FSharp/LICENSE", licenses / "FSharp-LICENSE")
        shutil.copy2(repo / "dotnet/FSharp/NOTICE", licenses / "FSharp-NOTICE")
        (staging / "README.md").write_text(f"""# Convex for F# {version} — native Linux x64 artifacts

This delivery contains six immutable NuGet packages, the optimized native backend,
managed host/CLI, templates, installer, validation receipts and nine native agent
skills. It is the community self-hosted fork; see `licenses/` and
`release/docs/RELEASE_SCOPE.md`. Production acceptance remains deployment-specific.

## Create and build a consumer

Set `PACK` to this directory's absolute path (do not select it through a symlink):

```sh
export CONVEX_FSHARP_RELEASE="$PACK/release"
dotnet "$CONVEX_FSHARP_RELEASE/runtime/cli/Convex.DotNet.Cli.dll" new MyApp /absolute/new/MyApp
python3 "$PACK/install_skills.py" /absolute/new/MyApp
cd /absolute/new/MyApp
dotnet convex-dotnet build convex.project.json
dotnet convex-dotnet dev convex.project.json --runtime "$CONVEX_FSHARP_RELEASE/runtime"
```

In another terminal: `dotnet run --project Web/MyApp.Web.fsproj`. The template is
anonymous demonstration code; apply your auth policy before public use. API/site
defaults are 3210/3211. Linux x64, .NET 10, bubblewrap 0.9+ and usable namespaces are
required. See the native development guide for host and retained-state requirements.

Existing projects use `release/packages` as their local NuGet source, pin all
Convex packages/tools to `{version}`, and explicitly select `release/runtime`.
Keep `Convex.*` source mapping on this feed. Preserve app-owned contracts/state;
do not reference the fork checkout. A moved pack needs updated feed/runtime paths.
`dotnet fsi release/Install.fsx` is the alternative per-user installation workflow.

## Agents and integrity

`install_skills.py PROJECT` installs all nine skills into `.agents/skills`, adds
a scoped routing section to `AGENTS.md` and records the git-ignored local
`.convex-native-release.json`.
It preserves unrelated content, supports identical reruns and refuses differing
skills/releases. Review and explicitly replace an older pack when upgrading.
It leaves the installed Convex plugin unchanged. Start with `$convex-native`.
All 41 upstream skill dispositions are in `skills/upstream-review.json` and
`skills/COMPATIBILITY.md`. Platform references travel with the skills.

Verify this complete delivery from its directory:

```sh
python3 -B -c 'from pathlib import Path; from artifact_support import verify; verify(Path("."), "artifact.json")'
```

The installer also verifies it before
copying. Hashes prove consistency, not publisher authenticity. Keep this delivery
immutable; put new logs/state/receipts elsewhere. `artifact.json` pins the source,
payload and every included file. The original release archive remains unchanged;
the small skills archive carries the additional agent workflows separately.

Validation in `validation/` belongs to this build. No production deployment or
installed 0.3.1 migration is implied, and remote CI runner acceptance is separate.
""")
        skill_archive = staging / f"convex-native-skills-{version}.tar.gz"
        with tarfile.open(skill_archive, "w:gz") as archive_file:
            archive_file.add(staging / "skills", arcname="skills")
        inventory = [{"path": path, **item} for path, item in files(staging).items()]
        write_json(staging / "artifact.json", {
            "format": "convex-native-artifacts", "version": version, "rid": "linux-x64",
            "sourceCommit": receipt["sourceCommit"], "documentationBaseCommit": commit,
            "releaseDescriptorSha256": digest(release / "release.json"),
            "upstreamCommit": receipt["upstreamCommit"], "skills": skill_names,
            "files": inventory
        })
        verify(staging, "artifact.json")
        staging.rename(output)
        print(f"Assembled {version}: {len(inventory)} files, {len(skill_names)} skills -> {output}")
    finally:
        if staging.exists():
            shutil.rmtree(staging)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("candidate", type=Path, help="Validated candidate with release/archive/validation")
    parser.add_argument("output", type=Path, help="New immutable artifact directory")
    args = parser.parse_args()
    assemble(args.candidate, args.output)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError) as error:
        raise SystemExit(str(error)) from error
