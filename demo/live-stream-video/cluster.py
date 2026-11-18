"""Install the recording workloads into the dedicated kind cluster."""
import argparse
import json
from pathlib import Path
import subprocess

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORK = ROOT / "artifacts/live-stream-video"
KUBE = ["kubectl", "--kubeconfig", str(WORK / "kubeconfig"), "--context", "kind-slimfaas-film"]
NAMESPACE = "slimfaas-demo"


def manifests(name):
    return yaml.safe_load_all((ROOT / "demo" / name).read_text(encoding="utf-8-sig"))


def install(stage):
    resources = []
    if stage == "start":
        resources.extend(manifests("service-account-slimfaas.yml"))
        for resource in manifests("deployment-slimfaas.yml"):
            if resource["kind"] in ("ConfigMap", "Namespace"):
                continue
            if resource["kind"] == "StatefulSet":
                container = resource["spec"]["template"]["spec"]["containers"][0]
                container.update(image="localhost/slimfaas-film:record", imagePullPolicy="Never")
                env = container["env"]
                env[:] = [v for v in env if "valueFrom" not in v]
                for item in env:
                    if item["name"].startswith("Logging__"):
                        item["value"] = "Information"
                env.extend([{"name": "SlimFaas__StatusStream__LiveEventSamplingRatio", "value": "1"},
                            {"name": "SlimFaas__StatusStream__MaxLiveEventsPerSecond", "value": "500"}])
                container["resources"] = {"requests": {"cpu": "100m", "memory": "192Mi"}}
            resources.append(resource)
    names = {"fibonacci1"} if stage == "start" else {"fibonacci3", "fibonacci4"}
    for resource in manifests("deployment-functions.yml"):
        name = resource["metadata"]["name"]
        if name not in names:
            continue
        if resource["kind"] == "Deployment":
            spec = resource["spec"]
            spec["replicas"] = 0 if stage == "start" else 2
            pod = spec["template"]
            annotations = pod["metadata"]["annotations"]
            container = pod["spec"]["containers"][0]
            container.update(image="localhost/fibonacci-film:record", imagePullPolicy="Never")
            container["resources"] = {"requests": {"cpu": "150m", "memory": "64Mi"}}
            # Suppress debug detail during the CPU-bound burst.
            container["env"] = [{"name": "ASPNETCORE_URLS", "value": "http://+:5000"}]
            if stage == "start":
                del annotations["SlimFaas/SubscribeStream"]
                annotations["SlimFaas/NumberParallelRequest"] = "4"
                annotations["SlimFaas/TimeoutSecondBeforeSetReplicasMin"] = "20"
                annotations["SlimFaas/Configuration"] = json.dumps({"DefaultAsync": {
                    "HttpTimeout": 300, "TimeoutRetries": [], "HttpStatusRetries": []}})
                policy = json.loads(annotations["SlimFaas/Scale"])
                policy["ReplicaMax"] = 4
                policy["Triggers"][0].update(MetricType="AverageValue", Threshold=5)
                policy["Behavior"]["ScaleUp"]["Policies"][0].update(Value=3, PeriodSeconds=2)
                policy["Behavior"]["ScaleDown"]["StabilizationWindowSeconds"] = 30
                annotations["SlimFaas/Scale"] = json.dumps(policy)
            else:
                annotations.update({"SlimFaas/SubscribeEvents": "Public:tool-catalog",
                                    "SlimFaas/SubscribeStream": "Public:agent-output",
                                    "SlimFaas/ReplicasMin": "2", "SlimFaas/DefaultVisibility": "Public"})
                role = "evaluator" if name == "fibonacci3" else "audit"
                container["env"].append({"name": "AgentTourRole", "value": role})
        resources.append(resource)
    subprocess.run([*KUBE, "apply", "-f", "-"], input=yaml.safe_dump_all(resources),
                   text=True, check=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stage", choices=["start", "subscribers"])
    install(parser.parse_args().stage)
