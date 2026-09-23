# Snapshot fixtures

`v0.84.0-6-0.snapshot` is the existing legacy snapshot payload fixture.

`dotnext-6.6.0-wal.zip` contains a snapshot and its compacted WAL produced by
SlimFaas at `fe3b08e7` with DotNext 6.6.0, Native AOT, macOS ARM64. It contains only
synthetic test data: 180 sets named `existing-0` through `existing-179`, with values
`value-for-existing-<number>`. Their internal keys have the `data:set:` prefix.
The snapshot is `151-1`; WAL data chunks are 16 KiB and metadata pages are 4 KiB.
No membership configuration file or deployment data is included.

The fixture came from the third node after the baseline phase of
[test-slimdata-raft-recovery.py](../../../.bin/test-slimdata-raft-recovery.py),
before that node had been opened by the candidate. The baseline executable's
SHA-256 is `11396a2fa8c7d33bbce37183a3d35c71a7721f0783aa89ad7e59c70c24016e6b`.
Extraction always targets a temporary directory; tests never mutate this archive.

The restoration test passes with 6.6.0 and the current 6.8.1 candidate, including
on a 16 KiB-page ARM64 host. The intervening 6.7.2 release failed on systems with
pages larger than 4 KiB; 6.8.1 carries the upstream compatibility fix. Keep this
test enabled on every architecture so that a future regression cannot be hidden
by a platform skip. Keep the explicit 16 KiB data chunk size when loading this
fixture on other hosts, and await application of the committed suffix before
checking keys beyond the snapshot.
