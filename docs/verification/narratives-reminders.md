# Narratives and reminders verification

All twelve work packages are implemented locally, including the two omissions found by the subsequent
independent audit: current reminder timing labels and CLI inactive-parent disclosure. Product source
`be3379e0c5bc97ea18df5bbe893fc5281273c704` passed fresh complete Chromium212/Firefox190/WebKit190 suites
and full standalone growth9/35. Native checks are reused only for unchanged relevant inputs, with their
actual source attribution below. This is staged local qualification, not one uninterrupted full-gate pass.
The three declared native CI environments remain unrun; no cross-platform readiness or rollout is claimed.

## Audit corrections and current-source verification

Current get/list responses expose required nullable server-derived `timing`; only active Pending work
has `OVERDUE`, `TODAY` or `UPCOMING`. The browser retains each loaded row's originating server basis.
CLI current single/page output uses the same timing and explicitly labels inactive parents in all three
languages. Historical effects/receipts retain their existing presenter without current timing. Authored
dates, states, date-filter semantics, permissions and persisted data are unchanged. Strict consumers
must regenerate; the generated client, schema, literal wire examples and integration guide are updated.

The browser correction also retains read admission until work settles and drains already-dispatched
obsolete reads under their own response deadline. Queued canceled reads never dispatch; obsolete results
stay hidden. The caller deadline includes queue time and is a separate bound. Mutation transport and
server capacity limits are unchanged. This is per-document coordination, not a cross-document/tab guarantee.

Current evidence is under `artifacts/verification/implementation-audit-reminders-um0_7wgb/` unless stated.

| Evidence | Actual result and tested source | Scope |
| --- | --- | --- |
| `gate-7c9401f9.log`, `gate-7c9401f9-reports/passing-stage-inventory.json` | 814 native unit/architecture, 276 PostgreSQL and 101 HTTP tests passed at7c9401f9; zero skips | Relevant F# product/db/contracts/native test inputs remain unchanged; `source-scope-reuse-be3379e0.json` records the checked paths. Original frontend671 is superseded below |
| `acceptance-22c53e32.log`, `acceptance-22c53e32-reports/results.trx` | 58 nonbrowser native acceptance cases passed at22c53e32, including actual published CLI timing/inactivity in English/Latvian/Arabic and JSON | That run failed its browser wrapper; no59/59 pass is claimed. Relevant CLI/server inputs are unchanged. Changed browser assets are exercised by the fresh complete suites below |
| `artifacts/verification/read-admission-lifetime/{drain-all-unit.log,drain-affected-flows.log,drain-negative-control.log,negative-control.log}` | 134 frontend files/673 tests passed; focused API/privacy set23/23, typecheck/lint passed at01b53c21 (same browser source asbe) | Original implementations fail intended unresolved-read/third-fetch-before-drain controls. Late receipt/history/private-field suppression remains asserted. Counts overlap the673, not additional coverage |
| `browsers-be3379e0-summary.json`, `browsers-be3379e0-reports/`, `connected-screens-be3379e0/inventory.json` | Fresh complete Chromium212, Firefox190 and WebKit190 passed; zero retries/skips; native browser wrapper1/1;121 safe PNGs and six loading reports retained | Current product sourcebe; Release/frontend builds passed with zero native warnings. Existing structured completion guards checked discovered files and every result. Local platform only; no WCAG certification |
| `growth-be3379e0-{summary.json,results.trx,run.json,log}`, raw `artifacts/verification/growth/f9fd8f36e33d4c6f9efd5e98772b081d/` | Fresh full qualification PASSED:9/9 writes,35/35 reads, cardinality1/100/1000 and high fan-out100/1000/10000 Pending plus equal Completed; native1/1, zero skips, about29m47s | Exact product sourcebe, no diagnostic/comparison selector. This is standalone qualification, not a new baseline comparison or SLA. Nested progress phase fields were missing; completed counts and workload measurements remain intact |
| `artifacts/verification/read-progress-serialization/verification-summary.json`, build/test logs and TRX | Progress-only repair e0a93324 PASSED2/2: atomic on-disk serializer and actual1k runtime emitter,11 workloads/1100 samples; zero skips/warnings | Compiled old-shape negative control fails on missing `progress.cases`; original compile failure retained. Fullbe evidence remains immutable; no repeated full campaign is claimed |

