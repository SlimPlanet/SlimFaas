#!/usr/bin/env python3
"""Install reviewed release tools, verifying the SHA-256 pinned in the repository."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import platform
import tarfile
import urllib.request


def install(names, destination):
    lock = json.loads(Path(__file__).with_name("release-tools.json").read_text())
    machine = {"x86_64": "amd64", "aarch64": "arm64", "arm64": "arm64"}[platform.machine()]
    system = f"{platform.system().lower()}-{machine}"
    destination.mkdir(parents=True, exist_ok=True)
    for name in names:
        entry = lock[name]["assets"][system]
        with urllib.request.urlopen(entry["url"], timeout=120) as response:
            data = response.read()
        if hashlib.sha256(data).hexdigest() != entry["sha256"]:
            raise ValueError(f"Checksum mismatch for {name}")
        if entry.get("member"):
            with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
                member = archive.getmember(entry["member"])
                if not member.isfile():
                    raise ValueError("Tool archive member must be a regular file")
                data = archive.extractfile(member).read()
        target = destination / name
        target.write_bytes(data)
        target.chmod(0o755)
        print(f"Installed {name} {lock[name]['version']} in {destination}")
    if os.environ.get("GITHUB_PATH"):
        with open(os.environ["GITHUB_PATH"], "a", encoding="utf-8") as stream:
            stream.write(str(destination.resolve()) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tools", nargs="+")
    parser.add_argument("--directory", type=Path, default=Path("artifacts/release-tools"))
    args = parser.parse_args()
    install(args.tools, args.directory)
