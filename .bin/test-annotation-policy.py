#!/usr/bin/env python3
"""Smoke-test demo/slimfaas-annotation-policy.yml on a disposable kind cluster."""

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
NAMESPACE = "slimfaas-demo"
CONTEXT = None
SLIMFAAS = f"system:serviceaccount:{NAMESPACE}:slimfaas"

FUNCTION = {
    "apiVersion": "apps/v1",
    "kind": "Deployment",
    "metadata": {"name": "payments", "namespace": NAMESPACE, "annotations": {"team": "billing"}},
    "spec": {
        "replicas": 0,
        "selector": {"matchLabels": {"app": "payments"}},
        "template": {
            "metadata": {
                "labels": {"app": "payments"},
                "annotations": {"SlimFaas/Function": "true", "SlimFaas/DefaultVisibility": "Private"},
            },
            "spec": {"containers": [{"name": "payments", "image": "registry.k8s.io/pause:3.10"}]},
        },
    },
}


# Namespace editors: RBAC lets them change workloads, the policy guards SlimFaas/* annotations.
EDITOR_ROLE = {
    "apiVersion": "rbac.authorization.k8s.io/v1",
    "kind": "Role",
    "metadata": {"name": "workload-editor", "namespace": NAMESPACE},
    "rules": [
        {"apiGroups": ["apps"], "resources": ["deployments", "statefulsets", "deployments/scale"],
         "verbs": ["get", "create", "update", "patch"]},
        {"apiGroups": ["batch"], "resources": ["jobs", "cronjobs"], "verbs": ["get", "create", "update", "patch"]},
    ],
}


def job(kind, annotations):
    template = {"spec": {"restartPolicy": "Never", "containers": [{"name": "job", "image": "registry.k8s.io/pause:3.10"}]}}
    spec = {"template": template}
    if kind == "CronJob":
        spec = {"schedule": "0 0 * * *", "suspend": True, "jobTemplate": {"spec": spec}}
    return {
        "apiVersion": "batch/v1",
        "kind": kind,
        "metadata": {"name": "fibonacci5", "namespace": NAMESPACE, "annotations": annotations},
        "spec": spec,
    }


def kubectl(*args, user=None, groups=(), stdin=None, check=True):
    command = ["kubectl", "--context", CONTEXT, *args]
    if user:
        command += ["--as", user, *[f"--as-group={group}" for group in groups]]
    result = subprocess.run(command, input=stdin, capture_output=True, text=True)
    if check and result.returncode != 0:
        raise RuntimeError(f"{' '.join(command)} failed: {result.stderr.strip()}")
    return result


def patch(annotations, path="template", user=None, groups=()):
    body = {"metadata": {"annotations": annotations}}
    if path == "template":
        body = {"spec": {"template": body}}
    return kubectl("-n", NAMESPACE, "patch", "deployment", "payments", "--type=merge",
                   "--dry-run=server", "-p", json.dumps(body), user=user, groups=groups, check=False)


def denied_by_policy(result):
    return result.returncode != 0 and "slimfaas-annotations" in result.stderr


def create(manifest, user=None, groups=()):
    return kubectl("create", "--dry-run=server", "-f", "-", stdin=json.dumps(manifest),
                   user=user, groups=groups, check=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cluster", default="slimfaas-annotation-policy")
    parser.add_argument("--image", help="kind node image (Kubernetes 1.30 or later)")
    parser.add_argument("--timeout", type=float, default=60)
    parser.add_argument("--keep-cluster", action="store_true")
    args = parser.parse_args()

    global CONTEXT
    CONTEXT = f"kind-{args.cluster}"
    existing = subprocess.run(["kind", "get", "clusters"], capture_output=True, text=True, check=True)
    if args.cluster in existing.stdout.split():
        sys.exit(f"kind cluster {args.cluster} already exists; choose another --cluster name")

    created = False
    try:
        create_cluster = ["kind", "create", "cluster", "--name", args.cluster, "--wait", "120s"]
        if args.image:
            create_cluster += ["--image", args.image]
        # Set before running: a failed creation can leave a partial cluster that this run owns.
        created = True
        subprocess.run(create_cluster, check=True)
        kubectl("apply", "-f", str(ROOT / "demo/service-account-slimfaas.yml"))
        kubectl("apply", "-f", "-", stdin=json.dumps(EDITOR_ROLE))
        kubectl("-n", NAMESPACE, "create", "rolebinding", "workload-editors", "--role=workload-editor",
                "--user=alice", "--user=carol")
        kubectl("apply", "-f", "-", stdin=json.dumps(FUNCTION))
        kubectl("apply", "-f", str(ROOT / "demo/slimfaas-annotation-policy.yml"))

        # A new policy takes a few seconds to reach the admission chain.
        deadline = time.monotonic() + args.timeout
        while not denied_by_policy(patch({"SlimFaas/DefaultVisibility": "Public"}, user="alice")):
            if time.monotonic() > deadline:
                raise TimeoutError("the policy was not enforced in time")
            time.sleep(1)
        print("Policy enforced", flush=True)

        cases = [
            ("alice makes a Private function Public", False,
             patch({"SlimFaas/DefaultVisibility": "Public"}, user="alice")),
            ("alice removes SlimFaas/Function", False, patch({"SlimFaas/Function": None}, user="alice")),
            ("alice adds a SlimFaas/ annotation to the object metadata", False,
             patch({"SlimFaas/DefaultTrust": "Trusted"}, path="metadata", user="alice")),
            ("the cluster administrator, who is not listed, changes SlimFaas/Scale", False,
             patch({"SlimFaas/Scale": "{}"})),
            ("alice creates a SlimFaas job configuration", False,
             create(job("CronJob", {"SlimFaas/Job": "true"}), user="alice")),
            ("alice restarts the function", True,
             patch({"kubectl.kubernetes.io/restartedAt": "2026-01-01T00:00:00Z"}, user="alice")),
            ("alice changes another annotation", True, patch({"team": "payments"}, path="metadata", user="alice")),
            ("alice scales the function", True,
             kubectl("-n", NAMESPACE, "scale", "deployment", "payments", "--replicas=1", "--dry-run=server",
                     user="alice", check=False)),
            ("alice creates a workload without SlimFaas/ annotations", True,
             create(job("Job", {"team": "billing"}), user="alice")),
            ("carol, in group slimfaas-operators, makes the function Public", True,
             patch({"SlimFaas/DefaultVisibility": "Public"}, user="carol", groups=["slimfaas-operators"])),
            ("the SlimFaas service account creates a Job with SlimFaas/DependsOn", True,
             create(job("Job", {"SlimFaas/DependsOn": "fibonacci1"}), user=SLIMFAAS)),
        ]
        failures = 0
        for description, allowed, result in cases:
            passed = result.returncode == 0 if allowed else denied_by_policy(result)
            failures += not passed
            outcome = "allowed" if result.returncode == 0 else result.stderr.strip()
            print(f"{'PASS' if passed else 'FAIL'}: {description}: {outcome}", flush=True)
        if failures:
            sys.exit(f"{failures} case(s) failed")
    finally:
        if created and not args.keep_cluster:
            subprocess.run(["kind", "delete", "cluster", "--name", args.cluster], check=False)


if __name__ == "__main__":
    main()
