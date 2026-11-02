# Narratives and reminders verification

This review record covers the local replacement of the single editable follow-up with independent
case work and reminders, including optional incident description and Handling summary. The governing
handoff is `claimcore-narratives-reminders-handoff-2026-11-02.tar.gz`; baseline source is
`b5e21690191482d6a5ea15cdccd7bb9ae5f31dc9`. The archive's adjacent checksum and extracted delivery
inventory passed. The delivery contains 1201 files, including 1171 baseline source files; this proves
delivery identity, not implementation correctness.

Product-source checkpoint: `2f276f43549b13a668cc704629ca8324ece1bb0a` on local branch
`codex/narratives-reminders`. Its ordinary gate is still running. Full current-source growth and the
full three-pair comparison remain pending. This document does not claim readiness across the required
platforms or authorize rollout.

## Evidence and qualification scope

Evidence paths below are local outputs under
[`artifacts/verification/narratives-reminders/`](../../artifacts/verification/narratives-reminders/),
unless stated otherwise. They are intentionally excluded from the source checkout. Preserve the
referenced reports alongside this review. Do not copy whole private acceptance fixtures: they contain
synthetic authentication state and private keys as well as useful failure evidence.

| Evidence | Source and actual result | Qualification limit |
| --- | --- | --- |
| `delivery-verification.json`, `baseline-ci.json`, `published-releases.json` | Delivery verified; baseline CI observed green; actual latest published stable baseline was v0.14.0 | Historical baseline evidence does not qualify the replacement |
| `full-gate-sixth.log`, `full-gate-source-bounded.tar` | At `0509b9a3`: 804 native unit/architecture, 656 frontend, 262 PostgreSQL and 101 HTTP/server tests passed; zero skips. Published acceptance: 57 passed, 1 failed, zero skips | Ordinary gate failed in the browser wrapper; later browser product changes require a fresh gate |
| `connected-chromium-fourth.log`, `chromium-f114d384-report.json`, `connected-source-f114d384.tar.sha256` | Complete Chromium at `f114d384`: 211/211 passed, zero retries/skips | Precedes the zoom-heading correction and timer regression; Firefox/WebKit and final-source integration remain pending |
| `work-day-timer-unit-final.log` | Six WorkOverview tests passed, including the added 60-second server-day/dependent-read control, integrated as `d7bf472a` | Focused unit evidence, not a browser or full-gate substitute |
| `maximum-text-visual-review/REVIEW.json`, `heading-control.json` and safe screenshots | Connected maximum text captured in English/Latvian/Arabic, three layouts; visual review found heading compression. Actual-CSS original/corrected geometry control verifies `3b552d47`; connected geometry assertions added | Earlier no-overflow success did not establish readability; final connected corrected-layout qualification remains pending. No accessibility certification claimed |
| `independent-oracles-and-races.log`, `focused-postgres-reminders.log` | 26 selected native controls and 46 selected actual PostgreSQL controls passed, zero skips; subsequently included in the sixth gate | Focused scopes and exact earlier source, not final delivered-state qualification |
| `protocol-cli/isolated-checks/evidence.json` | 42 typed-wire properties and seven actual process/PTY input controls passed; generator/output checks passed. Published native retained-text runtime passed in the sixth gate | Isolated producer/client checks complement real service/native acceptance |
| [Query-count and plan readout](/Users/erst/tools/claimcore-handoff-execution-2026-11-02/query-count-probe/evidence/run.CGbP1gyR/READOUT.md) | 105 actual production-backend/PostgreSQL checks passed: 50 assigned cases, 1050 pending/1000 completed reminders, distinct EUR25000. Case pages: 8 commands at 1/50 rows; global queue: 7; targeted pages: 9. Actual per-row negative control: 4→102 rejected | Backend compiled source `0509b9a3` matches the reviewed backend trees. Six exact production bindings/plans retained; targeted queues chose sequential scan/top-N at this population. Not full growth or an SLA |
| `performance-preflight.json` | Changed reminder-derived feature workloads separated before measurement; six common read keys and regression budgets unchanged | Full growth and full comparison have not yet produced qualification results |
| `full-gate-delivered.log`, `full-gate-delivered-source.commit`, `full-gate-delivered-source.tar.sha256` | Fresh ordinary gate at `2f276f43` in progress; source archive SHA-256 `499fb28471c95baaa6a8decab4da7cf1734e5821db6d28e12e1b5d0d474000dd` | Pending outcome; do not infer success from a running stage |

The host is macOS arm64 with .NET SDK 10.0.403, Node 26.12.1/npm 11.21.1, PostgreSQL 18.6 in
owned synthetic containers, and the pinned installed browsers. Actual environment and loaded DLL
identities for the query probe are retained with that probe. Source/toolchain and package/image pins
remain authoritative in their existing declarations.

