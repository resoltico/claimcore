# Narratives and reminders verification

This review record covers the local replacement of the single editable follow-up with independent
case work and reminders, including optional incident description and Handling summary. The governing
handoff is `claimcore-narratives-reminders-handoff-2026-11-02.tar.gz`; baseline source is
`b5e21690191482d6a5ea15cdccd7bb9ae5f31dc9`. The adjacent checksum and extracted delivery inventory
passed: 1201 files, including 1171 baseline source files. This establishes delivery identity, not
implementation correctness.

Qualified local product-source checkpoint: `f1019126ebb5c8fde9af3b3b69517f6e5406563c` on
`codex/narratives-reminders`. The ordinary full gate and full standalone growth passed. The controlled
three-pair comparison is incomplete: five raw runs finished and the last candidate run is underway.
No comparison budget result is established. Concrete before/after offline CLI capture remains pending.
Declared native CI-platform jobs remain unrun. This document does not claim readiness across those
platforms or authorize rollout.

## Evidence and qualification scope

Evidence filenames below denote local outputs under `artifacts/verification/narratives-reminders/`,
unless stated otherwise. They are intentionally excluded from source control; preserve them alongside
this review. The external query readout is under the extracted handoff execution directory, at
`query-count-probe/evidence/run.CGbP1gyR/READOUT.md`. Do not copy whole private acceptance fixtures:
they contain synthetic authentication state and private keys as well as useful failure evidence.

| Evidence | Source and actual result | Qualification limit |
| --- | --- | --- |
| `delivery-verification.json`, `baseline-ci.json`, `published-releases.json` | Delivery verified; baseline CI observed green; actual latest published stable baseline was v0.14.0 | Baseline observations do not qualify the replacement |
| `full-gate-foreground.log`, `full-gate-foreground-source.*`, `foreground-native-reports/` | Full ordinary gate at f1019126 passed: 804 native unit/architecture, 133 frontend files/663 tests, 262 actual PostgreSQL, 101 HTTP/server and 58 published acceptance tests; zero required skips | Local macOS27.2 arm64 qualification, not the declared CI runner matrix |
| `foreground-browser-chromium.json`, `foreground-browser-firefox.json`, `foreground-browser-webkit.json` | Complete supported suites passed: Chromium211, Firefox189, WebKit189; zero retries/skips. Real service/IndexedDB and native/published journeys included | Automated supported-engine evidence, not accessibility certification |
| `full-growth-summary.json`, `full-growth.log`, `full-growth-results.trx`, `full-growth-directory.txt` | Full standalone f101 growth passed in35m05s: 9/9 write profiles, 35/35 read profiles, cardinality1/100/1000; high fan-out100/1000/10000 pending plus equal completed population. Native wrapper1/1, zero skips; no diagnostic selector | Raw report directory: `artifacts/verification/growth/e65eecf43ef0408f860c4e504cad438e`. Standalone observations are not baseline ratios or an SLA |
| `maximum-text-visual-review/`, `foreground-connected-screens/`, `reused-input-tree-checks.json` | Connected English/Latvian/Arabic maximum-text/layout captures reviewed; original/corrected heading geometry retained. Final all-engine tests include usable-heading-width assertions. Earlier reviewed web source tree matches f101 | Before web capture is partial; visual/automated checks do not establish full WCAG certification |
| `independent-oracles-and-races.log`, `focused-postgres-reminders.log`, `protocol-cli/isolated-checks/evidence.json` | Selected26 native/46 PG controls,42 typed-wire properties and7 actual process/PTY input controls passed; their maintained replacement tests also run in the full gate | Earlier focused scopes complement definitive integrated results; they are not substitutes |
| External query-count/plan readout; `reused-input-tree-checks.json` | 105 actual production-backend/PG checks:50 assigned cases,1050 pending/1000 completed, exact distinct EUR25000. Case pages8 commands at1/50 rows; global queue7; targeted pages9. Actual per-row negative control4→102 rejected | Six exact SQL bindings/plans retained; targeted queues chose sequential scan/top-N at that population. Relevant backend/schema trees match f101. No100k-case plan or constant server-work claim |
| `performance-preflight.json`, `full-growth-comparison.log`, `full-growth-comparison-directory.txt` | Changed feature workloads separated before measurement; unchanged read keys and regression budgets preserved. Three alternating pairs requested; final raw run incomplete | No comparison budget pass/failure can yet be claimed |
| `musl-unit.log`, retained TRX, `musl-qualification-readout.json` | Supplemental pinned Alpine3.24/.NET10.0.403 linux-musl-x64 under Rosetta:804 discovered,799 passed,5 failed, zero skips | FAILED; not native CI qualification. Selected diagnosis does not supersede the failed full run |

