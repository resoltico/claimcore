# Changelog

Concise change records are kept here. Published GitHub release notes retain the detailed historical
compatibility, security and operating instructions. Current behavior is described in the [guides](docs/README.md).

## [Unreleased]

- **Breaking installation baseline:** initialize a separate installation; earlier schema baselines are refused untouched. Preserve them with matching software. [Compatibility](docs/architecture.md#durable-records) describes the posture.
- **Breaking wire contracts:** deploy matching service, CLI and browser. Shared operation preview/execution uses CASE or AUTHORITY scope; case views group registration, decision and payment facts while history diffs remain per field.
- Operation identity compares amount values and ASCII-folded references. Free text remains exact, and ledger requests retain authored values. Public snapshots keep their canonical representation.
- Acts and CLI verbs use consistent names, including CORRECT, REGISTER, GRANT, REVOKE, ENABLE and DISABLE; grant/revoke and enable/disable have distinct request shapes.
- Contract owns the typed operation catalog. It derives CLI syntax, browser bindings and the checked [CLI reference](docs/cli.md); generated contract and message types come from the same sources.
- Executables are `claimcore`, `claimcore-server` and `claimcore-database`. Human verbs have localized specific usage errors, preview, and byte-bounded offline preparation.
- One Template grammar serves CLI and browser messages. Typed arguments, frozen renderer vectors, bidi isolation and literal-only pseudolocalization replace the browser ICU runtime.
- Case and authority decisions share ledger sequencing, while scope-specific admission, policy and projections remain explicit.
- `operation.resolve` returns the author’s receipt or permanently fences an unaccepted ID. Exit 4 retains the exact retry frame; ordinary failure does not resolve prior uncertainty.
- Protected sessions use PostgreSQL deadlines and shared key-ring custody. Clock, restart and deployment limits remain explicit in the [operations guide](docs/operations.md#access-review-and-sessions).
- Managed Unix private-file admission supports secret-volume links and group read. Windows, ownership/ACL and open-race limits are documented in [private configuration](docs/operations.md#private-configuration).
- Input guidance and bounded Unicode warnings preserve authored text. Automated accessibility checks complement the human review still needed for translations and assistive technology.
- Pinned runtime/provider security updates, maintained browser boundary rules and independent negative controls strengthen build and verification inputs.

- Tests use four environment tiers and one shared support library; architecture checks join the pure tier and each published CLI usage error has its own discoverable case.

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
