# Changelog

Notable changes to this project are documented in this file. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