The full-gate source archive SHA-256 is
`be9336aba8058dce817b81c67e63211f20c528e4d91e605cbc500036ff679af3`.
The local host is macOS27.2 arm64 with .NET SDK10.0.403, Node26.12.1/npm11.21.1, PostgreSQL18.6 in
owned synthetic containers and pinned browsers. Exact environment and loaded DLL identities for the
query probe remain with that probe. Existing declarations remain authoritative for toolchain/package/
image pins. No product version or instruction baseline was changed.

## Work-package matrix

Every package is implemented. The table separates applicable passing local evidence from remaining
comparison/capture work and the external platform limits below. WP12 local closure is still open.

| Package | Implemented source / representative commits | Current evidence and outstanding work |
| --- | --- | --- |
| WP01 Baseline and integration map | Clean b5e2169 audited; delivery/source inventory, CI/releases, producer/consumer inspection and before captures | Delivery passed; before web capture is partial. Offline baseline/current CLI capture pending |
| WP02 Text and field contracts | `10153f9c`, `b35c3adc`: owned scalar/LF/byte profiles, optional registration/partial semantics and generated metadata | Full native/frontend/service/browser gate passed, including boundaries, permanent bytes and process input |
| WP03 Case-work ownership | `735613cc`, `7832fc15`: bounded Work/revision-zero root; summary/responsibility independent of financial state | Full gate passed independent revision/root/current-policy controls |
| WP04 Reminder lifecycle/atomic actions | `7832fc15`, `ab8481cb`: typed lifecycle/compound decisions and head-locked execution | Full gate passed stale revisions, races, actual PG second-effect rollback and replay; full high-fan-out growth passed |
| WP05 Durable effects/recovery | `6d52c1db`, `d660b750`, `d6f79210`: bounded records, explicit formats, causal metadata, correlated receipts and strict journals | Full gate passed independent literal/corruption controls and actual all-engine/native recovery |
| WP06 PostgreSQL integrity lifecycle | `de33bd7c`, `0c2c152b`, `d4694914`, `425e3293`: fresh schema, related erasure, verified rebuild/restore and memory mirror | Full PG/native gate and growth passed storage bytes/rollback/rebuild/restore/isolation; external platforms unrun |
| WP07 Queries/attention/read efficiency | `fd70d46e`, `1df0828b`, `56f265e5`, `13172ef8`: scoped queues/overview/activity, derived date/search and batched account reads | Full gate/growth and query-count controls passed; controlled unchanged-read comparison pending |
| WP08 CLI/shell workflows | `bdd540e4`, `000e9c06`, `fcaf7c5b`, `d359f5c2`: correct revisions/reviews/paging, file/stdin/editor, safe output and identity recovery | Published service/native/PTY/private-file controls passed; concrete offline before/after capture pending |
| WP09 Connected web workflows | `45b8f5a0`, `a6b13518`, `ba2d15aa`, `09c1108e`, `9108f968`: current theme, Work/Reminder UI, scoped ownership, read admission and focus | Complete final three-engine suites and corrected zoom assertions passed; reviewed safe captures retained |
| WP10 Independent contract/privacy checks | `163f2efd`, `d6f79210`, `ab8481cb`, `0734fd62`: literal oracles, wrong effects, races, rollback, erasure and ownership controls | Definitive full native/PG/client gate passed; actual query amplification negative control retained |
| WP11 Growth/setup/current docs | `7fc8400e`, `996534e4`, `dec84e5a`: populated growth, guides/examples/generated contracts/help and Unreleased consequences | Full tooling/build/publish-input checks and full standalone growth passed; comparison pending |
| WP12 Delivered-state closure | Local commits, exact-source archives, safe reports and per-WP/per-A matrices | Full gate/growth passed; comparison, offline CLI capture, final evidence consolidation and owned-resource cleanup pending; external limits explicit |

## Acceptance matrix

