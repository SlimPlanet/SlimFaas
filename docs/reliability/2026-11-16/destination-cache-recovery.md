# StatefulSet destination cache and Kubernetes recovery regressions

A function StatefulSet can replace a pod without changing its controller
resource version or aggregate endpoint readiness. The previous cache key omitted
pod revisions entirely, so it reused the old function snapshot after a successful
LIST. The HTTP-boundary reproduction retains the old UID/IP after replacement;
two further cases incorrectly retain Ready=true for a NotReady or terminating
pod while another pod stays ready. All three fail before the runtime correction.

Deployments and StatefulSets now share a cache revision built from controller
identity/revision, aggregate readiness and pod name/UID/revision. The corrected
suite passes all three regressions and the watch/LIST outage, 410 history-gap,
scale-to-zero/wakeup and captured-Proxy destination tests. Last snapshots remain
available while both watch and LIST are unavailable. Restored LIST access alone
reconciles the replacement while watch remains down, within the existing
15-second test deadline. No invocation retry or timeout budget changes here.

The first two watch tests initially failed because the fake assigned every new
pod resourceVersion=1. After correcting fake mutation versions, these four
recovery cases pass with the existing watcher/polling runtime. That fixture issue
is kept separate from the StatefulSet runtime defect, which is reproduced after
the fixture correction. Original failing TRXs remain indexed.

Validation: **2,459 .NET tests pass**, zero failures/skips. Python routing 9,
platform 47, chaos/accounting 32 and Sonar-boundary 7 pass without skips. Compiler
analyzers and dependency audits stay enabled. These HTTP-boundary tests do not
replace actual StatefulSet replacement, final-source AOT/performance or fault
campaigns. They establish a cache defect, not the cause of the historical
production incident, healthy-load NoPort errors or seven duplicate executions.
[Reproductions, commands, source hashes, TRXs and validation logs](destination-cache-recovery.json).
