# Performance measurements

Growth measurements are separate from the ordinary verification gate. They are optional unless required
by task acceptance criteria or an agreed release plan; a release does not automatically require both
growth commands. Choose the smallest existing scope that answers the concrete performance question.
State the reference and workloads for a comparison. Preserve evidence from running work; a policy change
does not resolve an observed regression or invalidate an agreed acceptance criterion.

Before a full comparison, complete the [verification prerequisites](contributing.md#verification):
stable relevant source and workload inputs, with applicable ordinary correctness qualification passing.
CI enforces the successful `Gate` dependency; direct local commands rely on the caller to establish
these prerequisites. Diagnose a failed path with focused checks before repeating expensive coverage.

| Purpose | Command | Evidence |
| --- | --- | --- |
| Focused write observation | `CLAIMCORE_GROWTH_PROFILE=1000:1 bash eng/verify.sh growth` | One current-source write profile and three cardinality populations; no reads or reference comparison. |
| Focused comparison | `CLAIMCORE_GROWTH_PROFILE=10000:1 bash eng/verify.sh growth-compare REF` | Three alternating pairs for that write profile and cardinality controls; diagnostic only. |
| Full comparison | `bash eng/verify.sh growth-compare REF` | Three alternating pairs with relative budgets over shared workloads. |
| Full observation | `bash eng/verify.sh growth` | The complete current-source workload set; no reference ratios. |

Single-profile `growth` can exit zero while its report marks qualification incomplete: only the selected
observation passed. Diagnostic `growth-compare` returns 2 when its budgets pass, as described below.
Neither result qualifies unmeasured workloads. Choose a broader run when needed by the question or agreed
acceptance scope; retain workload identity and all comparison checks when claiming a comparison result.

Run `bash eng/verify.sh growth-compare REF` to compare against a commit. It captures the candidate's
HEAD and tracked working changes, retaining the source patch and its SHA-256. It refuses any nonignored
untracked files: stage intended new source files and move unrelated scratch files outside the checkout.
It refuses source changes during capture. Both source-stable inputs build in disposable
detached worktrees with locked restore. The comparison alternates
reference → candidate three times. Write and cardinality profiles use fresh PostgreSQL containers;
read profiles share the fresh database for their case population. The compared set
is writes `10000:1`, `100000:1`, `100000:16`, the 1,000-case cardinality comparison, and read profiles
at 100k cases. Six common reads cover search, filtered cases, notification order, summary, global
authority history and actor-filtered authority history. Reminder-feature measurements comprise seven
profiles: derived follow-up/summary, pending/completed queues, bounded current case, financial isolation
and global work overview. Plain-sight read profiles also measure `case.views`, `reminder.views` and the
bounded typed `case.timeline`, including high-child-fanout change decoding and actor enrichment. New
keys without a reference equivalent remain not applicable for relative ratios; their actual candidate
measurements are still retained. The [workload plan](../tests/ClaimCore.IntegrationTests/GrowthWorkloadPlan.fs) owns the
exact set. Both inputs execute their selected workloads. Only shared workload keys yield ratios; unmatched profiles
are reported as not applicable.

For a targeted diagnostic or an injected-delay control, the existing `CLAIMCORE_GROWTH_PROFILE`
selector accepts `10000:1`, `100000:1` or `100000:16`. It still runs three alternating pairs and the
normal growth harness's three cardinality controls, without read workloads. The report states that
qualification is incomplete: exit 1 means a budget/check failed, and exit 2 means diagnostic budgets
passed but the required full set was not run. A diagnostic can never return qualification success.
Reduced runs do not establish a full comparison pass.

Comparison fails when a candidate median's write/read throughput is below 0.85×, or its p95, p99 or verification
time exceeds 1.30× the reference. Reports retain all six raw runs, medians, ratios and workload
fingerprints in JSON and Markdown under `artifacts/verification/growth-compare-*` (or beneath
`CLAIMCORE_TEST_REPORT_DIRECTORY`). These are same-host, same-session relative measurements, not an SLA.
Different workload fingerprints refuse comparison, naming the differing members.

`comparison-progress.json` is an atomic observation of preparation and alternating child runs. It records
source and harness identities, completed side count and the active owned checkout/report paths; it never
reports a partial pass. `comparison.json` remains the final verdict, including cleanup failures. Current
harnesses publish `artifacts/verification/growth/*/read-progress.json` in the checkout with seed/measurement
phase, counts and the last completed read report. Each completed read report is published immediately;
`run.json` bookkeeping can lag until the population fixture has been disposed. Incomplete progress is not
qualification evidence and does not permit skipping outstanding workloads.

Read reports include measured latency samples in observation order, excluding warmups, with the percentile
index rule recorded beside them. Comparison reports add informational per-pair ratios, run spread and
available sample evidence for latency percentiles. Missing reference samples are reported rather than
reconstructed. These observations do not change sampling, budgets or the median comparison rule. Retain
the source patch, harness patches, build logs and workload fingerprints together when reproducing a
result; a source hash alone does not identify the assembly loaded by a separate diagnostic.

A reference without workload fingerprints requires a recorded manual review of act cycles, populations,
authored values and read queries. Set `CLAIMCORE_GROWTH_EQUIVALENCE_FILE` to a JSON review with exact
`reference` and `candidate` commit IDs, `candidatePatchSha256` when working changes are present,
a `workloads` array of reviewed keys (for example
`write/10000/1`, `write/100000/1`, `write/100000/16`, `cardinality/1000`, `read/search/100000`,
`read/filtered/100000`), and a `review` explaining the evidence. Missing or mismatched reviews refuse
before building. This is explicit manual equivalence evidence, not an inferred fingerprint match.
Single-profile diagnostics also require review of `cardinality/1` and `cardinality/100`.

The comparison labels its disposable test containers and, when needed, adapts reference harness selection.
Every adaptation is retained as a patch in the report. Product code stays at the recorded commit.
The comparison removes its worktrees, build outputs and labelled containers on success or failure,
while preserving reports and failure output.

The full `bash eng/verify.sh growth` observation includes nine write-contention profiles,
three cardinality comparisons and search/filtered, notification-order, summary and explicit-work
follow-up/summary workloads at 1k, 10k and 100k cases. At the
100k case population, global and actor-filtered authority-history reads also run after 100k additional
grant/revoke events. Those events are seeded after the existing case-query measurements, preserving
their workload inputs and fingerprints. The authority workloads have their own declared fingerprints.
After those common measurements, one open case per thousand receives explicit responsibility and a past
Pending reminder based on the database business date. Mine/Overdue/FOLLOW_UP pages and filtered summaries check
independently named cases, account identity, dates and counts before recording their separate latency
samples. High-fanout workloads then populate 100/1,000/10,000 Pending and completed reminders on one
case, independently checking queue continuations, bounded current views, unique financial totals and
global attention. The `reminder-derived-follow-up` and `reminder-derived-work-summary` profiles use the
independent Work/Reminder seed. Only profiles with matching workload keys and fingerprints yield ratios;
common read/write/cardinality comparisons retain their budgets.
[GrowthQualificationTests](../tests/ClaimCore.IntegrationTests/GrowthQualificationTests.fs) owns sequencing
and completed-report checks; its `GrowthReadWorkloads` helper owns read populations and latency reports. Selecting one `CLAIMCORE_GROWTH_PROFILE` marks qualification
incomplete. Report actual counts, throughput, p50/p95/p99 latency and verifier memory/time. Measurements
establish neither an SLA nor constant total-system memory.

The gate bounds managed completed-report retention, not total caches, image or artifact use. Protect active
runs, measure resource use and reclaim only owned temporary resources. Locked NuGet/npm restore and audits
still run with caches; shared caches are not disposable task resources.


## Open plain-sight search comparison

The [completed same-host comparison](https://github.com/resoltico/claimcore/actions/runs/41993384662)
compared `60b3c8ca` with `30de1b32` and passed 50 of 52 relative checks. At 100,000 cases, search p95
rose from 2.598 ms to 3.700 ms (1.424×; maximum 1.30×), and throughput fell from 464.177 to 335.288
requests/second (0.722×; minimum 0.85×). These two failures remain open. The passing ordinary CI for
[PR #144](https://github.com/resoltico/claimcore/pull/144) does not supersede them.

A bounded source/SQL investigation on 2026-11-06 confirmed that `Queries.exactReference` adds one
`FindCaseByReference` statement for a syntactically valid search, including the absent `needle` query.
An absent match returns before the additional per-target authority read. A found match still needs that
read, independently of the current page/filter; removing it would change visibility or disclosure.

A disposable PostgreSQL 18.6 probe used the current baseline's exact cases table and unique reference
index with 100,000 synthetic erased rows. Two `EXPLAIN (ANALYZE, BUFFERS)` absent-reference selects
used `cases_reference_key`, returned zero rows and reported 0.017/0.024 ms execution time. This checks
the missing-index/scan hypothesis only: it uses a reduced projection, not the growth dataset, and omits
network/driver, authorization and decoding costs. It cannot attribute the historical 1.102 ms p95
increase or establish a new latency/throughput pass. No product search change or threshold change was
made. A future acceptance decision needs deployment requirements; any claimed relative repair still
needs a stable same-host comparison against the recorded workload and budgets.
