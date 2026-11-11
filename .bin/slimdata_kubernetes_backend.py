"""Kubernetes lifecycle adapter for the existing, unchanged ledger invariants.

The controller runs inside its dedicated namespace. Only resources bearing its
campaign label are controlled; Kubernetes service-account credentials stay local.
"""

import json
import os
from pathlib import Path
import re
import ssl
import time
from urllib.parse import quote
from urllib.request import build_opener, HTTPSHandler, ProxyHandler, Request


def create_campaign(options, local):
    config = json.loads(options.backend_config.read_text())
    namespace = config["namespace"]
    identifier = config["campaign_id"]
    if not re.fullmatch(r"slimfaas-pr506-[a-z0-9-]+", namespace) or identifier != namespace:
        raise ValueError("Kubernetes campaigns require their own labelled slimfaas-pr506-* namespace")
    if len(config["nodes"]) != 3:
        raise ValueError("Exactly three Kubernetes runtime nodes are required")

    class KubernetesCampaign(local["Campaign"]):
        def generate_manifest(self):
            self.manifest = self.output / "manifest.yaml"
            self.manifest.write_text(json.dumps(config, indent=2))

        def __init__(self):
            super().__init__(options)
            local["ENDPOINTS"].update({self.entry: config["entry"]})
            for index, node in enumerate(config["nodes"]):
                local["ENDPOINTS"].update({self.ports[index]: node["http"], self.raft + index: node["raft"]})
                local["WEBSOCKET_ENDPOINTS"][self.ports[index]] = node["websocket"]
            self.state_root = Path(config["state_root"])
            self.receipts = Path(config["receipts_root"])
            self.api_url = "https://" + os.environ["KUBERNETES_SERVICE_HOST"] + ":" + os.environ["KUBERNETES_SERVICE_PORT"]
            account = Path("/var/run/secrets/kubernetes.io/serviceaccount")
            if account.joinpath("namespace").read_text().strip() != namespace:
                raise ValueError("Controller service account belongs to another namespace")
            self.token = account.joinpath("token").read_text().strip()
            self.api_http = build_opener(ProxyHandler({}), HTTPSHandler(context=ssl.create_default_context(
                cafile=str(account / "ca.crt"))))

        def api(self, method, resource, body=None, raw=False):
            # resource is assembled exclusively from fixed, adapter-owned paths.
            request = Request(self.api_url + resource,
                data=json.dumps(body).encode() if body is not None else None,
                headers={"Authorization": "Bearer " + self.token,
                         "Content-Type": "application/merge-patch+json" if method == "PATCH" else "application/json"},
                method=method)
            with self.api_http.open(request, timeout=15) as response:
                return response.read() if raw else json.load(response)

        def resource(self, suffix, apps=False):
            return ("/apis/apps/v1" if apps else "/api/v1") + "/namespaces/" + namespace + "/" + suffix

        def owned(self, resource):
            metadata = resource["metadata"]
            if metadata["namespace"] != namespace or metadata.get("labels", {}).get("slimfaas-campaign") != identifier:
                raise RuntimeError("Refusing to control an unowned Kubernetes resource")
            return resource

        def scale(self, replicas):
            path = self.resource("statefulsets/slimfaas", apps=True)
            self.owned(self.api("GET", path))
            self.api("PATCH", path + "/scale", {"spec": {"replicas": replicas}})

        def start(self):
            self.scale(3)
            self.generation += 1
            self.ready()
            for index in range(3):
                local["wait_until"]("owned runtime process identity", lambda index=index: self.node_pid(index),
                                    self.options.recovery_timeout)

        def stop(self):
            while self.suspended:
                handle = next(iter(self.suspended))
                self.pause(handle, False)
                self.suspended.remove(handle)
            self.capture_pod_logs(f"before-stop-{self.generation}")
            self.scale(0)
            selector = quote("app=slimfaas,slimfaas-campaign=" + identifier, safe="")
            local["wait_until"]("owned StatefulSet pods terminate", lambda:
                not self.api("GET", self.resource("pods?labelSelector=" + selector))["items"],
                self.options.recovery_timeout)

        def leader(self, excluded=()):
            for index in range(3):
                if index in excluded:
                    continue
                try:
                    status, headers, body = local["request"](self.raft + index, "/SlimData/leader", timeout=.5, redirects=False)
                    address = body if status == 200 else headers.get("Location", "").encode()
                    match = re.search(rb"slimfaas-([0-2])(?:\.|:)", address)
                    if match and int(match[1]) not in excluded:
                        return int(match[1])
                except local["NETWORK_ERRORS"]:
                    pass
            return None

        def control(self, index, action=None, identity=None):
            node = config["nodes"][index]
            body = None if action is None else json.dumps({"action": action, "identity": identity}).encode()
            request = Request(node["control"] + ("/status" if action is None else "/control"), data=body,
                              headers={"Content-Type": "application/json"}, method="GET" if action is None else "POST")
            with local["HTTP"].open(request, timeout=5) as response:
                return json.load(response)

        def node_pid(self, node):
            pod = self.owned(self.api("GET", self.resource(f"pods/slimfaas-{node}")))
            identity = self.control(node)
            if identity["pod_uid"] != pod["metadata"]["uid"]:
                raise RuntimeError("Runtime controller identity does not match the owned pod UID")
            return json.dumps({"node": node, **identity}, sort_keys=True)

        def signal(self, handle, action):
            identity = json.loads(handle)
            node = identity.pop("node")
            self.control(node, action, identity)

        def pause(self, handle, paused):
            self.signal(handle, "pause" if paused else "resume")

        def kill_node(self, handle):
            self.signal(handle, "kill")

        def node_state_directory(self, node):
            return Path(config["node_state_roots"][node])

        def capture_pod_logs(self, name):
            for pod in self.api("GET", self.resource("pods"))["items"]:
                if pod["metadata"].get("labels", {}).get("slimfaas-campaign") != identifier:
                    continue
                self.owned(pod)
                pod_name = pod["metadata"]["name"]
                uid = pod["metadata"]["uid"]
                for container in pod["spec"]["containers"]:
                    container_name = container["name"]
                    path = self.output / f"{name}-{pod_name}-{uid}-{container_name}.log"
                    try:
                        path.write_bytes(self.api("GET", self.resource(
                            f"pods/{pod_name}/log?container={container_name}&timestamps=true"), raw=True))
                    except OSError as error:
                        path.with_suffix(".error.txt").write_text(repr(error))

        def capture(self, name):
            super().capture(name)
            self.capture_pod_logs(name)
            (self.output / f"{name}-runtime-identities.json").write_text(json.dumps(
                [self.control(index) for index in range(3)], indent=2))
            for kind in ("pods", "events"):
                (self.output / f"{name}-kubernetes-{kind}.json").write_text(
                    json.dumps(self.api("GET", self.resource(kind)), indent=2))
            self.client.append({"event": "kubernetes-capture", "phase": name, "utc": time.time()})

    return KubernetesCampaign()
