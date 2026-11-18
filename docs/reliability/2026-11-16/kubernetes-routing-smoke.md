# Kubernetes direct-routing replacement smoke

The C# generator completes **136 sync calls and 136 matching successful receipts**
while a function pod is deleted and replaced. Every receipt has the expected body
length and SHA-256. There are no missing confirmed calls, successful duplicates,
unknown client outcomes or incomplete client operations. Actual HTTP is 1.1.

The old pod UID/IP serves 52 calls and the replacement UID/IP serves 84. Runtime
logs show exactly the corresponding 52 old-IP and 84 new-IP send URLs. Six backend
connection tuples are observed across the three SlimFaas nodes and two function
pods, establishing connection reuse. The new pod is observed ready after 2.20 s;
the first new-pod success occurs around 3.42 s after the delete request. The last
old-pod success occurs around 0.20 s after it, with none after the separate 15 s
routing budget. These are observational times: a post-case clock diagnostic finds
VM minus host +0.147 s with ±0.119 s round-trip uncertainty, without a pre-case
alignment sample. This does not justify sub-millisecond convergence precision.

C# reports p95 7.68 ms, p99 56.52 ms and maximum 3.024 s, including the replacement
pause. This 30-second/C1/4 KiB/5-target-rps smoke is **not a performance comparison**.
Three runtime error headers occur during bootstrap before the load Job begins;
they are retained. The complete three-node log audit finds no workload-time error
header or managed OOM. The audit regression now also recognizes a console ISO
timestamp nested inside Kubernetes' own timestamp prefix.

Runtime and both load binaries come from `1b5552b8`; the corrected host adapter is
`76e6e625`. Linux ARM64 runtime compilation is managed/trimmed. Original volumes,
UID/restart-specific logs, API observations, EndpointSlices, events, C# samples,
client journals, fsynced function receipts and their verified compressed export
remain retained. [Commands, identities, observations and raw hashes](kubernetes-routing-smoke.json).

The first startup failure remains separate: no load Job was created before the
collector API error. This successful smoke does not clear historical large-body
routing/Raft errors or duplicate deliveries. The five-minute pool, stable Service
and headless DNS comparisons, C16 traffic, async attempts/acks and final-source
fault/recovery acceptance remain pending.
