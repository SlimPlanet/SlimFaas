# Captured configuration must not freeze pod destinations

Three deterministic regressions reproduce a stale synchronous routing snapshot:
a replaced pod remains selected, a deleted pod remains available, and an empty
captured pool cannot observe a later ready pod. All three fail on the source
containing the isolated PR467 connection-pool fix.

`Proxy` now resolves the function's destinations from the latest published
replica cache. The captured function still supplies its canonical name; endpoint
configuration and timeouts remain captured by the request pipeline. No extra
Kubernetes API request or HTTP retry is added. The 34 selected proxy/connection
checks pass after this correction, including all three original regressions.

This establishes a cache-selection defect independently of connection recycling.
It does **not** establish the cause of the source189 async `Not port or IP
available` errors: the async worker already constructed a live-cache Proxy.
Kubernetes replacement, termination, identity/reservation, watch-loss and full
end-to-end validation remain required. A pod can also disappear after selection;
the correction is not an atomic guarantee against Kubernetes lifecycle changes.

[Before/after evidence and hashes](proxy-snapshot-regression.json).
The separate PR467 transport checks use a real loopback TCP connection and an
active response body: the legacy pool reuses its connection, while the finite
pool replaces it only after that response finishes. Its test clock is shortened;
the asserted production lifetime remains five minutes. Kubernetes/DNS convergence
with that actual lifetime remains a separate required campaign.
