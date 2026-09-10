# External metrics autoscaling demo

An always-running Python standard-library exporter controls `jobs_pending`.
Workers expose `/health` and `/hello/{name}`. They do not consume a real queue:
you control the backlog explicitly so wake-up, draining and telemetry failure are repeatable.

## Native local mode

Requires Python 3.10+ and the normal SlimFaas development prerequisites.
From the repository root, run this standalone manifest separately from the main demo:

```bash
dotnet run --project src/SlimFaas -- local validate -f ../../slimfaas.local.external-metrics.yaml
dotnet run --project src/SlimFaas -- local up -f ../../slimfaas.local.external-metrics.yaml
```

In another terminal:

```bash
# Observe zero replicas without calling the worker.
curl http://127.0.0.1:30020/status-functions
curl -X PUT 'http://127.0.0.1:9090/pending?value=73'
# The external signal now requests eight replicas, without an HTTP wake-up.
curl http://127.0.0.1:30020/status-functions
curl http://127.0.0.1:30020/function/worker/hello/local

# A telemetry failure holds capacity even if the last observation was zero.
curl -X PUT 'http://127.0.0.1:9090/available?value=false'
curl http://127.0.0.1:30020/status-functions
curl -X PUT 'http://127.0.0.1:9090/pending?value=0'
curl -X PUT 'http://127.0.0.1:9090/available?value=true'
# Once HTTP inactivity and the 20-second stabilization window allow it: eight -> zero.
curl http://127.0.0.1:30020/status-functions
```

The state changes are asynchronous; poll status until the expected count appears.
Do not call the worker while checking scale-to-zero: that would create HTTP activity.
The short intervals here are for the demo; use a longer scale-down stabilization window in production.

The complete scenario can also be checked automatically against the running local demo:

```bash
python3 .bin/test-external-metrics-demo.py
```

For `/debug/promql/eval` with `source`, query the leader's direct HTTP port: external
health belongs to the collector on the leader. Followers report `Unavailable` until
they become leader and complete a scrape.

## Kubernetes

Install the newly built SlimFaas image in `slimfaas-demo`, then:

```bash
kubectl -n slimfaas-demo create configmap external-metrics-demo \
  --from-file=exporter.py=demo/external-autoscaling/exporter.py \
  --dry-run=client -o yaml | kubectl apply -f -
kubectl apply -f demo/deployment-external-metrics.yml
kubectl -n slimfaas-demo port-forward service/jobs-exporter 9090:9090
```

Use the same exporter `curl` commands in another terminal and observe:

```bash
kubectl -n slimfaas-demo get deployment worker --watch
```

The exporter has no `SlimFaas/Function` annotation and stays running at zero worker replicas.
Its control endpoints are only demo fixtures; keep the exporter inside the demo network.

See [the autoscaling guide](../../docs/autoscaling.md#external-metrics-and-opt-in-wake-up)
for syntax, failure behavior and production rollout/rollback steps.


### Inspect and simulate in the dashboard

Open `http://127.0.0.1:30020/#/live/scaling?function=worker` during the demo. The live view explains the exporter signal, policies, inactivity and replica application. Use **Simulate next decision** with a hypothetical value of `73` to preview wake-up without changing the exporter or waking the real worker. Changing the threshold from `10` to `20` previews a raw target of four instead of eight.

With the exporter at zero and the worker asleep, run the read-only multi-viewer smoke check:

```bash
python3 .bin/test-scaling-dashboard.py --viewers 6 --seconds 15
```

Then run `.bin/test-external-metrics-demo.py` for the actual exporter-driven `0 → 8 → 0` sequence. The new dashboard routes resolve the leader automatically, including through a follower’s HTTP port.
