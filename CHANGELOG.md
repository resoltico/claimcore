# Changelog

Notable changes to this project are documented in this file. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- **Ordinary rejection contracts now require a structured diagnostic.** CLI-v3/Web-v2 rejections include a stable `diagnostic.id` and an exact parameter object, so consumers can distinguish causes without parsing English. Native `Rejection` is now a closed reason with derived classification and guidance, and `DomainError.InvalidInput` has typed targets and violations instead of strings. Update native callers and rebuild matched host/browser contracts; old rejection shapes are not accepted. Default English remains available as presentation text. Case data, canonical operation records and recovery authority are unchanged, and no database migration is required. See [Rejection diagnostics](docs/diagnostics.md).

- **The CLI-v3 and Web-v2 contract fingerprints change, and no old-contract path is kept.** Semantic identity no longer includes presentation labels and explanations, so editorial wording no longer alters machine identity; it now includes the text constraints on amount and currency values, which it previously omitted, and a separate rule-set revision for deliberate behaviour changes that no descriptor shape would show. Rebuild the browser and its host together from the same source revision and do not mix generated contracts with older binaries. Canonical operation-record encoding, authored operation identity, and the thirteen business fields are unchanged, so stored cases and accepted history are unaffected and no migration is required.
- Building from source now requires every project reference to be declared. A case-work host can no longer compile against PostgreSQL administration or against Npgsql through the composition root, while the storage assemblies it needs at runtime are still published. A fork that relied on reaching those types indirectly must declare the reference it actually uses, or stop using it.

### Fixed

- The README no longer names a specific latest release, which was stale as soon as the next one published; it points to GitHub Releases instead.
- The domain contract said a closed case must be reopened before editing, which contradicted the one-step factual correction added in 0.3.0. It now records that correction as the deliberate exception.

### Internal

- Rejection metadata is fingerprinted independently of explanation text. Complete diagnostic examples and malformed variants qualify the native, CLI and browser projections, while shared Web metadata schemas keep generated validators within the existing size limit. This prepares ordinary rejections for localization; it does not add language selection or translate the remaining failure families.

- Semantic identity is now length-prefixed rather than separated by a null character, so no token boundary can be reproduced by a value that contains the separator, and integers are written with the invariant culture.
- A successful test producer now validates its own result file against the same reviewed inventory that final evidence reconciliation uses, before it records success. A changed test count, a substituted name at the same count, and a missing, duplicated or wrong-assembly report are all refused at the job that produced them rather than at the end of the run. Seven regression tests cover the new refusals.
- The evidence job fetches its artifact scanner once with a reviewed checksum instead of letting the scanner fetch itself mid-scan, and a scanner that cannot run is now reported differently from a scan that completed and refused its targets. Both still fail closed and neither emits a matched value or a target path, but a blocked run can now be told apart from a refused one.
- Browser networking rules now cover root components, qualified global calls, and alternative networking APIs, with positive and negative controls run by the existing lint stage. This is bounded lint enforcement, not a sandbox.

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