| Group | Specific implemented behavior and passing local evidence | Limits or outstanding evidence |
| --- | --- | --- |
| A01 Text/optional semantics | Full native/PG/browser/PTY gate covers scalar/LF/control rules, permanent decoding, keep/set/clear, required facts, paid description correction and exact output; all-language maximum-text/zoom assertions passed | No accessibility certification claim |
| A02 Reminder model/lifecycle | Full domain/store/CLI/browser gate covers creation identity/revision, terminal/no-op rules, inactive retention/reactivation, closed cleanup and replay; full growth accepted high pending/completed populations | Local platform scope |
| A03 Concurrency/atomicity | Shared memory+actual PG races cover distinct/same revisions, fact preview independence, close/erase/assignment/revoke. Injected PG trigger proves rollback. All-engine lost-ack retry checks identical bytes and one replacement identity | Local platform scope |
| A04 Scope/date/coverage | PG Kiritimati/Pago_Pago/Riga compare each get/list/summary observation with PG AT TIME ZONE; scope/Mine/distinct attention/no date-ledger writes checked. Focus and60s timer controls refresh server Nov2→Nov3 with client2031 and dependent TODAY→OVERDUE reads | Tests cover a DST-observing zone, not an actual DST-transition instant |
| A05 Query/search/financial isolation | PG keysets/extreme dates/current search/derived ownership, distinct money and SQL command-count controls passed. Full growth passed100k cases and10000 pending+10000 completed on one parent with independent identities/counts/money | Controlled unchanged-read latency/throughput comparison pending; diagnostic plans are smaller-population observations |
| A06 Records/history/vocabulary | Full gate passed literal commands/snapshots/effects/header bytes, root/creation/causal relations, invalid parent/due/max revision, typed activity/current-vs-prior disclosure and generated/publish-input checks | Local platform scope |
| A07 Complete erasure | Actual related rows/payloads/nonces/tombstones inspected; all reminder states/compound content cleared, identities reserved, identical other case preserved and erase races checked. Client cache/recovery suites passed all engines | Local platform scope |
| A08 Rebuild/restore/owner operations | Full PG/native gate passed maintenance/checkpoint/head exclusion, FK reconstruction, rollback, projection repair, old-backup refusal, accepted-erasure reconciliation and owner/process behavior; full growth passed | Declared native CI platforms unrun |
| A09 Delivery/recovery/ownership | Actual native publication-before-send/private journals and all-engine IndexedDB attempts/foreign scopes/tab loss/old formats/WORK+REMINDER identities passed; exact compound retry does not duplicate | No old real journals converted or deleted; declared native CI platforms unrun |
| A10 CLI/shell | Full native/service/PTY gate passed generated discovery, lifecycle/paging/revisions, file/stdin/editor cleanup refusal, inert text and exact JSON/NDJSON/CSV | Concrete offline before/after capture pending; declared native CI platforms unrun |
| A11 Web/interaction | Complete final Chromium211/Firefox189/WebKit189 suites cover permissions, recovery, uncertainty, financial safety and work; language/RTL/theme/narrow/zoom/reduced-motion/focus checks and reviewed captures retained | Before captures partial; no WCAG certification claim |
| A12 Integration/growth | Full ordinary gate and full9-write/35-read standalone growth passed on f101; generated/schema/format/source/publish consumers, unchanged version/instructions and exact source evidence retained | Comparison, offline CLI capture, final evidence/cleanup and exact native-platform limits remain open |

## Corrections and retained limitations

Failed gates and partial browser runs remain in their original logs; the artifact acceptance matrix
records their source-specific diagnosis. Corrections preserve assertions and budgets: fixtures now
bind actual independent revisions/projection owners; diagnostic cluster setup respects worker bounds;
browser reads queue within unchanged server admission through body decoding; action replacement
transfers focus; fixture writes await accepted readback. Visual review detected unreadable zoom
wrapping despite no horizontal overflow; corrected usable-heading geometry now passes connected tests.
Later successful gates do not erase those failed observations.

Initial iterative diagnostics were not all archived as complete source patches. Later source archives
and commits identify stronger evidence. The query probe retains prerequisite/parser failures and final
raw plans; its initially failed raw plan was not saved before the parser exception. Before web captures
are partial, and a concrete baseline CLI transcript is not yet retained.

Old authored-follow-up workloads and new reminder-derived workloads have different seed semantics and
fingerprints. They were separated before measurements; unmatched feature observations cannot claim
comparable budget passes. The six unchanged read keys and0.85 throughput/1.30 latency-and-verification
ratio budgets remain intact. Full standalone growth passed; three alternating full same-host comparison
pairs against b5e2169 remain incomplete. No reduced diagnostic substitutes for those results.

The supplemental Rosetta musl run remains failed799/804. Four watchdog cases passed separately with
the original compiled assertions/deadlines; parallel emulation pressure is an inference. The fifth
failed the empty-HOME precondition before the shell scenario. Independent `/bin/true` and `dotnet --info`
controls each created `.cache/rosetta` without a ClaimCore operation. No assertion or deadline was
changed, and selected passes do not establish full or native qualification.

The declared native CI matrix is Ubuntu24.04 `linux-x64`/`linux-musl-x64` and macOS15 `osx-arm64`.
All three new-source CI environments remain unrun: local macOS27.2 and Rosetta/Docker are distinct.
No push is authorized. No push, merge, tag, release, product-version bump, deployment, migration or
real-installation/data operation is part of this delivery. AGENTS.md and AGENTS_EXTRA.md remain
unchanged; no supplement change is proposed.