## Work-package matrix

“Implemented” identifies the replacement source and consumers. Qualification limits in the evidence
table apply to every row; WP12 remains open until the definitive stable-source checks finish.

| Package | Implemented source / representative commits | Evidence and outstanding work |
| --- | --- | --- |
| WP01 Baseline and integration map | Clean b5e2169 baseline audited; delivery/source inventory, CI/releases, producer/consumer inspection and before captures retained | Delivery passed; before browser capture is partial. Final evidence/source index pending |
| WP02 Text and field contracts | `10153f9c`, `b35c3adc`: domain-specific scalar/LF/byte profiles, optional registration/partial semantics, core/generated metadata | Native boundaries, permanent bytes and process input controls passed; final gate pending |
| WP03 Case-work ownership | `735613cc`, `7832fc15`: bounded Work/revision-zero root; summary/responsibility outside financial CaseAct/snapshots | Actual independent revision/policy tests passed; final gate pending |
| WP04 Reminder lifecycle/atomic actions | `7832fc15`, `ab8481cb`: typed create/update/complete/cancel/reopen/compound decisions and head-locked execution | Native/PG lifecycle, stale revisions, races, injected second-effect rollback and replay passed; final engines pending |
| WP05 Durable effects/recovery | `6d52c1db`, `d660b750`, `d6f79210`: bounded records, explicit formats, causal metadata, correlated receipts and strict journals | Independent literal bytes/corruption controls and complete Chromium recovery passed; final all-engine gate pending |
| WP06 PostgreSQL integrity lifecycle | `de33bd7c`, `0c2c152b`, `d4694914`, `425e3293`: fresh schema, related-subject erasure, verified rebuild/restore and memory mirror | Actual PG stored bytes/rollback/restore and other-case isolation passed; final source/native Linux qualification pending |
| WP07 Queries/attention/read efficiency | `fd70d46e`, `1df0828b`, `56f265e5`, `13172ef8`: scoped queues/overview/activity, derived date/search and page-oriented account reads | PG scope/date/money/keyset and query-count/plan controls passed; full high-population growth/comparison pending |
| WP08 CLI/shell workflows | `bdd540e4`, `000e9c06`, `fcaf7c5b`, `d359f5c2`: entity revisions, review/paging, safe file/stdin/editor, terminal/machine output, identity recovery | Published native service/PTY and private-file failure controls passed; final gate/native Linux qualification pending |
| WP09 Connected web workflows | `45b8f5a0`, `a6b13518`, `ba2d15aa`, `09c1108e`, `9108f968`: current theme, Work/Reminder UI, scoped ownership, bounded read admission and focus | Complete Chromium checkpoint passed; final integrated three engines and corrected zoom screenshots pending |
| WP10 Independent contract/privacy checks | `163f2efd`, `d6f79210`, `ab8481cb`, `0734fd62`: literal oracles, rehashed wrong effects, races, rollback, erasure and client ownership controls | Native/PG/client controls passed at identified sources; final integrated qualification pending |
| WP11 Growth/setup/current docs | `7fc8400e`, `996534e4`, `dec84e5a`: populated pending/completed growth, active guides/examples/generated contracts/help and cumulative Unreleased | Tooling/docs/build consistency passed earlier gate; full growth and controlled comparison pending |
| WP12 Delivered-state closure | Local commits, exact-source archives, safe reports, this matrix and artifact matrix | Open: final gate/all engines, full growth/comparison, final limits and owned-resource cleanup |

## Acceptance matrix

