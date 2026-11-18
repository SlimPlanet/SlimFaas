# Terminating pods are not new routing destinations

A deterministic test using the real Kubernetes client against a typed fake API
sets a pod's deletion timestamp while its container and pod readiness conditions
remain true. The previous mapper still marks that pod ready, and the regression
fails. This is the interval Kubernetes exposes during graceful termination.

The mapper now excludes deletion-marked pods from ready-target selection and
retains their UID and termination state. The new fields are optional/defaulted
for local and Docker providers. Active requests keep their existing timeouts;
the change does not introduce retries or discard the last known snapshot during
a discovery outage. Sixteen mapping, ownership, port, snapshot and synchronization
checks pass, including the original failing case.

[RED/GREEN evidence](terminating-pod-regression.json).
This reproduces a source-selection defect independently of connection reuse.
Real Kubernetes replacement, DNS/pool convergence and per-ID execution accounting
remain separate required validation; the targeted result does not clear the
source189 async NoPort/Raft errors or establish performance acceptance.