The frozen sourcebe archive SHA-256 is
`be495d0414c299d2537aa12d39b0e9dc6d338ee08050e86040fe57d17756143b`.
Subsequent e0a93324 changes only progress-reporting test tooling. Product `src`, `db`, `contracts` and
`web` are unchanged frombe; five warmups/100 measurements, workload inputs, metrics and acceptance
criteria remain unchanged. Product version is unchanged. The final documentation update has its own
link/anchor checks; it does not relabel older test binaries as newly qualified.

The current A01–A09 functional coverage comes from the unchanged native inputs plus the complete current
frontend/browser suites. A10's omitted CLI details are covered by the current presenter tests and
source22's published journeys; A11's omitted row details and adjacent cancellation defect pass all three
current engines. A12 combines those scoped results with full current-product growth. The historical
matrices below identify the remaining governing cases by group; their older source attribution is retained.

The source35 query probe and three-pair comparison below remain historical common-path evidence. Since
that source, SQL/schema, growth plans, write cycles, protection and measured common-path inputs are
unchanged; `CaseDue.at` now shares an equivalent active/open predicate. This is semantic reuse, not
binary identity. Reminder DTO construction changed and has fresh current-product growth. The historical
31/31 comparison pass is not a new measurement of this binary. Exact native CI qualification is outstanding.

The original audit probes remain unchanged, including their failures. Maintained current-contract tests
provide separate positive evidence; adding a required wire member does not turn an old malformed probe
into a passing behavior check. Initial new-test defects (wrong wire-field lookup, bare boolean CLI flag,
Pending-default oracle and fixture stewardship) and lint failures remain retained.

The source34 full acceptance attempt passed Chromium212/Firefox190, then failed WebKit after154 passes
(one failed,35 not run) at an existing compound-retry readback. Source22's corrective attempt failed a
different canonical-route journey after22 passes (one failed,167 not run). Retained server excerpts
confirm `session`429 and `case.list`429 respectively, without identifying the exact limiting layer.
Independent negative controls established premature client-slot release and replacement dispatch before
drain. The subsequent sourcebe complete suites passed both paths without retry; the earlier failures
remain failures. Original private fixtures and safe `admission-failure-server-excerpts.json` are retained.

Manual visual review at source34 covered actual English320px and Arabic390px reminder rows, including
localized timing/basis and fitting actions. `current-visual-review.json` records those two frames.
Reminder presentation is unchanged atbe; its121 current frames and automated geometry controls are separate
fresh evidence, not a claim that every frame was manually reviewed.

## Historical source35 qualification

Historical local qualification is for `35b3c5639e593575e18de323d6dbe411a05f5853` on
`codex/narratives-reminders`, against baseline `b5e21690191482d6a5ea15cdccd7bb9ae5f31dc9`.
The ordinary full gate, complete Chromium/Firefox/WebKit suites, refreshed actual-source query probe,
offline CLI captures, full standalone growth and full three-pair comparison all passed. The comparison
passed all 31 unchanged budgets with ten matching common workload fingerprints; independent recomputation
also passed. Earlier complete failed campaigns remain failures in the retained record.

The physical-design correction is implemented: pinned `pg_trgm` 1.6 and full GIN search indexes preserve
literal search/authorization/erasure semantics. The subsequent owned-fixture reset correction is included
in this exact source. The subsequent verification-tooling refactors passed separate runtime QA and are integrated. The full
product results remain attributed to source35; product `src`, `db` and `web` inputs are unchanged by
those tooling changes. No cross-platform readiness or rollout is claimed.