| Group | Specific implemented behavior and exercised controls | Remaining qualification |
| --- | --- | --- |
| A01 Text/optional semantics | `NarrativeValueTests`, `NarrativeContractTests`, record vectors and native PTY controls cover scalar boundaries, LF/Unicode/control admission, permanent decoding, keep/set/clear, required facts and exact output. Connected paid-description and maximum-text Chromium journeys passed | Final source/all engines; corrected zoom readability |
| A02 Reminder model/lifecycle | Domain decisions and shared store contract exercise creation identity/revision, terminal/no-op rules, inactive retention/reactivation, closed cleanup, replay and bounded pages. Actual CLI/Chromium lifecycles passed | Final gate/all engines; full high-terminal population |
| A03 Concurrency/atomicity | Shared memory+PG race contract tests distinct/same reminder revisions, preserved fact preview, close/erase/assignment/revoke outcomes. PG trigger failure proves rollback; actual Chromium lost-ack retry uses identical bytes and one replacement ID | Final-source integrated/all-engine checks |
| A04 Scope/date/coverage | Actual PG Kiritimati/Pago_Pago/Riga installations compare each get/list/summary observation to PostgreSQL AT TIME ZONE; Mine/access, distinct attention and no date-ledger writes checked. Existing focus and added 60-second test refresh server Nov2→Nov3 with client clock2031 and invalidate TODAY→OVERDUE dependent reads | Final timer integration/all-engine qualification; tests cover a DST-observing zone, not an actual DST-transition instant |
| A05 Query/search/financial isolation | PG keysets/extreme dates/derived-owner corruption, current search, unique-case money and actual SQL amplification controls passed. Full growth seeds 100/1000/10000 pending plus the same completed population on one parent, with independent page identities/counts/money | Full growth/100k-case plans and controlled unchanged-read latency/memory comparison pending |
| A06 Records/history/vocabulary | Work/Reminder commands/snapshots/effects/header literals, root/creation relations, wrong-parent/due/max-revision rejection, typed sequence activity and current-versus-prior disclosure checked. Generated schema/TS/OpenAPI/messages/help match | Final-source gate/publish input verification |
| A07 Complete erasure | Actual related rows/payloads/nonces/tombstones inspected; pending/completed/cancelled/compound content cleared, identities reserved, identical other case preserved, erase races checked | Final client cache/recovery all-engine checks |
| A08 Rebuild/restore/owner operations | PG maintenance/checkpoint/head exclusion, FK reconstruction, invalid-state rollback, derived-projection repair, old-backup refusal and accepted-erasure reconciliation checked; native owner/process cases passed | Final gate and declared native Linux environments |
| A09 Delivery/recovery/ownership | Native private publication-before-send and strict identity-only journals checked. Complete Chromium exercises actual IndexedDB independent attempts/foreign scopes/tab loss/old formats plus new WORK/REMINDER records; exact compound retry does not duplicate | Final three-engine source qualification; no old real journals converted/deleted |
| A10 CLI/shell | Generated discovery/help, actual service work lifecycle/paging/revisions, file/stdin/editor cleanup refusal, real retained-text PTY and exact JSON/NDJSON/CSV checked | Final ordinary gate/native Linux qualification |
| A11 Web/interaction | Complete connected Chromium checkpoint covers permissions, recovery, uncertainty, financial safety and work/reminders; language/RTL/theme/narrow/zoom/reduced-motion/focus checks and visual review retained | Corrected zoom connected evidence and final complete Chromium/Firefox/WebKit; no WCAG certification claim |
| A12 Integration/growth | Generated/schema/format/source/publish consumers updated; authoritative version/instructions unchanged; exact-source evidence retained | Final gate, full growth/three-pair comparison, final matrix/cleanup and exact native-platform limits remain open |

## Corrections and retained limitations

Material failed gates and partial browser runs remain in numbered logs. `full-gate-final.log` retains
the d7bf472a run:804 native tests passed, then frontend lint rejected the new fixture binding.
The one-line destructuring correction `2f276f43` passed frontend lint; its restarted full gate remains
pending. Corrections preserve required
assertions and budgets: fixtures now target independent revisions/current projection owners; diagnostic
cluster setup respects the suite worker bound; browser reads queue within the unchanged server admission
budget and retain permits through body decoding; replaced action triggers transfer focus; fixture writes
wait for accepted current reminder readback. Visual review detected unreadable zoom wrapping despite a
passing no-overflow assertion, so usable heading geometry is now independently checked. These are
corrections to observed failures, not reasons to treat earlier incomplete checks as complete.

Initial iterative diagnostics were not all captured as complete source patches. Later gate source
archives and exact commits identify the stronger evidence. The query probe retains prerequisite/parser
failures and final raw plans; its initially failed raw plan was not saved before the parser exception.
Before captures are partial. These limits must remain explicit when assembling the final review package.

Changed authored-follow-up workloads and reminder-derived workloads have different seed semantics and
fingerprints. They were separated before any comparison measurements; unmatched feature observations
cannot claim comparable budget passes. The six unchanged read keys and the 0.85 throughput / 1.30
latency-and-verification ratio budgets remain intact. Full current growth and three alternating full
same-host pairs against b5e2169 are required; selected diagnostics cannot replace them.

The declared native CI matrix includes Ubuntu24.04 `linux-x64`/`linux-musl-x64` and macOS15
`osx-arm64`. Local macOS execution does not establish the two native Linux jobs, and Docker emulation
would not establish those CI platforms. No push is authorized, so those new-source external jobs remain
unrun and must be reported separately. No push, merge, tag, release, product-version bump, deployment,
migration or real-installation/data operation is part of this delivery. AGENTS.md and AGENTS_EXTRA.md
remain unchanged; no supplement change is proposed.
