# Changelog

## [Unreleased]

- Use a fresh database: notification/amount projections and authority-history indexes change the SQL checksum. Earlier installations are refused; no migration or reader is supplied.
- Regenerate catalog, client and OpenAPI consumers; bindings name vocabularies/advice groups and add CHOICES for repeatable worklist facets.
- Discard earlier case-list cursors; format 3 binds canonical filters, assignment and notification order. Other existing cursor formats remain unchanged.
- CSV exports prefix formula-leading cells with an apostrophe before quoting; use JSON for exact authored values.
- CLI public settings load from a private Unix profile with per-value environment overrides; unknown members are refused. Use configure --show to inspect sources.
- Stored browser preferences reset; version 2 adds independent SYSTEM, LIGHT and DARK themes.
- Update source callers for typed IDs/positions, complete field groups, grouped refusals, act descriptions and endpoint reply classification; Schema interpreters handle Vocabulary annotations.
- Handle new CASE_STATUS_PROJECTION, CASE_PROGRESS_PROJECTION, CASE_CHANGED_AT_PROJECTION and CASE_SEARCH_PROJECTION verification findings.
- Audit authority changes with installation OWNER or AUDITOR rights using authority.history or the CLI, including typed facts and target-filtered newest-first pages.
- Select assigned cases and oldest notifications; case.summary counts the current visible filter and returns exact per-currency totals beyond single-amount precision.
- Erasure clears notification dates and all amount/currency projections; verification identifies divergence in each new column.
- Local evaluation setup creates an isolated Firefox CA profile when certutil is available and writes ready public CLI settings; installed Firefox is the documented default.
- Run reviewed commands in claimcore shell with one sign-in; access/refresh tokens and arrow-key history stay in memory, with 15-minute idle discard and no history file.
- Vocabulary token plans reduce repeated work in case writes and ledger verification while retaining permanent bytes and integrity checks.
- growth-compare compares three alternating same-host pairs and fails relative throughput, latency or verification regressions using workload fingerprints.
- Use focused checks during development; change probes and growth runs require a concrete question or explicit acceptance scope, while implementation PRs retain full product verification.
- Musl CI installs pinned Python required by native comparison controls.
- Positive test inputs use inventory builders; local golden updates refuse CI and permanent vectors, and one transition oracle supplies semantic expectations.
- Case history now pages newest revisions first; follow nextCursor for older changes. Cursor bytes and shape remain unchanged.
- Filter case lists by lifecycle, status, progress and normalized reference/claimant/insurer substrings; order by reference or latest accepted change.
- Case-list rows add claimant, insurer, canonical claimed/payable amounts, progress and last-change time; erased rows expose only retained metadata.
- Case lists hide cases with corrupt case-scoped human-only grants to the querying service principal, even with installation-wide read access, matching case.get.
- Export case lists as RFC 4180 CSV; --all follows per-page snapshots and stops with the reached cursor on refusal.
- Terminal text verbs can fetch an omitted revision, preview and ask before execution; automation still supplies a revision and omitted correction groups default to KEEP interactively.
- Browser routes preserve worklist queries, deep links and sign-in return; reserved references use reversible prefixed escaping.
- Browser inputs use locale dates/amounts and currency/country suggestions; linked error summaries focus first, and the server remains authoritative.
- The responsive work surface adds saved worklists, grouped facts, progress/history and contextual case/authority actions with distinct primary actions in both themes and en/lv/ar.
- Accepted browser case changes return to the current case with an act/revision notice; uncertain delivery retains exact retry and final-resolution controls.
- Authority actor actions bind the selected principal; retained requests need explicit discard consent before a fresh request can target another actor.
- Stored act, role and principal kinds use permanent decoders; unknown tokens fail reads and diverge verification even when SQL accepts their shape.
- Erasure also clears searchable text and status/progress projections while retaining reference, revision, last-change time and ledger metadata.
- One ClaimCore meter exposes bounded request, ledger and session observations; verification replies include elapsedMilliseconds and administrative heads support genesis.
- Field/act declarations drive shared processing; private constructors enforce complete typed groups while permanent ledger encodings stay unchanged.
- Browser forms use catalog vocabulary, correction advice and selected-entry constants; core labels remain owned by their declared vocabularies.
- Tests share a hand-authored independent inventory and one input-builder owner per language; seed-corpus failures name their shared cause without skipping properties.
- Protocol source paths follow Wire, Api, Catalog, Messages, Presentation and CommandLine responsibilities.
- Total exhaustive tables may justify complexity suppressions; rule bodies and product/test code retain the existing lint limits and controls.
- Built-in lint severities inherit enabled categories; explicit exceptions, configured rules and JavaScript-plugin rules remain in place.

