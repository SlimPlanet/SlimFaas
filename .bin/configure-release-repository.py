#!/usr/bin/env python3
"""Inspect release activation settings, or apply them after a reviewed migration."""
import argparse
import json

from release_lib import REPOSITORY, gh, run


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pr", type=int, required=True, help="Reviewed migration PR number")
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    base = f"repos/{REPOSITORY}"
    pull = gh(f"{base}/pulls/{args.pr}")
    head = pull["head"]["sha"]
    checks = gh(f"{base}/commits/{head}/check-runs?per_page=100")["check_runs"]
    statuses = gh(f"{base}/commits/{head}/status?per_page=100")["statuses"]
    observed = {}
    for check in sorted(checks, key=lambda item: item["id"], reverse=True):
        observed.setdefault(check["name"], check)
    required = []
    for name, slug in (("CI / required", "github-actions"), ("DCO", "dco")):
        check = observed.get(name)
        if check and check["app"]["slug"] == slug:
            required.append({"context": name, "app_id": check["app"]["id"]})
        elif args.apply:
            raise ValueError(f"Required check has not been observed from {slug}: {name}")
    fossa = next((status for status in statuses if status["context"] == "License Compliance"), None)
    # FOSSA currently reports a legacy commit status, without an app identity in
    # the status response. Omitting app_id lets GitHub bind its recent producer.
    required.append({"context": "License Compliance"})
    protection = {
        "required_status_checks": {"strict": True, "checks": required},
        "enforce_admins": True,
        "required_pull_request_reviews": {"dismiss_stale_reviews": True,
            "require_code_owner_reviews": True, "required_approving_review_count": 1},
        "restrictions": None, "required_conversation_resolution": True,
        "allow_force_pushes": False, "allow_deletions": False,
    }
    print(json.dumps({"repository": REPOSITORY, "migration_pr": args.pr,
        "migration_merged": bool(pull["merged_at"]), "main_protection": protection,
        "environments_main_only": ["release", "npm", "nuget", "pypi", "docker"],
        "immutable_releases": True, "publication_enabled_by_this_script": False}, indent=2))
    if not args.apply:
        return
    if not pull["merged_at"] or pull["base"]["ref"] != "main":
        raise ValueError("Apply only after the reviewed migration PR is merged into main")
    if not fossa or fossa["state"] != "success" or any(observed[name]["conclusion"] != "success" for name in ("CI / required", "DCO")):
        raise ValueError("Migration CI, DCO and FOSSA must pass before activation")
    # Ensure main actually contains this pipeline; never lock the old publisher.
    gh(f"{base}/contents/.github/workflows/release.yml?ref=main")
    active = gh(f"{base}/actions/runs?status=in_progress&per_page=100")["workflow_runs"]
    if any(item["head_branch"] == "main" for item in active):
        raise ValueError("Wait for existing main workflow runs to finish before activation")
    gh(f"{base}/branches/main/protection", "PUT", protection)
    gh(base, "PATCH", {"allow_squash_merge": True, "allow_merge_commit": False,
        "allow_rebase_merge": False, "squash_merge_commit_title": "PR_TITLE",
        "squash_merge_commit_message": "COMMIT_MESSAGES"})
    for environment in ("release", "npm", "nuget", "pypi", "docker"):
        endpoint = f"{base}/environments/{environment}"
        gh(endpoint, "PUT", {"deployment_branch_policy": {"protected_branches": False, "custom_branch_policies": True}})
        policies = gh(endpoint + "/deployment-branch-policies")["branch_policies"]
        # Preserve existing reviewers and fail on broader policies instead of
        # silently deleting an administrator's settings.
        if any(item["name"] != "main" or item.get("type", "branch") != "branch" for item in policies):
            raise ValueError(f"Remove non-main deployment policies from {environment}, then rerun")
        if not policies:
            gh(endpoint + "/deployment-branch-policies", "POST", {"name": "main", "type": "branch"})
    run("gh", "api", "--method", "PUT", base + "/immutable-releases")
    print("GitHub protections configured. Complete App/OIDC setup and validate before enabling publication.")


if __name__ == "__main__":
    main()
