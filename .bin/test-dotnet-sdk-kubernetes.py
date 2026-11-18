#!/usr/bin/env python3
"""Run the actual .NET SDK payload probe inside an existing, owned kind campaign."""

import argparse
import json
from pathlib import Path
import re
import subprocess
import time
import textwrap

ROOT = Path(__file__).resolve().parents[1]


def probe_resources(namespace, image, worker_image):
    NS = namespace
    IMAGE = image
    WORKER = worker_image
    BASE = "/evidence/runs/" + namespace + "/dotnet-sdk"
    entry = (
        textwrap.dedent(r"""
    import json,os,ssl,subprocess,threading,time
    from pathlib import Path
    from urllib.request import build_opener,HTTPSHandler,ProxyHandler,Request
    root=Path('BASE');root.mkdir(parents=True,exist_ok=False)
    account=Path('/var/run/secrets/kubernetes.io/serviceaccount')
    namespace=account.joinpath('namespace').read_text().strip()
    assert namespace=='NAMESPACE'
    base='https://'+os.environ['KUBERNETES_SERVICE_HOST']+':'+os.environ['KUBERNETES_SERVICE_PORT']+'/api/v1/namespaces/'+namespace
    http=build_opener(ProxyHandler({}),HTTPSHandler(context=ssl.create_default_context(cafile=str(account/'ca.crt'))))
    headers={'Authorization':'Bearer '+account.joinpath('token').read_text().strip()}
    logfile=root/'worker.log';logfile.touch()
    errors=[]
    def stream():
     try:
      deadline=time.monotonic()+120
      while True:
       with http.open(Request(base+'/pods?labelSelector=app%3Ddotnet-sdk-worker',headers=headers),timeout=10) as response:pods=json.load(response)['items']
       if len(pods)==1 and pods[0]['status']['phase']=='Running':break
       if time.monotonic()>deadline:raise AssertionError('No owned .NET worker pod')
       time.sleep(.2)
      pod=pods[0]
      assert pod['metadata']['labels']['slimfaas-campaign']==namespace
      (root/'worker-pod.json').write_text(json.dumps(pod,indent=2))
      with http.open(Request(base+'/pods/'+pod['metadata']['name']+'/log?follow=true',headers=headers),timeout=300) as response,logfile.open('ab',buffering=0) as output:
       while data:=response.read1(65536):output.write(data)
     except BaseException as error:errors.append(repr(error));(root/'log-error.txt').write_text(repr(error))
    thread=threading.Thread(target=stream,daemon=True);thread.start()
    code=subprocess.call(['python3','/probe/probe.py','--function','dotnet-kubernetes-pr506','--worker-log',str(logfile),'--output',str(root/'probe-output'),'--base-url','http://slimfaas-http:5000','--node-urls',','.join('http://slimfaas-'+str(index)+'.slimfaas:5000' for index in range(3))])
    if errors:raise AssertionError(errors)
    raise SystemExit(code)
    """)
        .replace("BASE", BASE)
        .replace("NAMESPACE", NS)
    )
    WAIT_SERVICE = "import time\nfrom urllib.request import urlopen\nend=time.monotonic()+120\nwhile True:\n try:\n  with urlopen('http://slimfaas-http:5000/ready',timeout=2)as response:\n   if response.status==200:break\n except OSError:pass\n if time.monotonic()>end:raise RuntimeError('Application Service is not ready in the worker network namespace')\n time.sleep(.1)\n"
    labels = {"slimfaas-campaign": NS}

    def obj(kind, name, **values):
        return {
            "apiVersion": "batch/v1" if kind == "Job" else "v1",
            "kind": kind,
            "metadata": {
                "name": name,
                "namespace": NS,
                "labels": labels | {"app": name},
            },
        } | values

    config = obj(
        "ConfigMap",
        "dotnet-sdk-probe",
        immutable=True,
        data={
            "probe.py": (ROOT / ".bin/test-dotnet-sdk-payloads.py").read_text(),
            "entry.py": entry,
        },
    )
    worker = obj(
        "Job",
        "dotnet-sdk-worker",
        spec={
            "backoffLimit": 0,
            "template": {
                "metadata": {"labels": labels | {"app": "dotnet-sdk-worker"}},
                "spec": {
                    "restartPolicy": "Never",
                    "initContainers": [
                        {
                            "name": "wait-for-owned-service",
                            "image": IMAGE,
                            "imagePullPolicy": "IfNotPresent",
                            "command": ["python3", "-c", WAIT_SERVICE],
                        }
                    ],
                    "containers": [
                        {
                            "name": "worker",
                            "image": WORKER,
                            "imagePullPolicy": "IfNotPresent",
                            "args": [
                                "--ws-mode",
                                "--function-name",
                                "dotnet-kubernetes-pr506",
                                "--ws-url",
                                "ws://slimfaas-0.slimfaas:5003/ws",
                            ],
                            "env": [
                                {
                                    "name": "SlimFaas__BaseUrl",
                                    "value": "http://slimfaas-http:5000",
                                }
                            ],
                            "resources": {
                                "requests": {"cpu": "100m", "memory": "128Mi"},
                                "limits": {"memory": "1Gi"},
                            },
                        }
                    ],
                },
            },
        },
    )
    probe = obj(
        "Job",
        "dotnet-sdk-probe",
        spec={
            "backoffLimit": 0,
            "template": {
                "metadata": {"labels": labels | {"app": "dotnet-sdk-probe"}},
                "spec": {
                    "serviceAccountName": "slimfaas",
                    "restartPolicy": "Never",
                    "nodeSelector": {"node-role.kubernetes.io/control-plane": ""},
                    "tolerations": [
                        {
                            "key": "node-role.kubernetes.io/control-plane",
                            "effect": "NoSchedule",
                        }
                    ],
                    "containers": [
                        {
                            "name": "probe",
                            "image": IMAGE,
                            "imagePullPolicy": "IfNotPresent",
                            "command": ["python3", "/probe/entry.py"],
                            "volumeMounts": [
                                {
                                    "name": "probe",
                                    "mountPath": "/probe",
                                    "readOnly": True,
                                },
                                {"name": "evidence", "mountPath": "/evidence"},
                            ],
                            "resources": {
                                "requests": {"cpu": "100m", "memory": "128Mi"},
                                "limits": {"memory": "1Gi"},
                            },
                        }
                    ],
                    "volumes": [
                        {"name": "probe", "configMap": {"name": "dotnet-sdk-probe"}},
                        {
                            "name": "evidence",
                            "hostPath": {"path": "/evidence", "type": "Directory"},
                        },
                    ],
                },
            },
        },
    )
    objects = {"apiVersion": "v1", "kind": "List", "items": [config, worker, probe]}
    return objects


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--kubeconfig", type=Path, required=True)
    parser.add_argument("--namespace", required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--worker-image", required=True)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    if len(options.namespace) > 63 or not re.fullmatch(
        r"slimfaas-pr506-[a-z0-9]+(?:-[a-z0-9]+)*", options.namespace
    ):
        parser.error("Use a dedicated slimfaas-pr506-* namespace")
    prefix = [
        "kubectl",
        "--kubeconfig",
        str(options.kubeconfig.resolve()),
        "--context",
        "kind-slimfaas-pr506",
        "--namespace",
        options.namespace,
    ]

    def kubectl(*arguments, **kwargs):
        return subprocess.run(prefix + list(arguments), check=True, text=True, **kwargs)

    namespace = json.loads(
        kubectl(
            "get", "namespace", options.namespace, "-o", "json", capture_output=True
        ).stdout
    )
    if (
        namespace["metadata"].get("labels", {}).get("slimfaas-campaign")
        != options.namespace
    ):
        raise RuntimeError("Refusing to operate on an unowned namespace")
    job = json.loads(
        kubectl(
            "get", "job", "campaign-driver", "-o", "json", capture_output=True
        ).stdout
    )
    if not job.get("status", {}).get("succeeded"):
        raise RuntimeError(
            "Finish the initial campaign successfully before running the SDK probe"
        )
    statefulset = json.loads(
        kubectl(
            "get", "statefulset", "slimfaas", "-o", "json", capture_output=True
        ).stdout
    )
    runtime = next(
        item
        for item in statefulset["spec"]["template"]["spec"]["containers"]
        if item["name"] == "runtime"
    )
    if runtime["image"] != options.image:
        raise RuntimeError("The requested image must match the campaign runtime")
    for kind, name in [
        ("configmap", "dotnet-sdk-probe"),
        ("job", "dotnet-sdk-worker"),
        ("job", "dotnet-sdk-probe"),
    ]:
        if kubectl(
            "get", kind, name, "--ignore-not-found", "-o", "json", capture_output=True
        ).stdout.strip():
            raise RuntimeError(
                "SDK resources already exist; retain them and use a fresh campaign"
            )
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    objects = probe_resources(options.namespace, options.image, options.worker_image)
    (output / "dotnet-sdk-resources.json").write_text(
        json.dumps(objects, indent=2) + "\n"
    )
    (output / "runtime-before.json").write_text(
        json.dumps(statefulset, indent=2) + "\n"
    )
    kubectl(
        "set",
        "env",
        "statefulset/slimfaas",
        "Logging__LogLevel__SlimFaas.WebSocket=Debug",
        "Logging__LogLevel__SlimFaas.Endpoints=Debug",
    )
    kubectl("scale", "statefulset/slimfaas", "--replicas=3")
    kubectl("apply", "-f", "-", input=json.dumps(objects))
    try:
        deadline = time.monotonic() + 900
        while time.monotonic() < deadline:
            status = json.loads(
                kubectl(
                    "get", "job", "dotnet-sdk-probe", "-o", "json", capture_output=True
                ).stdout
            ).get("status", {})
            if status.get("failed"):
                raise RuntimeError(
                    "In-cluster .NET SDK probe failed; original evidence is retained"
                )
            if status.get("succeeded"):
                return 0
            time.sleep(2)
        raise TimeoutError("In-cluster SDK deadline exceeded")
    finally:
        with (output / "collect-before-stop.log").open("w") as log:
            subprocess.run(
                [
                    "python3",
                    str(ROOT / ".bin/run-kubernetes-campaign.py"),
                    "collect",
                    "--kubeconfig",
                    str(options.kubeconfig.resolve()),
                    "--namespace",
                    options.namespace,
                    "--output",
                    str(output / "runtime-evidence"),
                ],
                stdout=log,
                stderr=subprocess.STDOUT,
                check=True,
            )
        for name in ["dotnet-sdk-probe", "dotnet-sdk-worker"]:
            with (output / (name + ".log")).open("w") as log:
                kubectl("logs", "job/" + name, stdout=log, stderr=subprocess.STDOUT)
        kubectl("delete", "job", "dotnet-sdk-worker", "--wait=true")
        kubectl("scale", "statefulset/slimfaas", "--replicas=0")


if __name__ == "__main__":
    raise SystemExit(main())