The governing delivery is `claimcore-narratives-reminders-handoff-2026-11-02.tar.gz`. Its adjacent checksum
and extracted inventory passed: 1,201 files, including 1,171 baseline source files. Delivery identity does
not establish implementation quality. Live `AGENTS.md`/`AGENTS_EXTRA.md` and referenced handoff documents
were read. Neither instruction file was changed.

## Historical evidence and qualification scope

Paths below are relative to `artifacts/verification/narratives-reminders/` unless explicitly rooted
elsewhere. These ignored local outputs must accompany review; code-formatted paths intentionally do
not become clean-checkout documentation links. Private fixture directories can contain credentials,
authentication state and keys. Only allowlisted reports/loading measurements/synthetic screenshots were
copied; private fixture contents are not public attachments.

| Evidence | Actual result and source | Limit |
| --- | --- | --- |
| `delivery-verification.json`, `baseline-ci.json`, `published-releases.json` | Delivery verified; baseline CI observed green; published stable baseline v0.14.0 | Baseline observations do not qualify current source |
| `gate-35b3c563.log`, `gate-source-35b3c563.*`, `qualification-35b3c563-source.json`, `gate-35b3c563-reports/inventory.json` | Ordinary full gate passed: 805 native unit/architecture, 133 frontend files/663 tests, 274 PostgreSQL, 101 HTTP/server and 58 published acceptance; zero required skips | Local macOS 27.2 arm64 only; declared native CI platforms remain unrun |
| `gate-35b3c563-reports/native.sRKTlTPy/` | Complete Chromium 211, Firefox 189 and WebKit 189 passed; zero retries/skips; canonical summaries and six loading measurements retained. All four native-stage TRX retained | Actual service, IndexedDB, native and browser journeys; no accessibility certification claim |
| `connected-screens-35b3c563/inventory.json` | 121 safe current PNGs: English/Latvian/Arabic, RTL/themes/layout/maximum text; connected heading geometry controls pass. Earlier `maximum-text-visual-review/` and original/corrected controls retained | Screenshots/axe do not establish full WCAG certification; older visual inspection is identified separately |
| `baseline-design-capture/run-20261103T015108Z-jtta5vk9/{capture.json,CLEANUP-FOLLOWUP.json,VISUAL-REVIEW.json}`, `baseline-design-capture/run-20261103T015108Z-jtta5vk9/native/screens/` | Original b5 gallery passed 26 dimensions, 91 PNGs, 754 page loads and 780 axe/reflow checks; native 1/1, zero skips. Three representative English/light/dark and Arabic/narrow frames reviewed; baseline product/TS untouched | Gallery completion, not a new full 193-case/all-engine baseline gate. Original collector FAILED immediate transient-Ryuk postflight; separate later `CLEANUP_VERIFIED` evidence is retained |
| `cli-before-after/run-20261103T043611Z-v6v_9jke/capture.json`, per-command stdout/stderr | 11 actual commands passed with declared b5/current35 executables: help/describe and independent expected dry-run frames; authored file preserved; exact CRLF→LF Arabic/emoji summary; isolated XDG directories empty | Offline presentation/acquisition only. DLL/runtime/declared-source provenance limits remain; full gate supplies service/native/PTY evidence |
| `query-probe-35b3c563.log`, `query-probe-35b3c563-directory.txt`; external `query-count-probe/evidence/run.6UAC6HwQ/report.json` and raw bindings/plans/catalogs | 134 passing observations/118 distinct labels; 50 assigned cases, 1,050 Pending/1,000 Completed and distinct EUR25,000. Case pages/global queues use five commands at 1/50 rows; targeted pages seven; actual per-row negative control 3→101 rejected. Seven custom replays plus two naturally GENERIC executions | Exact current source/assembly evidence checked below; no 100k-case plan qualification, SLA or constant server-work claim. This run has no `AUDIT.json`; do not substitute old `run.UIP9W2Je` attribution |
| `growth-35b3c563-{summary.json,results.trx,log,directory.txt}`; raw `artifacts/verification/growth/b85a1c30a9584adb971d269490bf45db` | Full current growth PASSED in about 33m32s: 9/9 writes, 35/35 reads, cardinality 1/100/1000, fan-out 100/1000/10000 Pending plus equal Completed; native wrapper 1/1, zero failed/skipped; no diagnostic selector | Standalone current-source qualification, separate from comparison; native test duration 33m30.827s; no SLA |
| `comparison-35b3c563-summary.json`, `comparison-35b3c563-directory.txt`; raw `artifacts/verification/growth-compare-dbbad32d-5936-48a6-a7d0-23410d273a91/{comparison.json,comparison.md,AUDIT.json,AUDIT.md}` | Full three alternating reference→candidate pairs PASSED 31/31; all six native runs 1/1 Passed, zero other counters; ten common fingerprints match; cleanup passed. Independent audit reconstructed 87 raw workloads, checked six run states/93 fingerprint digests and recomputed all separate-median budgets | Same-host/session comparison, not an SLA or platform matrix. Three write profiles/six common reads/one cardinality profile per side; nine asymmetric feature keys are N/A, never comparable passes |
| `gate-8917f6b1-{failure-summary.json,postgres-failed.trx,log}` | Earlier GIN-source gate FAILED six of 274 PostgreSQL tests (268 passed): fresh-initializer fixtures dropped only `claimcore`, leaving database-wide `pg_trgm`, then hit 42710 before intended checks | Test setup defect corrected in35 by resetting owned schema+extension; strict production duplicate-object refusal and original assertions retained. Current274/274 passed independently |
| `comparison-f1019126-failure-readout.json`, `comparison-ff9c0cf0-failure-readout.json`, `comparison-e1ed05a9-failure-readout.json` and their raw directories | Three COMPLETE failed campaigns retained: f101 four common-page failures; ff9 seven/31; e1ed search p99 20.7815→29.4042ms, ratio1.41492. Six runs/fingerprints/cleanup completed in each | Genuine historical failures under unchanged ≥0.85 throughput/≤1.30 latency-verifier budgets; favorable diagnostics or later35 pass do not erase them |
| `growth-e1ed05a9-summary.json`, `corrective-full-growth-summary.json`, associated logs/TRX/raw directories | Earlier e1ed/ff9 full standalone growth passed 9/35 plus cardinality and high fan-out | Historical evidence; current35 growth passed independently |
| `common-read-cardinality-b6ba/`, `artifacts/verification/{named-row,global-grants-narrow-page,direct-search-fields}/summary.md`, `fixed-query-prepare/` | Scoped four-fingerprint 100k diagnostic and focused pure/PG/memory/provider controls passed; mapper/root verification, narrow financial pages, global joins, typed search and two selectively prepared fixed reads exercised | Overlapping focused scopes, not additive full-suite discovery or replacement for full comparison |
| `search-index-write-e1ed/{readout.json,pair-readout.json,SOURCE-IDENTITY.json}`, `search-index-read-e1ed/{readout.json,SOURCE-IDENTITY.json}` | Actual pre-adoption diagnostics: three write pairs gave median throughput1.024/p950.996/p991.059; WAL +32.43%. Actual100k GIN search diagnostic used stock five warmups/100 requests, p99 3.437ms; BitmapOr/exact indexes, one heap row, pending-list488/35pages retained unflushed | Scoped physical-design diagnostics, not full budgets; full correction and separate35 gate/growth/comparison now implemented and passed. WAL increase remains an operator consequence |
| Rejected operator diagnostics, including `search-operator-regex-e1ed/` | `strpos` selected wrong/slower plan; `COLLATE C` gave no gain; quoted regex about2.8× slower | Rejected without product changes or relaxed assertions/budgets |
| `final-integrated-fast.log`, `final-integrated-fast-summary.json`, `final-integrated-fast-unit.trx` | Final integrated lint/unit/frontend gate passed: 805 native unit/architecture, 133 frontend files/663 tests, retention and both Python suites. The current verification report and cumulative changelog were included | Reuses unchanged product35 full runtime/growth/comparison inputs; does not relabel this fast gate as a full runtime or platform gate |
| `read-measurement-evidence-refactor/CHECKS.json`, `verification-prerequisites-refactor/CHECKS.json` | Integrated tooling QA: Release build, read harness native1/1 with11 workloads/1100 raw samples; Python21+driver4; retention; owner3, acceptance lifecycle4, publication/discovery10, documentation3; formatting/lint. Zero native skips. Retained old passing/failing comparison verdicts and metric values are identical | Observational test/driver/report changes, not a repeated full product campaign. New publish control discovery was checked explicitly; initial typing/format/prerequisite failures remain retained |
| `musl-unit.log`, retained TRX, `musl-qualification-readout.json` | Old pinned Alpine3.24/.NET10.0.403 linux-musl-x64 under Rosetta: 804 discovered, 799 passed, five failed, zero skips | FAILED supplemental emulation run; selected diagnosis does not establish native CI qualification |

