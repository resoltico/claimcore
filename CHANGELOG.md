# Changelog

Notable changes to this project are documented in this file. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security

- Runtime opening and database verification now refuse a schema missing critical business-record or accepted-history constraints, or carrying an unvalidated or unenforced version of them. The existing baseline SQL and stored data are unchanged; operators must stop and reconcile the installation under a reviewed recovery procedure, not edit its marker to pass admission.
- Runtime and schema-owner connections now refuse non-loopback PostgreSQL targets unless `SSL Mode=VerifyFull` authenticates the server certificate and hostname; GSS encryption fallback is disabled for those connections. Operators using a remote database must update both private connection files before opening it. Loopback development connections remain available without TLS.
- The Web host now removes expired in-memory session entries when a new session is created, preventing dormant entries from accumulating without changing idle or absolute session lifetimes.
- The Web host now refuses expired, wrong-hostname, and non-server certificates at startup. Replace an unsuitable PFX with an owner-private certificate containing a `localhost` DNS subject alternative name and server-authentication usage; browser trust remains a separate operator step.

## [0.5.0] - 2026-09-23

### Added

- The local Web interface now offers English, Latvian and Arabic text, right-to-left layout, and independent `en-GB`, `lv-LV` and `ar-EG` display formats. Changing language or format keeps drafts, prepared reviews, consent and recovery identities; displayed dates and amounts are localized while authored values, canonical copies and recovery files remain exact. Expanded English is a layout-test pseudolocale. See [Browser presentation](docs/web.md#browser-presentation).

### Changed

- **Breaking for database administrators:** `ClaimCore.Database initialize <canonical-IANA-ID>` now creates one checksum-bound schema, installation identity and immutable business time zone atomically; `verify` checks the result without changing it. The 0.5.0 runtime refuses 0.4.0 and other unsupported ClaimCore schemas without modifying them. Preserve existing databases and backups with their matching older software, and initialize a separate database; this release cannot upgrade or import their case history. See [Database](docs/database.md#fresh-installation-boundary).
- **Breaking for recovery operators and integrations:** New requests use canonical command-record format 3 and recovery envelopes use format 2. An uncertain retry must keep its exact operation ID and request bytes. Terminal technical preparations with an unsettled identified attempt are now excluded from pruning, even after a later accepted retry. See [Recovery](docs/cli.md#canonical-request-identity-and-recovery).
- **Breaking for CLI, Web and native consumers:** Ordinary rejections, core faults, recovery refusals, CLI protocol and local failures, HTTP host failures and database administration results now expose closed diagnostic identities and exact parameters. CLI adapter failures use `localFailure`; host failures carry correlated HTTP status and execution phase. Old failure payloads and native failure records are unsupported: regenerate clients against the matching CLI-v3/Web-v2 contracts and deploy the matching Web bundle and host. The semantic fingerprint now covers scalar constraints and rule revision, excludes presentation wording, and uses length-framed encoding. See [Diagnostics](docs/diagnostics.md).
- Source builds now require direct project references for every dependency. Forks or tools that previously reached PostgreSQL administration or Npgsql through a case-work host must declare an allowed direct dependency or remove that usage. See [Architecture](docs/architecture.md#direct-compilation-boundaries).

### Removed

- `ClaimCore.Database migrate` and `set-business-zone`, the ordered in-place upgrade path, and import support for historical canonical records and recovery envelopes are removed. There is no compatibility alias or automatic conversion; keep old recovery material with matching earlier software.

### Fixed

- CLI delivery failures and malformed later frames no longer inherit a prior operation's recovery context or append a second stdout result. A lost write or flush preserves the current exact operation context and reports uncertainty when work may have started; inspect that identity before retrying. See [CLI](docs/cli.md#local-failures-and-core-outcomes).
- Database administration preserves confirmed work and unknown commit status across cleanup or result-delivery errors. Exit 4 requires inspection and reconciliation rather than assuming rollback. See [Database](docs/database.md#administration-results-and-delivery).
- HTTP input failures select status 400 or 413 from typed causes rather than English wording. Protocol, startup and administration diagnostics use bounded, known locations and causes without echoing unknown arguments, private paths or provider messages.
- Correction validation focuses the rejected field in its tagged group, and an unreadable recovery import reports a local failure instead of leaving the import busy.

### Internal

- CI now checks one producer per evidence stage, complete current-attempt results and coverage, structural workflow policy, and bounded failure summaries. Scheduled dependency freshness is separate from required vulnerability, deprecation, signature and license checks. See [CI governance](docs/ci-governance.md).
- A read-only owner-review report inventories changed paths and current-revision CI, and an opt-in settings plan describes owner-only PR updates. Merging this source does not activate the GitHub ruleset or establish independent human approval. See [Owner review](docs/owner-review.md).
- Locked frontend and NuGet test dependencies were refreshed, including Prettier, Knip, TypeShape 10 and ApplicationInsights 3; the previous dependency holds were removed after qualification.

## [0.4.0] - 2026-09-21

### Added

- Operator guidance for the installation calendar, explaining that ClaimCore resolves the stored IANA business time zone through the host's own time-zone database rather than shipping one. Hosts serving a single installation should run the same operating-system time-zone data and be updated together: a daylight-saving rule that changed between time-zone database releases can move the business date of an operation that falls inside that transition. See [Security and operations](docs/operations.md#the-installation-calendar). Stored data, configuration, and case-work behaviour are unchanged.

### Changed

- Building from source now requires Node 26.9.0 instead of 26.8.2; the npm release is unchanged. Anyone following the first-run path or running frontend commands must install the release selected by [`.node-version`](.node-version) before building.
- The published CLI tree now also contains `ClaimCore.CliProtocol.dll` and `ClaimCore.Hosting.dll`, and the published Web tree now also contains `ClaimCore.Hosting.dll`; each appears in that tree's .NET SBOM. The Database tree is unchanged, and case work, the CLI-v3 and Web-v2 contracts, and their fingerprints are identical to 0.3.0. Administrators comparing a publish tree or SBOM against a 0.3.0 inventory should expect the additional assemblies.

### Fixed

- `ClaimCore.Database set-business-zone` now refuses a platform-native time-zone identifier on every host. A Windows zone name such as `W. Europe Standard Time` resolves and round-trips on Windows, so it could previously be stored as the installation calendar even though the macOS and Linux hosts that run the case-work applications cannot resolve it. The stored identifier must be an IANA identifier, as the documentation has always described. Installations configured on a supported host are unaffected.

### Internal

- Added a required boundary-decoding qualification suite covering strict JSON parsing, CLI invocation framing, canonical request and snapshot records, recovery envelopes, and history and recovery cursors. It drives each with arbitrary bytes, mutated valid encodings, adversarial JSON, and invalid UTF-8, and requires a typed refusal rather than an escaping exception. The suite runs on Linux, macOS, and Windows, and at a higher case count in the scheduled exploration run. It found no defect in the boundaries it now covers.
- The component architecture is declared once in [`architecture.json`](architecture.json) and enforced by the compiled architecture suite: every project is classified, declared and evaluated project, package, and shared-framework edges must match the declaration exactly in both directions, a permitted but unused edge fails, `InternalsVisibleTo` grants are reviewed, time-zone resolution is confined to the components that own the installation calendar, and the architecture document's component table is generated from the same file.
- Separated the runtime composition roots into their own assemblies. `ClaimCore.Hosting` composes the PostgreSQL runtime, and `ClaimCore.CliProtocol` holds CLI-v3 framing and dispatch over a supplied core, leaving `ClaimCore.Cli` as the process entry point; `ClaimCore.Contracts` now also renders the database-free CLI discovery payloads. Emitted bytes and published contracts are unchanged.
- Hardened two places in the application core: the canonical request identity is derived once from the request instead of being read back from a recovery view that may withhold it, and a recovery cursor's identity write is checked instead of discarded. Both were correct in 0.3.0 and remain so; the change removes latent ways for a later edit to bind the wrong preparation or encode a zero identity.
- Applied the four pending GitHub Actions updates and raised Fantomas to 8.0.0, which reformats six files without changing behaviour. `engine-strict` no longer appears in the frontend npm configuration: it made npm refuse to run under any other Node, so the automated dependency updater could never resolve the frontend graph, leaving it with no automated updates at all. The exact toolchain is still required by every workflow, by the dependency-currency gate, and by the audited, signature-verified locked graph. The lock file recorded Node 26.8.2 while the manifest required 26.9.0; both now agree.
- Consolidated repository workflow toolchain setup into one composite action that proves the runner is using the pinned .NET and Node releases, and added a policy gate that refuses a workflow selecting a toolchain itself, checking out with persisted credentials, or referencing an action without a reviewed commit pin.
- Release notes published to GitHub no longer repeat the version heading that GitHub already supplies as the release title and date.

## [0.3.0] - 2026-09-20

### Added

- Added one-step factual correction for existing decided, paid, and closed cases, allowing complete registration, decision, and payment facts to be corrected together while preserving the thirteen-field register, case reference, status, and prior history.
- Added safer recovery for unfinished work: an operator can close an unaccepted operation permanently, review pending work separately from terminal evidence, and inspect long attempt histories in bounded pages.
- Added an installation-wide business time zone chosen by the database owner, so the CLI and Web use the same business date regardless of the computer or process that is running them.

### Changed

- Updated the CLI and Web request contracts for case correction and recovery. Clients must use the generated contract that matches this source revision; pre-1.0 wire compatibility is not retained.
- Recovery now distinguishes accepted, pending, and revoked authority from what is known about individual attempts, providing clearer next steps after an interrupted submission.

### Fixed

- Editing a request after it has been sent now creates a new operation identity when its authored content changes, preventing a changed request from being retried under the earlier identity.
- A delayed worker now respects a durable operator revocation even when an earlier submission attempt had already started, so it cannot later change the case.

## [0.2.0] - 2026-09-14

### Added

- Added backup-restore guidance explaining that an older backup can omit later accepted operations and must be reconciled with independent records before case work resumes.

### Changed

- If you retry preparation of a command that was already accepted, the CLI and Web now return only its receipt, without the earlier recovery details. Integrations that read those details must use the current contract.
- Under the hood, we simplified how the CLI and Web share ClaimCore's case rules; the thirteen-field register and day-to-day case work are unchanged.

### Fixed

- Retrying an accepted operation now returns its original receipt even after temporary recovery data has been cleaned up, without repeating the action or changing the case.
- A rejected retry or recovery command that conflicts with an existing operation ID no longer includes that operation's receipt or private recovery details.

## [0.1.0] - 2026-09-13

- First release.
