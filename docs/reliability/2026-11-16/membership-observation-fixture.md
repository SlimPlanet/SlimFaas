# Membership observations follow the current leader

Windows coverage on `81881c7a` records a `NotLeaderException` while removing an
offline member. The fixture captures a leader and makes six direct reconciliation
calls on that one host. Production instead runs a worker on every node, with
each cycle checking current leadership; another node continues reconciliation
after a leader change. This differs from the earlier initial-seed setup race.

The fixture now reselects the leader for each desired-membership observation,
retains each host's worker observation state and waits for the actual expected
configuration. Only transient `NotLeaderException` from this idempotent
reconciliation is handled. The existing 90-second fixture deadline, 120-second
theory limit, two-second request/one-second RPC settings and single-attempt
post-fault application writes remain unchanged. Unrelated errors still fail.

Two deterministic observation cases fail with the extracted original one-shot
loop; the other three deadline/error controls pass. All five controls, both real
three-node recovery cases and four bootstrap controls then pass (11 total).
[RED/GREEN evidence](membership-observation-fixture.json).
The original Windows failure is retained; a new Windows coverage run remains
required. This test-only change does not clear healthy-traffic Raft errors or
alter runtime membership behavior.