Exact source35 archive SHA-256:
`6fed81592fa6b75516f402b1e43275129b26476f3473cb16141ed0b27f1e1d41`.
Host: macOS27.2 arm64, .NET SDK10.0.403/runtime10.0.14, Node26.12.1/npm11.21.1, locked browsers
and pinned PostgreSQL18.6. Existing repository declarations own pins. Source was frozen for definitive
runs. Product version remains unchanged.

Current query evidence is specifically external
`/Users/erst/tools/claimcore-handoff-execution-2026-11-02/query-count-probe/evidence/run.6UAC6HwQ/`.
`source-commit.txt`/`compiled-source-commit-per-root.txt` name35; six paired backend source-tree entries
match; before/after source status is empty and root Release DLL hashes are unchanged. `report.json`
records five loaded product assemblies with hash/MVID plus Npgsql. ClaimCore.Json's copied/reference
DLL hash matches; no separate loaded Json location/MVID is claimed.

The two natural prepared plans used the held backend **PID 95**, `plan_cache_mode=auto`, after eight
production warm calls under repeatable read. `global-read-facts` (`_p1`) generic counters increase8→9,
custom remains5, and returns two rows; `work-summaries` (`_p2`) generic increases9→10, custom remains5,
and returns50. These are actual current-run observations, distinct from old UIP9/PID 94. Exact identities,
SQL, types and catalog deltas are in each `*-prepared-natural-identity-and-bindings.json` and companion
before/after files. Npgsql renders only the first ten UUIDs plus ellipsis; independent full50-parent
Guid-array/typed-literal replay and all50 business-value assertions supplement that rendering. Full
interception of the actual bound array or a stored full-response capture is not claimed. Fixed command
counts do not imply fixed total database work as reminder populations grow.

