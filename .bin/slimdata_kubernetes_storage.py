"""Fresh-only bootstrap permission and replay-safe WAL setup for owned campaigns."""

import argparse
import json
import os
from pathlib import Path
import re


def sync_directory(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def write_new(path, content):
    with path.open("xb") as stream:
        stream.write(content)
        stream.flush()
        os.fsync(stream.fileno())
    sync_directory(path.parent)


def validate_campaign(namespace, source):
    if not re.fullmatch(r"slimfaas-pr506-[a-z0-9-]+", namespace) or len(namespace) > 63:
        raise ValueError("Storage setup requires an owned campaign namespace")
    if not re.fullmatch(r"[0-9a-f]{40}", source):
        raise ValueError("An exact source commit is required")


def prepare_control(control, namespace, source):
    """Call only after the coordinator proved namespace and VM run root absent."""
    validate_campaign(namespace, source)
    control = Path(control)
    if control.parent.name != namespace or control.parent.exists():
        raise ValueError("Control setup requires a proven-new namespace run root")
    control.mkdir(parents=True, exist_ok=False)
    # Podman's rootless kubelet must create its PV directories below this new
    # run root even when the coordinating VM command used sudo.
    control.parent.chmod(0o777)
    control.chmod(0o777)
    write_new(control / "fresh-campaign.json", (json.dumps({"namespace": namespace, "source": source}) + "\n").encode())
    sync_directory(control.parent)


def initialize_storage(root, control, node_name, chunk_bytes, namespace, source):
    validate_campaign(namespace, source)
    if not re.fullmatch(r"slimfaas-[0-2]", node_name) or chunk_bytes not in (4096, 8192, 16384, 32768, 65536):
        raise ValueError("Invalid campaign node or WAL geometry")
    root, control = Path(root), Path(control)
    if root.is_symlink() or control.is_symlink():
        raise ValueError("Campaign volume/control roots cannot be symlinks")
    expected = {"namespace": namespace, "source": source}
    if json.loads((control / "fresh-campaign.json").read_text()) != expected:
        raise ValueError("Foreign campaign control proof")
    consumed = control / "bootstrap-consumed.json"
    if consumed.exists() and json.loads(consumed.read_text()) != expected:
        raise ValueError("Foreign bootstrap consumption record")
    node, wal = root / node_name, root / node_name / "wal"
    if node.is_symlink() or wal.is_symlink():
        raise ValueError("Campaign WAL paths cannot be symlinks")
    marker = wal / "slimdata-chunk-size"
    existing = wal.exists()
    if node.exists() and not existing and any(node.iterdir()):
        raise ValueError("Existing node state has lost its WAL; preserve it for diagnosis")
    if existing:
        if not marker.is_file() or marker.is_symlink() or marker.read_bytes() != str(chunk_bytes).encode("ascii"):
            raise ValueError("Unknown or incompatible existing WAL geometry; refusing reinterpretation")
    else:
        wal.mkdir(parents=True)
        node.chmod(0o777)
        wal.chmod(0o777)
        write_new(marker, str(chunk_bytes).encode("ascii"))
        sync_directory(node)
        sync_directory(root)

    bootstrap = False
    if node_name == "slimfaas-0" and not consumed.exists():
        if existing or (root / ".profile-bootstrap-consumed").exists():
            raise ValueError("Bootstrap permission is missing for existing storage; refusing bootstrap")
        # The authoritative one-shot record is outside every data PV. Losing
        # pod zero's data volume can never recreate permission to bootstrap.
        write_new(consumed, (json.dumps(expected) + "\n").encode())
        write_new(root / ".profile-bootstrap-once", b"fresh-campaign-only")
        bootstrap = True
    return {"node": node_name, "chunk_bytes": chunk_bytes, "existing_wal": existing,
            "fresh_bootstrap_permission": bootstrap}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--control", type=Path, required=True)
    parser.add_argument("--node", required=True)
    parser.add_argument("--chunk-bytes", type=int, required=True)
    parser.add_argument("--namespace", required=True)
    parser.add_argument("--source", required=True)
    args = parser.parse_args()
    print(json.dumps(initialize_storage(args.root, args.control, args.node, args.chunk_bytes, args.namespace, args.source)))


if __name__ == "__main__":
    main()