## [0.13.0] - 2026-10-28

- Initialize a fresh installation; the SQL baseline checksum changes. Earlier installations are refused untouched; preserve them with matching software. No migration or reader is supplied.
- Deploy matching service, CLI and browser builds; wire tokens, input diagnostics and locale keys change. Regenerate integrations from the checked contracts.
- Input refusals carry a nonempty INVALID_INPUT.issues array; each issue has field, code and declared parameters. Replace single-target scalar diagnostics in integrations.
- Technical-target locale keys use field.*; history detail uses input.detail.*. Replace token.* keys with vocabulary-qualified labels and typed terms; renderer catalogs cannot copy core wording.
- Database HELP/VERSION replies use kind tags; help exitCodes lists code/number entries. Update consumers of earlier machine replies.
- Database USAGE_ERROR carries a typed issue and applicable syntax; update machine consumers. Both tools share help/version models and declare supported exits locally.
- Build with .NET SDK 10.0.403 and deploy .NET/ASP.NET Core 10.0.14; production JWT/configuration transitives and deployment images track the security patch train.
- Independent input errors accumulate within parsing and admission phases. CLI prints each issue; browser forms show all field errors, focus the first and report the count.
- Principal kinds and roles stay typed after decoding. Unknown tokens are protocol faults; malformed stored kinds or roles produce integrity faults instead of input refusals.
- Actor replies use admitted principal identities with canonical HTTPS issuers. Authored identity fields remain separate from trusted output models.
- Case advice lists act kinds with conditional correction choices; offers omit expectedRevision. Use the snapshot revision, or zero when opening.
- Catalog inputs, paths and branch conditions derive from request codecs. Generation refuses missing/orphan annotations and undeclared branch tokens; CLI owns command syntax.
- Source integrations use Domain outcomes, typed amendments/act slots, shared grants/actors and EntryHash positions; Protocol owns endpoint, catalog and command-line models.
- Registration amendments expose KEEP/REPLACE; CLEAR remains a wire decode error. PaymentProgress prevents payment without a decision. Retained encodings and format numbers stay unchanged.
- Other refusals and faults retain flat code-tagged parameters; actualRevision is specific to revision conflicts. One checked diagnostic registry supplies direct message-key lookup.
- Authority admission belongs to Domain. Service and named ports replace Application routes and handlers; CLI authority input uses --principal-kind.
- Client reply correlation checks are removed; clients trust schema-admitted HTTPS replies. Retain exact frames for resubmission or final resolution after uncertain delivery.
- Execute OUTCOME_UNKNOWN remains distinct from known failure with exit 4 and its recovery guidance. Existing success tags and numeric exits remain stable.
- Remove unproduced REPLAY_EXACT/REAUTHENTICATE actions and SESSION_FORBIDDEN host code; credential failures retain SESSION_REJECTED with HTTP 401.
- Remove REVISION_EXHAUSTED and LEDGER_SEQUENCE_EXHAUSTED; endpoint locale keys replace label.read.<endpoint>.*. Country metadata publishes unknownCode.
- RecordFormat owns literal durable names and record-byte limits independently of wire policy; permanent scalar grammars only relax within a format.
- Compiled controls cover core ownership, unused/duplicate declarations, typed vocabularies/messages and codec/catalog agreement. Each added gate has a prohibited control.
- F# and TypeScript product/test files have 500-line limits. Functions allow five parameters and F# tuples four items; complexity remains capped at 12 without new suppressions.
- Remove unused .NET and browser coverage tooling. Locked dependency audits, meaningful properties, handwritten vectors and complete stage discovery remain required.
- Browser production builds compile app sources and omit OpenAPI from the bundle. Browser unit paths mirror source; lint policy is split by responsibility.

