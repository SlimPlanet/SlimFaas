# Source f2f80124 publications and validation

All three macOS ARM64 Native AOT publications complete on
`f2f80124af9b88d0ad4bbd94653372cf393ad1b5`: SlimFaas, SlimData and SlimFaasMcp.
The full UI build, documentation build and Planet Saver coverage also pass.
The documentation checks 23 pages, 1,689 local links/assets and 408 search
entries; Planet Saver passes four tests. No dependency or lockfile changes.
The new publications **have not yet passed application non-regression or long
endurance**. Historical source189 results do not transfer to this source.

## Compilation and image identity

The toolchain is SDK 11.0.100, Node 26.13.0 and pnpm 12.18.0. Compiler, restore,
trim/AOT analyzers and dependency audits remain enabled. Native publication
omits only the regenerable dependency graph XML (`IlcGenerateDgmlFile=false`).
The retained Apple linker output includes missing prebuilt Clang module-cache
warnings in SDK debugging information; publication succeeds without adding
warning suppressions. Original logs and their hashes remain in the manifest.

The Linux ARM64 runtime is **managed/trimmed**, rather than Native AOT. Both
container variants use identical managed, self-contained C# drivers from source
f2f80124; native macOS campaigns have separate framework-dependent managed
drivers targeting the Native AOT runtime. A compressed ledger records SHA-256
for all **1,043 publication files**.

| Image | Runtime source | Immutable image ID | Dedicated kind imports |
| --- | --- | --- | ---: |
| `localhost/slimfaas-pr506:ownership-flat-tools-01` | `18982623` | `7737cbfac45d` (full ID in manifest) | 4 |
| `localhost/slimfaas-pr506:ownership-flat-after-01` | `f2f80124` | `8660bf7d8033` (full ID in manifest) | 4 |

The tools image retains the historical reference runtime and supplies the shared
generator and synthetic function. The candidate contains the new runtime. Both
original source-built layered images remain retained. Squashing removes old
hidden image layers while preserving the checked runtime DLLs, driver DLLs and
resolved entrypoint, arguments, environment, user, work directory, exposed ports,
volumes and stop signal. Import completes on the control plane and three workers.
An image import refusal at 19.736 GiB of host free space remains recorded in
attempt02; the subsequent verified imports do not erase that refusal.

The [manifest](ownership-publications-f2.json) includes each native executable's
hash, full image IDs, DLL hashes, archive hashes, exact build commands and log
hashes. Resolve the [complete publication ledger](ownership-publication-file-index.jsonl.gz)
relative to `artifacts/pr506`. Build contexts used only for packaging can be
regenerated from the retained canonical publications; their duplicate files
were subsequently reclaimed without deleting those publications.

## Tests and actual Sonar scope

Local full validation passes **2,477 .NET tests**, zero failures/skips, plus the
official Python SDK and harness tests documented in the
[reservation report](async-reservation-coalescing.md). Windows CI independently
passes the same 2,477 .NET tests. Main workflow `44003554494`, dashboard workflow
`44003554449`, reliability workflow `44003554270` and FOSSA License Compliance
complete successfully for this source. Conditional publication/deployment jobs
are separate from required validation jobs.

Completed Sonar task `AaHnYKpcRiBE5d6bjkVG`, analysis
`5fbbcbdf-2f15-4dd9-9e6b-0bb6a6da36aa`, verifies the benchmark analysis boundary.
The coverage importer reports 546 C# files, including **301 main files with
coverage**; the PR API returns 372 analyzed files. These counts have different
scopes. Unchanged files can be omitted by the PR API; product/SDK project
submissions are checked separately. The
[completed scope evidence](sonar-completed-scope-f2.json) preserves those results.
The separate external quality gate **fails**: Reliability C and Security D,
required A. Production code and SDKs remain analyzed; no finding or rule is
waived in these publication/report changes.

## Pending application controls

The next bounded controls use the canonical C# generator with normal Kubernetes
probes: StatefulSet ordinal replacement, direct-IP process exit and termination,
C16 with 0.5-second in-flight requests, Service/headless DNS and async per-ID
tracing. For example, after satisfying disk/isolation preflights:

```bash
python3 .bin/run-kubernetes-routing.py \
  --kubeconfig artifacts/pr506/kubernetes/kubeconfig \
  --namespace slimfaas-pr506-routing-f2-example \
  --output artifacts/pr506/routing/f2-example \
  --image localhost/slimfaas-pr506:ownership-flat-after-01 \
  --controller-image localhost/slimfaas-pr506:ownership-flat-tools-01 \
  --source-commit f2f80124af9b88d0ad4bbd94653372cf393ad1b5 \
  --route direct --function-controller statefulset --replacement-mode exit \
  --scenario sync --concurrency 1 --rps 20 \
  --before-seconds 30 --after-seconds 60 --retire-delay 600
```

This is a prepared command, **not a passing campaign result**. Following the
disk refusal, cleanup and byte-preserving compression recover approximately
23 GiB of host free space, below the plan's 30 GiB restart target. The new series
has therefore not started. No volumes, WAL, snapshots, sessions or raw evidence
were deleted to bypass that guard. The threshold 256/512/128 KiB screen, complete
per-ID attribution of historical duplicates, source-final application comparisons
and affected long endurance remain outstanding. No default or deadline changes
and no LTS certification are claimed.
