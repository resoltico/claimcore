# Changelog

Notable changes to this project are documented in this file. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- A separately credentialed PostgreSQL witness records ordered acceptance, attempt, grant, lifecycle and owner-authority evidence. `verify-data` replays the current primary and witness pair and quarantines missing or divergent history; operators must provision and retain the separate witness, its key ring, writer capability and auditor credential. A same-machine pair does not prove independent-host survival or backup freshness. See [Security and operations](docs/operations.md#independent-witness-authority).
- Data-entry-error voiding, witnessed reinstatement, holds and staged privacy erasure now have distinct actor and owner workflows. Voiding a case with a current or historical payment assertion requires two distinct witnessed steward approvals and does not reverse an external payment; a void preserves accepted history. An erasure request fences ordinary access, while live purge, witness-payload pruning, known-copy deletion and the suppression horizon require separate evidence and approvals. Pseudonymous suppression evidence remains data, and unknown or unmanaged copies cannot be certified erased. See [Disposition and privacy transitions](docs/domain.md#cc-life-001).
- Owner administration tracks signed managed and adopted external copies, retention, holds and independently approved per-copy deletion. `VERIFIED_DELETED` proves one known copy's checked absence, not whole-case erasure; operators must account for every known custodian and location before later privacy certification. See [Managed-copy custody](docs/operations.md#managed-copy-custody-and-restore).
- Owner-held encrypted primary/witness BASE capture, WAL and checkpoint custody, physical per-copy verification, isolated restored-pair reports and fenced writer-handoff evidence are available for synthetic qualification. The generic checkout does not activate real-data certification or independent-host writer handoff; `CAPTURED_UNVERIFIED`, one `RETAINED` copy, or a local restore drill does not authorize promotion, prove independent custody, or provide automatic failover. See [Backup and restore evidence](docs/operations.md#cc-backup-001).
- Owner-only installation-loss retirement gives two distinct, pre-authorized human owners a signed terminal decision when acknowledged history cannot be reconstructed. Its first witness ticket fences old case work and recovery; exact primary and witness settlement retains known operation-denial commitments or an unknown-set whole-installation fence. This does not restore missing cases or permit a same-lineage degraded successor; retain surviving evidence and treat any new installation as unrelated. See [Installation-loss retirement](docs/database.md#administration-results-and-delivery).

### Changed

- **Breaking for administrators:** the new checksum-bound primary baseline refuses existing v0.5.0 primary namespaces untouched, and the separate witness must be initialized fresh; there is no in-place upgrade, adoption or recovery-format conversion. Preserve old databases, volumes, backups and evidence with their matching software, and initialize a separate installation for synthetic evaluation. The generic checkout has no reviewed publication root and cannot activate `REAL_DATA` by configuration alone. See [Fresh installation boundary](docs/database.md#cc-db-001).
- **Breaking for clients and operators:** CLI-v4 and Web-v3 use one authenticated HTTPS case-work service with individual OIDC identities and explicit actor grants. Direct CLI database case work and the shared bootstrap credential are removed; configure issuer/client identities, service TLS and grants, regenerate integrations from the current contracts, and deploy matching Web assets. See [Web admission](docs/web.md#cc-web-001) and [CLI protocol](docs/cli.md).
- **Breaking for recovery integrations:** transfer now uses an encrypted, authenticated, installation-bound recovery artifact v3; v0.5.0 format-2 envelopes and raw canonical-record import are refused. Preserve old artifacts with v0.5.0 software, and retain the exact operation ID and request bytes after uncertain delivery rather than constructing a replacement. See [Recovery identity](docs/cli.md#cc-rec-001).

### Security

- Actor grants are default-deny and rechecked for case and operation reads, mutations, management and recovery. Missing, inaccessible, voided and erasure-fenced resources share a non-disclosing public refusal; case-list continuations are short-lived and bound to the principal, grant revision and query. See [Actor authority](docs/architecture.md#cc-auth-001).
- Runtime opening and bounded scheduled full audits hold primary and witness barriers, refuse missing or weakened business/history constraints, divergent witness authority and pending activation, handoff or terminal-loss fences, and close actor access after an audit failure or missed deadline. A jointly stale primary/witness pair can pass that audit; owner promotion additionally requires separately retained freshness, WAL, recovered-data and writer-fence evidence. The source-preview build cannot claim real-data readiness without a reviewed independent publication root. See [Data and recovery](docs/operations.md#data-and-recovery).
- Primary runtime/schema-owner and witness writer/auditor/schema-owner connections now refuse non-loopback PostgreSQL targets unless `SSL Mode=VerifyFull` authenticates the server certificate and hostname; GSS encryption fallback is disabled. Operators using a remote primary or witness must update every affected private PostgreSQL connection file before opening it. Loopback development connections remain available without TLS.
- The Web host now refuses expired, wrong-hostname, and non-server certificates at startup. Replace an unsuitable PFX with an owner-private certificate containing a `localhost` DNS subject alternative name and server-authentication usage; browser trust remains a separate operator step.

### Internal

- Expanded component, generated-contract, source-review and test-inventory enforcement for the new service and evidence boundaries; local synthetic qualifications and green checks are verification evidence, not independent-host or production certification. Updated the locked .NET, frontend and repository-tool dependency graph without changing the stated coverage floors.

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
