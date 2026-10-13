#!/usr/bin/env python3
"""Install a verified native skill pack into one application, preserving edits."""

import argparse
import os
from pathlib import Path
import shutil
import sys
import tempfile

sys.dont_write_bytecode = True
from artifact_support import digest, files, no_links, read_json, verify, write_json

START = "<!-- convex-native:start -->"
END = "<!-- convex-native:end -->"


def atomic_text(path, content):
    fd, name = tempfile.mkstemp(prefix=".convex-skills-", dir=path.parent)
    try:
        with os.fdopen(fd, "w") as stream:
            stream.write(content)
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def install(pack, project, release):
    pack, project, release = map(no_links, (pack, project, release))
    metadata = verify(pack, "artifact.json")
    release_data = verify(release, "release.json")
    if release_data["version"] != metadata["version"]:
        raise ValueError("Skills and selected release versions differ")
    if digest(release / "release.json") != metadata["releaseDescriptorSha256"]:
        raise ValueError("The selected release differs from this skill pack's payload")
    if not (project / "convex.project.json").is_file():
        raise ValueError("Select an existing native application with convex.project.json")
    no_links(project / "convex.project.json")
    destination = no_links(project / ".agents" / "skills")
    names = metadata["skills"]
    planned = []
    for name in names:
        if not name.startswith("convex-native") or "/" in name or "\\" in name:
            raise ValueError("Unexpected native skill name")
        source, target = pack / "skills" / name, no_links(destination / name)
        if target.exists():
            if not target.is_dir() or files(source) != files(target):
                raise ValueError(f"Preserving different existing skill: {target}")
        else:
            planned.append((source, target))
    receipt_path = no_links(project / ".convex-native-release.json")
    receipt = {
        "format": "convex-native-project-release", "version": metadata["version"],
        "release": str(release), "artifactDescriptorSha256": digest(pack / "artifact.json"),
        "sourceCommit": metadata["sourceCommit"], "skills": names
    }
    if receipt_path.exists() and read_json(receipt_path) != receipt:
        raise ValueError("Existing native release receipt differs; review the upgrade before replacing it")
    agents = no_links(project / "AGENTS.md")
    previous = agents.read_text() if agents.exists() else ""
    ignore_path = no_links(project / ".gitignore")
    ignored = ignore_path.read_text() if ignore_path.exists() else ""
    receipt_ignore = "/.convex-native-release.json"
    if receipt_ignore not in ignored.splitlines():
        ignored += ("\n" if ignored and not ignored.endswith("\n") else "") + receipt_ignore + "\n"
    if previous.count(START) != previous.count(END) or previous.count(START) > 1:
        raise ValueError("Malformed native skill routing block; preserving AGENTS.md")
    block = f"""{START}
## Native Convex F#/.NET

This project uses the self-hosted native Convex fork. Start with
`.agents/skills/convex-native/SKILL.md` and select the relevant `convex-native-*`
skill. `.convex-native-release.json` records the exact artifact and release path;
set `CONVEX_FSHARP_RELEASE` to its `release` value when invoking the local CLI.
Use matching NuGet packages, native manifests and an explicit runtime. Preserve
first-party F# and application-owned contracts. The upstream Convex plugin's
TypeScript/Cloud scaffolding and publishing flows do not apply to this project.
Never commit `.convex/` private state or expose admin credentials to clients.
{END}"""
    if START in previous:
        begin, finish = previous.index(START), previous.index(END) + len(END)
        if finish <= begin:
            raise ValueError("Reversed native routing markers")
        updated = previous[:begin] + block + previous[finish:]
    else:
        updated = previous + ("\n\n" if previous and not previous.endswith("\n\n") else "") + block + "\n"
    # All conflicts are checked before the first project mutation. Copy a whole
    # skill at once; a completed partial installation is safely repeatable.
    destination.mkdir(parents=True, exist_ok=True)
    for source, target in planned:
        staging = Path(tempfile.mkdtemp(prefix=".convex-skill-", dir=destination))
        try:
            shutil.copytree(source, staging, dirs_exist_ok=True)
            if files(source) != files(staging):
                raise ValueError("Skill changed during installation")
            staging.rename(target)
        finally:
            if staging.exists():
                shutil.rmtree(staging)
    if not receipt_path.exists():
        write_json(receipt_path, receipt)
    if updated != previous:
        atomic_text(agents, updated)
    if not ignore_path.exists() or ignore_path.read_text() != ignored:
        atomic_text(ignore_path, ignored)
    print(f"Installed/verified {len(names)} native skills in {project}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", type=Path)
    parser.add_argument("--release", type=Path, help="Exact unpacked release directory")
    args = parser.parse_args()
    pack = Path(__file__).resolve().parent
    install(pack, args.project, args.release or pack / "release")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError) as error:
        raise SystemExit(str(error)) from error
