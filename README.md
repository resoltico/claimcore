# ClaimCore

**A local claims case register with one core for people and automation.**

Record claim facts, payment decisions and payment dates through a structured CLI or a browser
interface. Both use the same F#/.NET core for validation, case transitions and recovery, with
PostgreSQL storing the current case and its accepted history.

[Get started](docs/getting-started.md) · [Documentation](docs/README.md) ·
[CLI reference](docs/cli.md) · [Architecture](docs/architecture.md) ·
[Releases](https://github.com/resoltico/claimcore/releases)

> **Source preview.** ClaimCore is pre-1.0 software for evaluation with synthetic data, not a
> production deployment template. The instructions and contracts in a checkout describe that
> source revision; they may differ from a published release.

## What it records

The [thirteen-field business record](docs/domain.md) covers incident and notification details,
parties, claimed and payable amounts, case reference, decision and payment dates, and status.
Revisions, operation IDs and other technical metadata remain separate.

Register and amend a case; record or withdraw a payment decision; record or clear a payment date;
close or reopen the case. Atomic factual corrections preserve accepted history. Payment progress
and open/closed status are independent: recording payment does not close a case.

These are the handler's recorded assertions. ClaimCore does not adjudicate insurance coverage,
transfer funds, or replace a complete claims-management platform. Policies, reserves, banking
details, notes and attachments are outside the current record.

## Interfaces

| Application | Use it for |
|---|---|
| `ClaimCore.Cli` | Strict JSON commands and structured outcomes for agents, scripts and terminal users. Runs independently of the Web host. |
| `ClaimCore.Web` | Localhost-only HTTPS browser interface for human case work and explicit recovery. |
| `ClaimCore.Database` | Schema-owner administration: fresh initialization, read-only verification and bounded pruning of technical preparations. Not a case-work interface. |

The browser offers English, Latvian and Arabic, independent display formats and right-to-left
layout. Expanded English is a layout-test pseudolocale. Language changes preserve drafts, prepared
requests, confirmation and recovery state without changing the installation calendar or recorded
currency. See [Browser presentation](docs/web.md#browser-presentation).

## Try ClaimCore

### Inspect the CLI without a database

After the [Release build](docs/development.md#first-checkout), run these commands from the repository
root to inspect the semantic summary and machine-readable invocation schema:

```sh
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- describe summary
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- schema invocation
```

Neither command starts PostgreSQL or the Web host, or requires runtime credentials. The
[synthetic CLI walkthrough](examples/README.md) exercises a complete case lifecycle.

### Run the local application

Follow [Getting started](docs/getting-started.md) from source build through PostgreSQL initialization,
private credentials, a local HTTPS certificate and first login. Keep administrator credentials
separate from runtime credentials.

**Runtime support is macOS and Linux.** Windows supports source builds, tests and database-free
discovery; private-file runtime operations fail closed there.

**Fresh baseline, no in-place upgrade.** Unsupported existing installations and old recovery
formats are refused, not converted or deleted. Preserve old databases and artifacts with their
matching software. Read the [installation boundary](docs/database.md#fresh-installation-boundary)
before initializing a new target.

ClaimCore assumes one trusted local administrative boundary. Browser admission does not provide
individual operator identities or per-user authorization. Remote access and multi-tenancy are not
supported. Read [Security and operations](docs/operations.md) before considering real data.

## How state stays consistent

- **One authority.** Domain and Application own validation and available commands. Execution
  revalidates authoritative state; an editable form or preview is not permission to commit.
- **Revision-aware changes.** Stale commands cannot silently overwrite newer state. A newly accepted
  operation advances one revision; an exact replay returns its recorded receipt without another.
- **Explicit uncertainty.** Missing output is not proof of failure. Recovery preserves the exact
  operation ID and request bytes. Durable revocation ends an unaccepted operation's authority
  without erasing earlier attempt evidence.
- **An explicit calendar.** Each installation has one immutable IANA business time zone, separate
  from browser language and display formats. Amount display preserves decimal precision; authoring
  and canonical copying remain independent of presentation.

Recovery is not a backup system. Restoring an older database can remove later acceptance evidence;
reconcile the restore before resuming case work. See [Data and recovery](docs/operations.md#data-and-recovery).

## Architecture and verification

ClaimCore is a typed modular monolith, not a collection of distributed services.
[`architecture.json`](architecture.json) defines component responsibilities and permitted
dependencies; compiled architecture tests check those boundaries. Shared contract projections
produce the CLI/Web schemas and browser validators instead of letting each adapter invent its own.

Required verification includes deterministic and property tests, real PostgreSQL tests, published
CLI tests, and Chromium, Firefox and WebKit lifecycle and accessibility checks. A passing gate is
execution evidence, not owner approval or production certification. [Development](docs/development.md)
owns the build and verification commands; [Architecture](docs/architecture.md) explains the design.

## Contributing and support

Read [Contributing](CONTRIBUTING.md) and, for agent-assisted work, [AGENTS.md](AGENTS.md).
[Support](SUPPORT.md) covers general help and privacy-safe issue reporting.

Report vulnerabilities and sensitive findings through [Security](SECURITY.md), never a public
issue. Do not include claimant data, credentials or recovery artifacts in public reports.

## License

[Apache License 2.0](LICENSE). Original work © 2026 Ervins Strauhmanis; contributors retain copyright
in their contributions. Qualified publish outputs include third-party notices and software bills
of materials. Release changes are recorded in the [changelog](CHANGELOG.md).