The full comparison uses ratio of the three candidate observations' median to the three reference
observations' median, separately for every metric. Required throughput ≥0.85 and p95/p99/verifier ≤1.30
are unchanged. Current search p99 is25.5641→2.9144ms (ratio0.114004); worst latency ratio is1.11930
(global authority p99), worst verifier1.08191, minimum read throughput0.985014 and minimum write
throughput1.00561. Unmatched keys are exactly two historical authored-follow-up/summary profiles plus
seven reminder-derived/queue/current/financial/overview profiles; different seed semantics remain
separate. The independent audit used no comparison-engine imports. Cleanup is recorded passed with
no errors; the root separately observed no remaining containers. It does not qualify all unrelated
host activity. Later tooling changes have their own focused runtime QA below.

## Work-package matrix

All twelve product work packages were implemented at source35, with the two client omissions corrected
as described above. The following matrix records historical exact35 evidence.
Subsequent verification tooling is integrated with separate focused runtime QA; owned temporary
worktrees were removed after preserving evidence. Native CI limits remain explicit.

| Package | Delivered responsibility | Historical source35 status and evidence |
| --- | --- | --- |
| WP01 Baseline/integration map | Delivery/source/CI/releases, boundaries and before captures | Verified delivery; actual b5/current35 CLI captures; original gallery26/91 completed with failed immediate collector and separate cleanup follow-up retained |
| WP02 Text/field contracts | Owned scalar/LF/permanent-byte types, optional registration/partial semantics, generated constraints | Complete35 native/PG/browser/PTY gate passed boundaries, keep/set/clear, financial safeguards and exact output |
| WP03 Independent case work | Revision-zero bounded Work root; summary/responsibility independent of financial state/revisions | Complete35 gate passed root reconstruction, independent revision and actual-state policy controls |
| WP04 Reminder/atomic actions | Typed lifecycle/compound acts; head-locked revision/lifecycle policy | Complete35 rule/race/replay/PG rollback controls and full populated growth passed |
| WP05 Durable effects/recovery | Bounded records/formats, causal identities, compound effects, receipts and journals | Literal-byte/corruption/ownership/recovery controls passed in35 native/PG/all engines |
| WP06 PostgreSQL integrity lifecycle | Fresh schema, scoped related erasure, verified rebuild/restore and memory mirror | Complete35 storage/root/canonical-byte/precedence/rollback/restore/isolation controls and full growth passed; six earlier owned-reset failures retained |
| WP07 Queries/attention/efficiency | Scoped dates/coverage/queues/activity, narrow financial pages, bounded work batches and GIN literal search | Complete35 gate, refreshed134/118-label query probe, full9/35 growth and full31/31 comparison passed; historical failed campaigns and WAL cost retained |
| WP08 CLI/shell workflows | Revisions/reviews, file/stdin/editor, paging, inert/exact output and scoped recovery | Complete35 actual service/native/PTY controls and11-command b5/current35 offline captures passed |
| WP09 Connected web workflows | Narratives/work/reminders in existing themes/routes; ownership, truthful state, admission and focus | Complete35 Chromium211/Firefox189/WebKit189, safe121PNG inventory/loading and geometry controls passed |
| WP10 Independent contracts/privacy | Authored oracles, wrong identities/effects, races/rollback/erasure and late callbacks | Complete35 native/PG/client controls and actual query-amplification negative control passed |
| WP11 Growth/setup/current docs | Populated growth, guides/examples/generated/help, release consequences and selective preparation | Product35 full gate/growth/comparison passed. Integrated measurement/progress/retention/publish-input/contributor-guide refactors passed focused runtime QA; criteria unchanged |
| WP12 Delivered-state closure | Local commits/archives, safe reports, per-package/acceptance outcomes and retained failures | Exact35 archive, current matrices and independent audit retained; tooling QA integrated and owned temporary worktrees cleaned with disposition proofs. Exact native CI-platform qualification remains unrun |

