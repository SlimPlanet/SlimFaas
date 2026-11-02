#!/usr/bin/env python3
"""Combine native platform builds into one OCI archive without changing their blobs."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import shutil
import tarfile
import tempfile

from release_lib import checked_artifact_path, oci_identity, release_directory, sha256

OCI_INDEX = "application/vnd.oci.image.index.v1+json"


def collect_archive(path, blobs):
    with tarfile.open(path) as archive:
        index = json.load(archive.extractfile("index.json"))
        if len(index["manifests"]) != 1:
            raise ValueError("Each platform archive must have one root descriptor")
        for member in archive:
            if member.isdir() or member.name in {"index.json", "oci-layout"}:
                continue
            if not member.isfile() or not re.fullmatch(r"blobs/sha256/[a-f0-9]{64}", member.name):
                raise ValueError("Unexpected entry in OCI platform archive")
            destination = blobs / Path(member.name).name
            with archive.extractfile(member) as source, destination.open("wb") as target:
                shutil.copyfileobj(source, target)
            if sha256(destination) != destination.name:
                raise ValueError("OCI blob checksum mismatch")
        descriptor = index["manifests"][0]
        if not re.fullmatch(r"sha256:[a-f0-9]{64}", descriptor["digest"]):
            raise ValueError("Unexpected OCI root digest")
        root = json.loads((blobs / descriptor["digest"][7:]).read_text())
        return root.get("manifests", [descriptor])


def merge_archives(inputs, output):
    output = checked_artifact_path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="slimfaas-oci-") as temporary:
        blobs = Path(temporary) / "blobs"
        blobs.mkdir()
        descriptors = []
        for path in inputs:
            path = checked_artifact_path(path)
            oci_identity(path)  # Check the original manifest/config graph before combining it.
            descriptors.extend(collect_archive(path, blobs))
        manifest = json.dumps({"schemaVersion": 2, "mediaType": OCI_INDEX, "manifests": descriptors},
                              sort_keys=True, separators=(",", ":")).encode()
        digest = hashlib.sha256(manifest).hexdigest()
        (blobs / digest).write_bytes(manifest)
        index = {"schemaVersion": 2, "manifests": [{"mediaType": OCI_INDEX,
                 "digest": "sha256:" + digest, "size": len(manifest)}]}
        with tarfile.open(output, "w") as archive:
            for name, value in (("oci-layout", {"imageLayoutVersion": "1.0.0"}), ("index.json", index)):
                data = json.dumps(value, sort_keys=True).encode()
                member = tarfile.TarInfo(name)
                member.size = len(data)
                archive.addfile(member, io.BytesIO(data))
            for path in sorted(blobs.iterdir()):
                member = tarfile.TarInfo("blobs/sha256/" + path.name)
                member.size = path.stat().st_size
                with path.open("rb") as stream:
                    archive.addfile(member, stream)
    _, platforms = oci_identity(output)
    if sorted(item["platform"] for item in platforms) != ["linux/amd64", "linux/arm64"]:
        raise ValueError("Combined release image must contain exactly amd64 and arm64")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=release_directory, required=True)
    parser.add_argument("archives", nargs=2, type=release_directory)
    args = parser.parse_args()
    merge_archives(args.archives, args.output)
