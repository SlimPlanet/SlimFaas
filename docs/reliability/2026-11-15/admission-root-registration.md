# Admission root-formatter initialization

The first reference in the pooled-verification application comparison fails on
frozen source `511630668ffad507f14d6ce7d42c941bb224ad84`: 199 of 200 requests are
confirmed; one HTTP 500 has an unknown outcome. Kestrel records an unhandled
`NullReferenceException` in `MemoryPackSerializer.Deserialize<FileAdmissionResult>`
through `SlimDataService.ExecuteFileAdmissionAsync`. The full 180-second
observation finds no confirmed missing IDs or successful duplicate ID/function
pairs. Complete runtime logs contain no managed OOM, container restart or
OOMKilled event. The healthy-workload exception invalidates the reference.
**Application performance remains INCONCLUSIVE.**

Namespace `slimfaas-pr506-fa52-abba-1-before`, its stopped volumes, WAL, snapshots,
receipts and hash index are retained. Later ABBA ordinals did not run. Waiting
geometry and threshold coordinators stop on that dependency; their prepared
profiles are not observations. The affected production methods are identical
between source511 and `f13e8a60`.

## Cause and correction

MemoryPack 1.21.4 identifies repository commit
`0f2fe0827907940768293b7bb81b9605a7d8cba8`. Its
[formatter provider](https://github.com/Cysharp/MemoryPack/blob/0f2fe0827907940768293b7bb81b9605a7d8cba8/src/MemoryPack.Core/MemoryPackFormatterProvider.cs#L47-L52)
publishes the registration flag before assigning the cached formatter. A
concurrent first use can initialize that cache after observing the flag and
return its still-null formatter. This matches the observed exception location.
Tests reproduce that state deterministically and restore the cache in `finally`;
they run in collections with parallelization disabled.

Raft replay already bypasses the root lookup through generated
`ReadPackable`/`WritePackable` dispatch. Client admission and server
admission-result publication missed that mechanism. The correction reuses the
existing internal codecs for batch requests/responses, admission
mutations/results, Streams and DLQ mutations. SlimFaas receives internal access
to these SlimData helpers. Exact sizing retains one final payload array and
avoids an additional growing buffer of the combined payload size.

No payload members, protocol version, HTTP acceptance rule, persistence barrier,
timeout, default or dependency changes. The Raft regression compares exact
result bytes with the existing representation.

All fourteen client regressions fail before correction: ten expose a null
formatter directly; four remain in the existing retry path until the bounded
test deadline. They cover global and partitioned batching, admission
mutation/result, batch request, successful/conflict response, Streams and DLQ.
All fourteen pass after correction. A separate Raft admission-result regression
fails before correction; it and the six existing replay regressions pass
afterward. Full validation passes **2,421 .NET tests with zero failures/skips**,
including 52 .NET SDK tests, plus 37 Python SDK tests, 28 chaos-harness tests,
41 platform tests and two MemoryLab report tests. The documentation build checks
23 pages, 1,685 links/assets and 406 search entries. Full UI builds, analyzers and
audits remain active. New-source AOT/image publication and application replays
remain pending; these regressions alone do not establish performance or
endurance acceptance.

## Closed source-f13 application controls

Before the reference failure, all four logical volume-content-loss controls pass
on `f13e8a60ad3f02b987eda45e1459787418a4999c`, each with one confirmed request,
zero unknowns, verified SHA-256/length and full 180-second observation. An absent
member loses its in-memory announcements; the ingress returns with empty storage
and a new storage identity.

| Control | Acceptance | Absent-node repair after ready | Fresh-ingress repair after ready |
|---|---:|---:|---:|
| Files / follower | 2.597 s | 3.063 s | 4.067 s |
| Files / leader | 5.113 s | 3.260 s | 4.061 s |
| Async / follower | 2.601 s | 3.872 s | 4.065 s |
| Async / leader | 5.115 s | 3.668 s | 4.062 s |

The normal-probe no-remote-copy control also passes: both body APIs return 503;
a separate Raft Set returns 200. No handler receipt/public alias appears, and
temporary bodies are cleaned during the full observation. The two body outcomes
remain unknown; the Set is the sole confirmed operation. Original runtime
identities remain unchanged.

All three macOS Native AOT publications and native controls pass on source-f13.
Its managed/trimmed Linux ARM64 image configuration hash is
`940bf98cff2fe22302ead7434a75e782588a3614c5893897d52081c2f4cd23b8`, verified in
all four dedicated kind nodes. These observations precede the serializer
correction. kind workers share one VM filesystem: logical content loss does not
certify independent-hardware tolerance.

Disk recovery removes verified unused installers/build caches or compresses
closed bytes losslessly. Transparent APFS compression preserves publication
paths, lengths and hashes. Older VM import archives remain as SHA-verified gzip
with an original-to-compressed index. Sessions, sources, runtime volumes, WAL,
snapshots, journals and original failures remain.

See [earlier cost findings](persisted-copy-validation-511.md),
[micro-probe scope](pooled-file-verification.md) and the
[benchmark protocol](../../benchmarking.md). Adverse and incomplete results remain
recorded. No merge, final non-regression acceptance or LTS certification follows.

[Evidence and hashes](admission-root-evidence.json), the
[closed raw index](admission-root-application-index-03.json.gz) (500 local files,
six retained VM roots), [per-ID journals](admission-root-per-id-ledgers-03.json.gz)
(1,510 rows from 25 originals) and the
[frozen reproducer](admission-root-reproducer.tar.gz) retain both failures and
passing controls. The reproducer starts from `f13e8a60`, copies the exact test
sources, runs the fifteen failing-before cases, applies the product-only patch
and reruns them. Its README records the commands and safe fresh-root requirement.