## Acceptance matrix

| Group | Exercised historical source35 functional evidence | Limit or outstanding qualification |
| --- | --- | --- |
| A01 Narrative/optional semantics | Complete35 native/PG/browser/PTY: scalar/LF/Unicode/control admission, permanent bytes, keep/set/clear, seven facts, paid-description correction and exact output | Local platform; no accessibility certification |
| A02 Reminder lifecycle | Domain/store/CLI/browser creation IDs/revisions, terminal/no-op rules, inactive retention/reactivation, closed cleanup, replay/bounded pages; current populated growth passed | Local platform scope |
| A03 Concurrency/atomicity | Memory+PG races, distinct/same revisions, financial preview, close/erase/assignment/revoke, second-effect rollback; all-engine identical-byte compound retry creates one replacement | Local platform scope |
| A04 Coverage/scope/business dates | Kiritimati/Pago_Pago/Riga compare observed instants with PG AT TIME ZONE; Mine/access/distinct union/no date-ledger writes; focus+60s timer refreshes server Nov2→Nov3 with client2031 and dependent reads | DST-observing zone covered; actual DST-transition instant not exercised |
| A05 Queries/search/financial isolation | Complete35 extreme dates/keysets/current search/derived ownership/distinct money; query134/118 labels, seven custom/two actual natural plans; full growth and31/31 comparison passed | Command counts do not bound total server work; probe is50cases,100k plan diagnostics separate; WAL+32.43%; historical failures retained |
| A06 Durable records/history/vocabulary | Literal command/snapshot/effect/header bytes; root/creation/causal identity, wrong parent/due/max revision, typed activity/current-prior disclosure and generated/publish inputs passed | Local platform scope |
| A07 Complete erasure | Related rows/payloads/nonces/tombstones inspected; all reminder states/compound content cleared, reserved IDs, identical other case preserved, races/all-engine cache/recovery passed | Local platform scope |
| A08 Rebuild/restore/owner operations | Maintenance/checkpoint/head exclusion, FK reconstruction, rollback, derived repair, old-backup refusal, accepted-erasure reconciliation and process checks passed; current growth passed | Three exact native CI environments unrun |
| A09 Delivery/recovery/ownership | Native publication-before-send/private journals; all-engine IndexedDB scopes/attempts/tab loss/old formats/new families; exact compound retry avoids duplication | No real old journals converted/deleted; native CI platforms unrun |
| A10 CLI/shell UX | Complete35 service/native/PTY discovery/lifecycle/paging/revisions/file/stdin/editor/inert JSON/NDJSON/CSV;11 b5/current35 offline commands passed | Offline capture supplements service/PTY evidence; declared DLL source alone is not provenance; native CI platforms unrun |
| A11 Web/interaction UX | Complete35 engines211/189/189: permissions/recovery/uncertainty/financial safety, languages/RTL/themes/narrow/zoom/reduced motion/focus;121safe PNGs | Baseline26/91 gallery separately complete; no WCAG certification |
| A12 Integration/growth | Complete35 gate805/663/274/101/58, all engines, query134/118, CLI11,9/35 standalone growth,31/31 full comparison and exact-source archive passed | Product35 qualification plus focused integrated-tooling QA passed; evidence retained and owned worktrees cleaned. Three native CI environments remain unrun; no rollout/readiness claim |