## [0.12.0] - 2026-10-26

- Initialize a separate installation; the new database baseline refuses earlier installations untouched. Preserve them with matching software; no conversion is supplied.
- Deploy matching service, CLI and browser: shared operation preview/execution and renamed acts replace the preceding wire contract. Regenerate integrations from the checked OpenAPI.
- Tagged unions use `kind`; receipts and reviews use `subject`. Cases expose ACTIVE, VOIDED or ERASED state with grouped registration, decision and payment fields.
- Durable records have independent codecs and format 1. Database administration replies use `kind`, `entryHash` and `REJECTED`.
- Executables are `claimcore`, `claimcore-server` and `claimcore-database`. CLI verbs provide localized usage errors, preview and bounded offline preparation.
- Operation identity compares amount values and ASCII-folded case references while retaining authored requests; free text remains exact.
- `operation.resolve` returns the author’s receipt or permanently fences an unaccepted ID. Exit 4 retains the exact retry frame; ordinary failure leaves prior uncertainty unresolved.
- A codec-derived operation catalog supplies CLI syntax, browser bindings and the generated [CLI reference](docs/cli.md); generated contracts replace the preceding artifact inventory.
- Core, browser and CLI own separate locale catalogs using one Template grammar. Typed arguments, bidi isolation and literal-only pseudolocalization support presentation.
- Typed endpoint handlers, field-owned scalar rules and one eligibility table align server, client and persistence behavior while preserving refusal precedence.
- Four test environments share support code; independent byte fixtures, compiled architecture checks and maintained browser boundary rules cover the shared contracts.
- Refresh pinned dependencies and deployment images, including Keycloak 26.8.1-1.

## [0.11.0] - 2026-10-21

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.11.0).

- .NET runtime security updates, managed private-input admission, and persistent protected Web sessions with explicit key-ring custody.
- Closed diagnostics and targeted malformed-business-input rejections; current navigation/advice and bounded history/grant pages.
- Coupled consumer-contract identity and a fresh database baseline; earlier installations remain preserved with matching software.
- Duende OAuth/OIDC clients, offline localized CLI help, and strict loopback callback handling.
- Conservative CLI output-loss handling, bounded readiness and explicit browser recovery preserve delivery uncertainty.
- Managed build/process controls replace native components; framework-dependent publication retains reviewed redistribution notices.
- Owned Compose readiness, private path handling and identity memory budgets improve evaluation setup.
- Closed domain types, pure verification folds, independent contract/record checks and retained numeric growth evidence strengthen verification.

## [0.10.0] - 2026-10-17

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.10.0).

- Owner-private file admission checks ownership, modes and ACLs on the qualified platforms.
- A coupled twelve-endpoint client/host contract separates principal syntax from authoritative identity admission.
- Case listing and bounded actor/grant views authorize independently and preserve snapshot scope.
- Generated browser authoring and complete decoders cover business, stewardship and authority work; preferences retain only their declared fields.
- PostgreSQL verification, project closures and bounded fixture/process cleanup gain dedicated controls; native publication prerequisites are documented in the release notes.

## [0.9.0] - 2026-10-15

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.9.0).

- One PostgreSQL authority and accepted ledger replace the primary/witness model; incompatible installations are refused untouched.
- A coupled eleven-endpoint contract and versioned RFC 8785 records define case work and exact bounded wire values.
- Current authority controls snapshot disclosure; preview remains advisory and execution rechecks state under the ledger lock.
- Case references retain accepted spelling, service principals need explicit registration/grants, and historical accepted values remain readable.
- VOID, REINSTATE and ERASE become accepted case acts, with live erasure limits stated explicitly.
- Build and verification stages are simplified; product-managed witness, copy-custody and backup/restore orchestration are removed.

