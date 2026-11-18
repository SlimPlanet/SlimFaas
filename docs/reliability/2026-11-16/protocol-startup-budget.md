# Separate startup and recovery bounds in the protocol probe

The first final-source kind role stops before its long absence tests: the
silent-follower protocol probe times out waiting for the restarted HTTP listener.
There are **zero confirmed client operations and zero unknown outcomes** in this
failed control. Its original Job, logs, volumes and state are retained; it is not
counted as a completed endurance.

The priority coordinator incorrectly applied `--recovery-timeout 180` to every
wait, replacing the Kubernetes launcher's historical 300-second allowance for
this startup path. The [benchmark guide](../../benchmarking.md) already records
that the membership announcement can consume approximately 200 seconds before
the listener opens when its first peer is silent. The separate majority-ready/
write check after listener startup retains its 15-second limit. This startup
limitation is not a new performance improvement or a waived failure.

The harness now accepts `--protocol-startup-timeout`, applying it only to the
process-restart and HTTP-listener waits in that probe. Its default remains the
recovery bound for compatibility. Corrected container campaigns explicitly use
**startup 300 s, recovery/confirmed drain 180 s, ready/write 15 s**. The option accepts
positive finite values and the report records the selected startup allowance.
It never changes the recovery timeout or the fixed 15-second ready/write guard.
Runtime source `18982623`, Native AOT executable and managed ARM64 image
`after-tools-net11-71` stay unchanged.

Four unit tests cover independent budgets, unchanged default behavior, actual
launcher argument order and invalid bounds. Two fail against the old harness
(one assertion failure and one unsupported CLI argument). After correction,
**all 32 chaos-harness and 41 platform tests pass**, including the default 180 s
compatibility case and a simulated 201 s startup with recovery still 180 s and
ready/write still 15 s. No actual sleep or external service is used by these tests.
The failed command and original results remain unchanged; corrected runs use
fresh namespaces and independently attributed harness hashes.

The [first corrected real control](protocol-startup-first-real-control-113.json)
now passes with the same image: crash-to-listener is **202.094 s**, followed by
majority-ready/write in **0.464 s**. Startup remains within the historical 300 s
allowance and ready/write within 15 s; the 180 s recovery/drain bound remains
explicit in the actual driver arguments. This validates the separate budgets,
not a reduction of the underlying approximately 200 s startup limitation. The
long role continues separately in the fresh namespace.

The [evidence manifest](protocol-startup-budget-evidence-112.json) and
[frozen reproducer](protocol-startup-budget-reproducer-112.tar.gz) retain 37
files: old/current sources, failing/passing test output, patch, original failure
report and collected UID/restart-specific controller logs/configuration. Archive
SHA-256:
`5439237654dde50b164394c024886b1eb29b3333380277c2750976e616ae17df`.
The [quiescent original-state index](native-original-state-and-failed-kind-index-189.json.gz)
also retains hashes for all 76 files in the stopped failed kind root, alongside
the three completed Native role originals. No original volume is reused for a
new measurement, cold-bootstrapped or resubmitted to obtain a passing result.

For a fresh dedicated kind control, forward both independent bounds:

```bash
protocol_campaign_id="slimfaas-pr506-protocol-repro-$(date +%s)"
python3 .bin/run-kubernetes-campaign.py launch \
  --kubeconfig artifacts/pr506/kubernetes/kubeconfig \
  --namespace "$protocol_campaign_id" \
  --output "artifacts/pr506/kubernetes/$protocol_campaign_id" \
  --image localhost/slimfaas-pr506:after-tools-net11-71 \
  --controller-image localhost/slimfaas-pr506:after-tools-net11-71 \
  --source-commit 189826231fe43fe3069e8a1f7724f83589432c6f \
  --probes endurance --scenarios protocol-timeout \
  --protocol-startup-timeout 300 --recovery-timeout 180
```

Retain and collect the resulting namespace with the same identifiers before
stopping only its labelled workloads. The three corrected long roles and final
general-validation verdict remain separate, open requirements. No merge or LTS
certification is claimed.
