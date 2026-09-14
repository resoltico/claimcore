# Changelog

Notable changes to this project are documented in this file. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
