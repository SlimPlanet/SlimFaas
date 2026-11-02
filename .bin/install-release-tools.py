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
import urllib.parse
import zipfile

from release_lib import ROOT, release_directory


def download_url(value):
    url = urllib.parse.urlparse(value)
    parts = url.path.split("/")[1:]
    if (url.scheme != "https" or url.netloc != "github.com" or url.query or url.fragment
            or len(parts) != 6 or parts[2:4] != ["releases", "download"]
            or any(not part or part in (".", "..") or "%" in part or "\\" in part for part in parts)):
        raise ValueError("Release tools must be downloaded from an HTTPS GitHub release")
    # Encode each path component independently so neither traversal nor URL
    # delimiters from the reviewed lock file can change the download endpoint.
    return "https://github.com/" + "/".join(urllib.parse.quote(part, safe="") for part in parts)


def install(names, destination):
    lock = json.loads(Path(__file__).with_name("release-tools.json").read_text())
    machine = {"x86_64": "amd64", "aarch64": "arm64", "arm64": "arm64"}[platform.machine()]
    system = f"{platform.system().lower()}-{machine}"
    destination.mkdir(parents=True, exist_ok=True)
    for name in names:
        entry = lock[name]["assets"][system]
        with urllib.request.urlopen(download_url(entry["url"]), timeout=120) as response:
            data = response.read()
        if hashlib.sha256(data).hexdigest() != entry["sha256"]:
            raise ValueError(f"Checksum mismatch for {name}")
        if entry.get("member"):
            if entry.get("format") == "zip":
                with zipfile.ZipFile(io.BytesIO(data)) as archive:
                    member = archive.getinfo(entry["member"])
                    if member.is_dir() or (member.external_attr >> 16) & 0o170000 == 0o120000:
                        raise ValueError("Tool archive member must be a regular file")
                    data = archive.read(member)
            else:
                with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
                    member = archive.getmember(entry["member"])
                    if not member.isfile():
                        raise ValueError("Tool archive member must be a regular file")
                    data = archive.extractfile(member).read()
        target = destination / name
        if target.is_symlink():
            raise ValueError("Installed release tools cannot be symlinks")
        target.write_bytes(data)
        target.chmod(0o755)
        print(f"Installed {name} {lock[name]['version']} in {destination}")
    if os.environ.get("GITHUB_PATH"):
        with open(os.environ["GITHUB_PATH"], "a", encoding="utf-8") as stream:
            stream.write(str(destination.resolve()) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tools", nargs="+", choices=("actionlint", "syft", "cosign", "regctl", "gh"))
    parser.add_argument("--directory", type=release_directory, default=ROOT / "artifacts/release-tools")
    args = parser.parse_args()
    install(args.tools, args.directory)