## [0.8.0] - 2026-10-08

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.8.0).

- Dependency auditing retires the patched build-tool advisory exception and refreshes pinned runtimes and providers.
- Docker contexts exclude private/generated material, while publication retains package-specific redistribution notices.
- Witness schema and asynchronous native interfaces change; matching deployments and fresh compatible baselines are required.
- Exact draft comparison, independent preference changes and delayed-result handling preserve authored browser state.
- Database and audit work honor cancellation; timestamp validation, runtime diagnostics and bounded evidence handling are tightened.
- Operating guidance covers public origins, separate listeners, private macOS paths and complete deployment qualification.

## [0.7.0] - 2026-10-04

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.7.0).

- Original work and incoming contributions move to MPL-2.0; covered redistribution requires corresponding source information.
- TLS revocation, public-only trust roots, private-file Unicode handling and platform admission receive security hardening.
- Raw Ed25519 key comparison enforces signing-role separation; backup and lifecycle approval controls bind current authority and time.
- Session deadlines and conservative CLI cancellation/output handling preserve unresolved mutation outcomes.
- Browser recovery and stale-read handling preserve the intended case, current disclosure and authored values.
- Bounded frames, typed admission phases and Unicode warnings make input/delivery behavior explicit.
- Independent oracles, complete report discovery and owned restore/overload controls strengthen qualification; detailed historical advisory exceptions remain in the published notes.

## [0.6.0] - 2026-10-02

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.6.0).

- A separately credentialed PostgreSQL witness records acceptance, attempts, grants, lifecycle transitions and owner authority.
- Actor-bound authorization, void/reinstatement holds and staged privacy erasure govern case work.
- Signed copy custody, encrypted captures, isolated restore qualification and two-owner terminal retirement support the then-current operating model.
- Fresh schema and client/native contracts replace earlier baselines and unbound facades; recovery artifacts become authenticated and installation-bound.
- Authority, clock and TLS checks cover runtime opening, delayed work and audits.
- Contributor tooling, compiled boundaries, source/publication evidence and strict report qualification are expanded.

## [0.5.0] - 2026-09-23

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.5.0).

- English, Latvian and Arabic browser text, RTL layout and independent display formats preserve drafts and prepared work.
- Initialization binds one schema, installation identity and immutable business time zone; earlier migration and historical import paths are removed.
- Versioned canonical requests, recovery envelopes and closed diagnostic identities require matching clients and exact uncertain retries.
- Explicit project dependencies and scalar-aware identity strengthen contributor and integration boundaries.
- CLI/database delivery handling, typed HTTP failures and targeted correction guidance preserve known outcomes through failures.

## [0.4.0] - 2026-09-21

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.4.0).

- Installation-calendar guidance requires consistent host time-zone data and canonical IANA identifiers.
- Compiled component boundaries and strict decoding/property controls cover protocol and recovery surfaces.
- Hosting and CLI protocol responsibilities are separated, and pinned toolchains/publication trees are documented.
- Canonical identity and workflow policy receive preventive hardening; GitHub release notes avoid duplicate headings.

## [0.3.0] - 2026-09-20

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.3.0).

- One-step factual correction covers registration, decision and payment facts while retaining the thirteen-field register.
- An installation-wide business time zone and safer bounded recovery clarify unfinished work.
- Coupled client contracts, fresh identities after editing and durable revocation prevent stale work from changing a case.

## [0.2.0] - 2026-09-14

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.2.0).

- Backup/restore guidance requires reconciliation of accepted work newer than a restored checkpoint.
- Replaying accepted work returns the original receipt without prior recovery details.
- Conflicting retries preserve private receipt/recovery information and do not repeat the accepted act.

## [0.1.0] - 2026-09-13

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.1.0).

- First release.
