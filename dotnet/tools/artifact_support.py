"""Small shared inventory helpers for native release delivery (Python 3.10+)."""

import hashlib
import json
from pathlib import Path, PurePosixPath


def no_links(path):
    path = Path(path).absolute()
    for item in (path, *path.parents):
        if item.is_symlink():
            raise ValueError(f"Symbolic links are not accepted: {item}")
    return path


def digest(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def read_json(path):
    path = no_links(path)
    if path.stat().st_size > 4 * 1024 * 1024:
        raise ValueError(f"Oversized metadata: {path}")
    return json.loads(path.read_text())


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + "\n")


def files(root):
    root = no_links(root)
    result = {}
    for path in sorted(root.rglob("*")):
        no_links(path)
        if path.is_dir():
            continue
        if not path.is_file():
            raise ValueError(f"Non-regular artifact: {path}")
        result[path.relative_to(root).as_posix()] = {
            "sha256": digest(path), "bytes": path.stat().st_size
        }
    return result


def verify(root, descriptor):
    root = no_links(root)
    data = read_json(root / descriptor)
    expected = {}
    for item in data["files"]:
        name = item["path"]
        parts = PurePosixPath(name).parts
        if (not parts or name.startswith("/") or "\\" in name
                or any(p in ("", ".", "..") for p in name.split("/"))
                or name in expected or name == descriptor):
            raise ValueError("Invalid or duplicate artifact inventory path")
        expected[name] = item
    actual = files(root)
    actual.pop(descriptor, None)
    if actual.keys() != expected.keys():
        raise ValueError("Artifact inventory contains missing or unlisted files")
    for name, item in actual.items():
        if item["sha256"] != expected[name]["sha256"]:
            raise ValueError(f"Artifact digest mismatch: {name}")
        if "bytes" in expected[name] and item["bytes"] != expected[name]["bytes"]:
            raise ValueError(f"Artifact size mismatch: {name}")
    return data
