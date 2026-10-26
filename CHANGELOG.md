# Changelog

`Unreleased` summarizes implemented changes intended for the next release, including relevant uncommitted work, cumulatively against the preceding published non-prerelease version; the first release describes implemented scope. Verify actual publication rather than inferring it from headings, tags or workflow status; public prereleases do not reset this baseline. Check claims and material omissions against implementation, contracts, tests and documentation; commits and plans are leads. Describe consequences for users, operators, integrators or contributors, preserving material compatibility effects, limitations and required action, including for prerelease users. Exclude development chronology, superseded changes and verification logs.

Use concise bullets without category labels or subheadings, grouping related changes and putting breaking changes and required actions first. Each bullet occupies one physical line, at most 200 characters including Markdown but excluding its `- ` marker: a ceiling, not a target. Shorten or split without losing material consequences. Use technical names when helpful; link to existing relevant documentation, without assuming paths or creating documents merely to satisfy this rule. Keep `## [Unreleased]` above dated `## [version] - YYYY-MM-DD` headings, newest first; retain existing release-note links. This file is authoritative for release summaries and their writing rules; derive published summaries from it and leave unrelated history unchanged. Review accuracy, completeness, duplication, compatibility and format; report unresolved evidence gaps outside this file and run applicable existing checks. Versioning, dating and publication require release authorization.

## [Unreleased]

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
