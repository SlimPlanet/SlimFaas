#!/usr/bin/env python3
"""Coordinate a release from immutable, verified artifacts. No build commands live here."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time
import urllib.parse

from release_lib import (NPM_PACKAGE, REPOSITORY, ROOT, SHA, TITLE, check_metadata, check_published_file, download,
                         emit, gh, make_inventory, next_version, python_version, run, sha256,
                         verify_inventory, version_parts)


def release_metadata(version):
    version_parts(version)
    return gh(f"repos/{REPOSITORY}/releases/tags/{version}")


def source_on_main(source_sha):
    if not SHA.fullmatch(source_sha):
        raise ValueError("A full source SHA is required")
    run("git", "merge-base", "--is-ancestor", source_sha, "origin/main")


def prepare(mode, tag):
    source_sha = os.environ["GITHUB_SHA"]
    if not SHA.fullmatch(source_sha):
        raise ValueError("Invalid workflow SHA")
    current = check_metadata()
    if mode != "validate":
        if os.environ.get("GITHUB_REPOSITORY") != REPOSITORY or os.environ.get("GITHUB_REF") != "refs/heads/main":
            raise ValueError("Publication is only allowed from this repository's main workflow")
        if os.environ.get("RELEASES_ENABLED") != "true":
            raise ValueError("Complete release setup, then set RELEASES_ENABLED=true")
        source_on_main(source_sha)
    if mode == "resume":
        metadata = release_metadata(tag)
        with tempfile.TemporaryDirectory() as temporary:
            run("gh", "release", "download", tag, "--repo", REPOSITORY, "--pattern", "release.json", "--dir", temporary)
            manifest = json.loads((Path(temporary) / "release.json").read_text())
        source_sha = manifest["source_sha"]
        source_on_main(source_sha)
        if manifest["version"] != tag or manifest["repository"] != REPOSITORY:
            raise ValueError("Resume manifest does not match the selected release")
        version = tag
        channel = version_parts(version)[3] or "stable"
        if channel == "dev":
            raise ValueError("Validation candidates cannot be published")
        check_tag(version, source_sha)
        emit(version=version, source_sha=source_sha, channel=channel, resume=True, published=not metadata["draft"])
        return
    if mode == "stable":
        if tag != current or version_parts(tag)[3]:
            raise ValueError("Release Please tag does not match committed version metadata")
        version, channel = tag, "stable"
        check_tag(version, source_sha)
    elif mode in {"alpha", "beta"}:
        messages = run("git", "log", "--format=%B%x00", f"{current}..{source_sha}", capture=True).split("\0")
        base = next_version(current, [message.strip() for message in messages if message.strip()])
        version = f"{base}-{mode}.{os.environ['GITHUB_RUN_NUMBER']}"
        channel = mode
    else:
        version = f"{current}-dev.{os.environ['GITHUB_RUN_NUMBER']}"
        channel = "validate"
    emit(version=version, source_sha=source_sha, channel=channel, resume=False, published=False)


def check_tag(version, source_sha):
    actual = run("git", "rev-parse", f"refs/tags/{version}^{{commit}}", capture=True)
    if actual != source_sha:
        raise ValueError("Release tag does not point to the build source")


def check_ci(source_sha):
    """Require CI on the source and external checks on the reviewed PR head."""
    source_on_main(source_sha)
    checks = gh(f"repos/{REPOSITORY}/commits/{source_sha}/check-runs?per_page=100")["check_runs"]
    required = next((check for check in sorted(checks, key=lambda x: x["id"], reverse=True)
                     if check["name"] == "CI / required" and check["app"]["slug"] == "github-actions"), None)
    if not required or required["conclusion"] != "success":
        raise ValueError("The source commit has no successful CI / required check")
    pulls = gh(f"repos/{REPOSITORY}/commits/{source_sha}/pulls")
    pulls = [pr for pr in pulls if pr.get("merged_at") and pr["base"]["ref"] == "main" and pr["merge_commit_sha"] == source_sha]
    if len(pulls) != 1:
        raise ValueError("Release source must be a reviewed PR merge on main")
    head = pulls[0]["head"]["sha"]
    checks = gh(f"repos/{REPOSITORY}/commits/{head}/check-runs?per_page=100")["check_runs"]
    statuses = gh(f"repos/{REPOSITORY}/commits/{head}/status?per_page=100")["statuses"]
    dco = next((check for check in sorted(checks, key=lambda x: x["id"], reverse=True)
                if check["name"] == "DCO" and check["app"]["slug"] == "dco"), None)
    if not dco or dco["conclusion"] != "success":
        raise ValueError("DCO has not passed on the merged PR")
    license_status = next((status for status in statuses if status["context"] == "License Compliance"), None)
    if not license_status or license_status["state"] != "success":
        raise ValueError("FOSSA License Compliance has not passed on the merged PR")


def verify(directory, version, source_sha, validation=False):
    manifest = verify_inventory(directory, version, source_sha, allow_validate=validation)
    ref = os.environ.get("GITHUB_REF", "refs/heads/main") if validation else "refs/heads/main"
    identity = rf"https://github\.com/SlimPlanet/SlimFaas/\.github/workflows/(main|release)\.yml@{re.escape(ref)}"
    run("cosign", "verify-blob", "--bundle", directory / "SHA256SUMS.sigstore.json",
        "--certificate-identity-regexp", identity, "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com",
        directory / "SHA256SUMS")
    run("gh", "attestation", "verify", directory / "SHA256SUMS", "--bundle", directory / "provenance.sigstore.jsonl",
        "--repo", REPOSITORY, "--signer-workflow", f"{REPOSITORY}/.github/workflows/release.yml",
        "--source-digest", source_sha, "--source-ref", ref, "--deny-self-hosted-runners")
    return manifest


def save_draft(directory, version, source_sha):
    """Never overwrite an asset. A rerun must match the bytes already staged."""
    source_on_main(source_sha)
    refs = gh(f"repos/{REPOSITORY}/git/matching-refs/tags/{version}")
    matching = [ref for ref in refs if ref["ref"] == "refs/tags/" + version]
    if matching:
        run("git", "fetch", "origin", "tag", version)
        check_tag(version, source_sha)
    else:
        gh(f"repos/{REPOSITORY}/git/refs", "POST", {"ref": "refs/tags/" + version, "sha": source_sha})
    releases = gh(f"repos/{REPOSITORY}/releases?per_page=100")
    metadata = next((release for release in releases if release["tag_name"] == version), None)
    if metadata is None:
        run("gh", "release", "create", version, "--repo", REPOSITORY, "--draft", "--target", source_sha,
            "--title", f"SlimFaas {version}", "--notes", f"Preview built from {source_sha}. See release.json for artifacts and provenance.")
        metadata = release_metadata(version)
    if not metadata["draft"]:
        raise ValueError("Cannot change a published release; use resume to verify its completion")
    existing = {asset["name"] for asset in metadata["assets"]}
    with tempfile.TemporaryDirectory() as temporary:
        for path in sorted(directory.iterdir()):
            if path.name in existing:
                run("gh", "release", "download", version, "--repo", REPOSITORY, "--pattern", path.name, "--dir", temporary)
                if sha256(path) != sha256(Path(temporary) / path.name):
                    raise ValueError(f"Draft asset collision: {path.name}. Resume with the original candidate.")
            else:
                run("gh", "release", "upload", version, path, "--repo", REPOSITORY)


def registry_files(directory, manifest, require_all=False):
    """Validate existing packages before either publishing or skipping them."""
    version = manifest["version"]
    encoded = urllib.parse.quote(NPM_PACKAGE, safe="")
    npm_data = download(f"https://registry.npmjs.org/{encoded}/{version}")
    npm_exists = False
    if npm_data:
        url = json.loads(npm_data)["dist"]["tarball"]
        if urllib.parse.urlparse(url).hostname != "registry.npmjs.org":
            raise ValueError("Unexpected npm tarball host")
        npm_exists = check_published_file(directory / f"slimfaas-planet-saver-{version}.tgz", download(url))
    nuget_url = f"https://api.nuget.org/v3-flatcontainer/slimfaasclient/{version}/slimfaasclient.{version}.nupkg"
    nuget_exists = check_published_file(directory / f"SlimFaasClient.{version}.nupkg", download(nuget_url), nuget=True)
    py = python_version(version)
    pypi_data = download(f"https://pypi.org/pypi/slimfaas-client/{py}/json")
    existing = {entry["filename"]: entry["digests"]["sha256"] for entry in json.loads(pypi_data)["urls"]} if pypi_data else {}
    missing_python = []
    expected_python = [f"slimfaas_client-{py}-py3-none-any.whl", f"slimfaas_client-{py}.tar.gz"]
    if set(existing) - set(expected_python):
        raise ValueError("Unexpected files already published for this Python version")
    for name in expected_python:
        if name in existing:
            if existing[name] != sha256(directory / name):
                raise ValueError(f"PyPI version collision: {name}")
        else:
            missing_python.append(name)
    if require_all and (not npm_exists or not nuget_exists or missing_python):
        raise ValueError("Not all packages are visible in their registries")
    return npm_exists, nuget_exists, missing_python


def image_digest(reference):
    result = subprocess.run(["regctl", "image", "digest", reference], text=True, capture_output=True, check=False)
    if result.returncode == 0:
        return result.stdout.strip()
    # Authentication failures and transient errors must never be treated as absence.
    if "manifest unknown" in result.stderr.lower() or "not found" in result.stderr.lower() and "404" in result.stderr:
        return None
    raise RuntimeError(result.stderr)


def check_images(manifest):
    existing_images = {}
    for image in manifest["images"]:
        reference = f"{image['name']}:{manifest['version']}"
        existing = image_digest(reference)
        if existing is not None and existing != image["digest"]:
            raise ValueError(f"Registry image collision: {reference}")
        existing_images[reference] = existing
    return existing_images


def verify_image_signature(image):
    run("cosign", "verify", "--certificate-identity-regexp",
        r"https://github\.com/SlimPlanet/SlimFaas/\.github/workflows/(main|release)\.yml@refs/heads/main",
        "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com",
        f"{image['name']}@{image['digest']}")


def publish_images(directory, manifest):
    existing_images = check_images(manifest)
    for image in manifest["images"]:
        reference = f"{image['name']}:{manifest['version']}"
        existing = existing_images[reference]
        if existing is None:
            run("regctl", "image", "import", reference, directory / image["file"])
        if image_digest(reference) != image["digest"]:
            raise ValueError(f"Published OCI digest mismatch: {reference}")
        immutable = f"{image['name']}@{image['digest']}"
        run("cosign", "sign", "--yes", immutable)
        verify_image_signature(image)


def finalize(directory, manifest):
    # Account for registry indexing delay, bounded to five minutes. No writes retried here.
    deadline = time.monotonic() + 300
    while True:
        try:
            registry_files(directory, manifest, require_all=True)
            break
        except ValueError as error:
            if str(error) != "Not all packages are visible in their registries" or time.monotonic() >= deadline:
                raise
            time.sleep(10)
    for image in manifest["images"]:
        if image_digest(f"{image['name']}:{manifest['version']}") != image["digest"]:
            raise ValueError("Published image does not match the release manifest")
        verify_image_signature(image)
    channel = manifest["channel"]
    destination = "latest" if channel == "stable" else channel
    if channel == "stable":
        latest = gh(f"repos/{REPOSITORY}/releases/latest")
        if version_parts(latest["tag_name"].removeprefix("v"))[:3] > version_parts(manifest["version"])[:3]:
            raise ValueError("Refusing to move latest backwards; this release has been superseded")
    # npm >=11.21 supports OIDC for dist-tag operations as well as publish.
    run("npm", "dist-tag", "add", f"{NPM_PACKAGE}@{manifest['version']}", destination)
    tags = json.loads(run("npm", "view", NPM_PACKAGE, "dist-tags", "--json", capture=True))
    if tags.get(destination) != manifest["version"]:
        raise ValueError("npm channel promotion failed")
    for image in manifest["images"]:
        run("regctl", "image", "copy", f"{image['name']}@{image['digest']}", f"{image['name']}:{destination}")
        if image_digest(f"{image['name']}:{destination}") != image["digest"]:
            raise ValueError("Image channel promotion failed")
    metadata = release_metadata(manifest["version"])
    if metadata["draft"]:
        run("gh", "release", "edit", manifest["version"], "--repo", REPOSITORY, "--draft=false",
            f"--prerelease={'false' if channel == 'stable' else 'true'}", f"--latest={'true' if channel == 'stable' else 'false'}")


def resume_run(manifest):
    # Rerun the original jobs so OIDC provenance still names the original source SHA.
    # A new workflow checking out an old commit would have a different OIDC source digest.
    run_id = str(manifest["run_id"])
    if not run_id.isdigit():
        raise ValueError("Invalid producer run ID")
    original = gh(f"repos/{REPOSITORY}/actions/runs/{run_id}")
    if original["head_sha"] != manifest["source_sha"] or original["head_repository"]["full_name"] != REPOSITORY:
        raise ValueError("Original workflow does not match the signed release source")
    if original["path"] not in {".github/workflows/main.yml", ".github/workflows/release.yml"}:
        raise ValueError("Unexpected producer workflow")
    if original["status"] != "completed":
        raise ValueError("Original release is still running")
    if original["conclusion"] == "success":
        print("Original release run already succeeded; no jobs rerun.")
        return
    run("gh", "api", "--method", "POST", f"repos/{REPOSITORY}/actions/runs/{run_id}/rerun-failed-jobs")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("prepare", "inventory", "verify", "save-draft", "check-ci", "registry-status", "publish-images", "finalize", "resume-run", "check-title"))
    parser.add_argument("--mode", choices=("stable", "validate", "alpha", "beta", "resume"), default="validate")
    parser.add_argument("--tag", default="")
    parser.add_argument("--version", default=os.environ.get("RELEASE_VERSION"))
    parser.add_argument("--sha", default=os.environ.get("RELEASE_SHA"))
    parser.add_argument("--channel", default=os.environ.get("RELEASE_CHANNEL"))
    parser.add_argument("--directory", type=Path, default=ROOT / "artifacts/release")
    args = parser.parse_args()
    if args.command == "prepare":
        prepare(args.mode, args.tag)
    elif args.command == "check-title":
        check_metadata()
        event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
        if "pull_request" in event:
            pr = event["pull_request"]
            if not TITLE.fullmatch(pr["title"]):
                raise ValueError("PR title must be a Conventional Commit, e.g. fix(ci): validate releases")
            if re.search(r"(?im)^Release-As:", pr.get("body") or ""):
                raise ValueError("Release-As overrides are unsupported")
    elif args.command == "check-ci":
        check_ci(args.sha)
    elif args.command == "inventory":
        make_inventory(args.directory, args.version, args.sha, args.channel)
    else:
        manifest = verify(args.directory, args.version, args.sha, validation=args.command == "verify" and args.channel == "validate")
        if args.command == "save-draft":
            save_draft(args.directory, args.version, args.sha)
        elif args.command == "registry-status":
            npm, nuget, missing = registry_files(args.directory, manifest)
            pending = ROOT / "artifacts/pypi-pending"
            pending.mkdir(parents=True, exist_ok=True)
            for name in missing:
                shutil.copy2(args.directory / name, pending / name)
            emit(npm_exists=npm, nuget_exists=nuget, pypi_pending=bool(missing))
        elif args.command == "publish-images":
            publish_images(args.directory, manifest)
        elif args.command == "finalize":
            finalize(args.directory, manifest)
        elif args.command == "resume-run":
            resume_run(manifest)


if __name__ == "__main__":
    main()