## Preserved failures and practical limits

Three complete failed comparisons remain retained under their original sources and reports. f101 failed
four common-page budgets: filtered throughput0.768; notified throughput0.714/p951.347/p991.439.
ff9 failed seven/31: filtered throughput0.656/p951.568/p991.775; notified
throughput0.594/p951.740/p991.807; cardinality verifier1.312. e1ed passed30/31 and failed search
p99: reference20.7815ms/candidate29.4042ms, ratio1.41492. All six runs, common fingerprints and cleanup
completed in each campaign. None is reclassified as passing or an outlier. The later35 correction
passed its own complete unchanged-budget campaign, not a reduced rerun or threshold waiver.

The 8917 GIN-source full gate stopped in PostgreSQL with six fresh-initializer fixture failures:
`claimcore` was dropped while database-wide `pg_trgm` survived; 42710 occurred before the intended
commit-boundary controls. Source35 resets only newly owned fixtures' schema and extension together;
production strict duplicate-object refusal/commit semantics and the tests' intended assertions remain.
The failed log/TRX and exact failed source archive are preserved. Current35 independently passed274/274.
Earlier b6b stopped at formatting in three verification files before tests; e1ed formatter wrapping and
a fresh complete gate resolved that checkpoint without hiding its failed log.

Other material native/browser failures and their corrections remain in original logs and the earlier
report: independent-revision fixtures, bounded diagnostic setup, body-lifetime read admission, focus
transfer, accepted-readback synchronization and independently checked zoom geometry. Initial iterative
diagnostics were not all archived as full source patches; later exact-source archives identify stronger
evidence. The initial query parser failure did not save its returned raw plan; subsequent actual plans
are retained. Seven custom replays are not natural warm-plan captures; only the two specifically
identified current prepared executions have actual PID/catalog/mode evidence.

The original baseline attempt kept46 frames before a120-second smoke-wrapper cutoff. QA later ran
the unchanged26-dimension gallery within its normal20-minute bound, adding45 missing frames, once
without retry; no new full baseline suite/assertion was imposed. An isolated-XDG/mise preflight was
corrected with actual pinned Node/npm paths before browser execution. The completed matrix passed,
but its collector immediately saw one Ryuk sidecar and reported FAILED. That original report remains
unchanged. Exact Docker create/destroy events tie the reaper/Keycloak/PostgreSQL containers to the
owned session and show natural removal; eight preexisting containers were unchanged. Separate
`CLEANUP-FOLLOWUP.json` records `CLEANUP_VERIFIED`, no remaining scoped processes/containers and no
broad prune/manual deletion. Three representative frames were visually reviewed, not every91 frame.

Pre-adoption physical-design diagnostics measured favorable read/write ratios and WAL+32.43%; they
remain scoped evidence and the WAL cost is retained. Rejected operators changed no product code.
Old authored-follow-up versus new reminder-derived workloads keep distinct seed semantics/keys.
The six common reads, fingerprint requirements, stock five warmups/100 measured reads and original
0.85/1.30 budgets were not reduced to obtain35's full pass.

The old supplemental Rosetta musl result remains FAILED799/804. Four watchdogs passed separately
with original compiled assertions/deadlines; parallel emulation pressure remains an inference. The
fifth failed an empty-HOME precondition: independent `/bin/true` and `dotnet --info` controls each
created `.cache/rosetta` without ClaimCore. Selected passes do not establish native qualification.

All three declared native CI environments remain unrun: Ubuntu24.04 `linux-x64` and `linux-musl-x64`,
and macOS15 `osx-arm64`. Local macOS27.2/Rosetta/Docker do not substitute for those platforms. No push,
merge, tag, release, version bump, deployment, migration or real-installation/data operation is authorized
by this report. Product35 results are separate from the later verification-tooling runtime qualification described above.

## Delivered-state boundaries

Before the two audit corrections above, the product/runtime, fresh database baseline and frontend inputs
were unchanged from source35. Those verification refactors add raw samples after the original timing boundary, incremental
completed read reports, small atomic progress, informational variability and conservative failure
retention. They preserve the five warmups/100 measured reads, fingerprints, workloads, alternating
pair order, ratio-of-separate-medians gates and 0.85/1.30 thresholds. Separate focused runtime QA
covers their actual discovery, publication, retention and prerequisite paths; no new full-product
performance campaign is claimed for those observational changes. The audit correction does change
current reminder DTO construction and presentation; its current-source qualification is reported above.

The contributor verification guide owns the updated workflow and evidence-scope instructions.
`AGENTS_EXTRA.md` already links that guide and remains unchanged; the protected `AGENTS.md` baseline
also remains unchanged. Exact native CI-platform qualification is still outstanding as described above.

Owned-resource disposition evidence is under `cleanup/`: ten old task-created worktrees and two
completed tooling worktrees were removed after exact source/status checks. Three dirty task worktrees
were independently proven to contain already-integrated patches or the exact archived executed
baseline-capture harness. Branch references, local commits, raw results and useful private failure
fixtures remain; shared caches, preexisting containers and unrelated resources were not pruned.

The three subsequent reminder-audit worktrees were clean and removed after their commits were
integrated with reviewed corrections. Their branch references, zero-diff disposition inventory and
original commits remain under the audit evidence directory. Only task-owned worktrees were removed.
