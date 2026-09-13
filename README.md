# ClaimCore

ClaimCore is a deliberately narrow, pre-1.0 claims case register. Its F#/.NET core and PostgreSQL
store preserve thirteen business fields and every accepted revision; a local React Web host and a
structured CLI expose the same business rules.

ClaimCore assumes a single trusted local administrative boundary. Its Web bootstrap credential
controls local admission but does not identify or authorize individual users. ClaimCore records a
handler's assertions; it does not adjudicate coverage, transfer money, or provide a remotely
accessible or multi-tenant service.

## Applications

ClaimCore publishes exactly three application executables:

| Executable | Purpose | Intended operator |
|---|---|---|
| `ClaimCore.Web` | Localhost-only HTTPS React interface for normal case work and recovery. | A trusted human operator. |
| `ClaimCore.Cli` | Strict JSON CLI-v3 interface for automation and operational use. | Trusted scripts, agents, and terminal users. |
| `ClaimCore.Database` | PostgreSQL schema migration and bounded technical-preparation pruning. | A database administrator using the schema-owner credential. |

The Database executable changes ClaimCore's PostgreSQL schema and technical recovery storage. It is
not a second case-work interface. Product libraries and repository tooling are not additional
applications.

Runtime operations that use private credentials or artifacts are supported on macOS and Linux.
Windows remains a source build/test and database-free discovery host; the three applications fail
closed there for private-file runtime work until an independently verified Windows backend exists.

## Guarantees and limits

- `CaseFields` has exactly thirteen business fields; revisions, operation IDs, timestamps,
  attribution, and available commands remain technical metadata.
- The closed domain model owns validation and its eight state transitions.
- Optimistic concurrency prevents a stale command from silently overwriting a newer revision.
- Content-bound operation IDs provide exact replay after an uncertain result.
- PostgreSQL transactions, structural constraints, and checksum-bound ordered migrations protect
  durable state.
- One semantic contract projects pure CLI-v3/Web-v2 codecs, exact response schemas, generated
  browser DTOs and validators, keeping adapters aligned with the core.
- Deterministic, property, PostgreSQL, published-process, three-engine browser, accessibility, and
  recovery tests exercise the supported boundaries.

The [domain contract](docs/domain.md) is authoritative for field and transition meaning.

## Start here

There is no public packaged release yet. The supported first-run path builds the current version from
a source checkout, creates a dedicated persistent local PostgreSQL database, and launches the
published Web host. Follow [Getting started](docs/getting-started.md); it uses only synthetic data.

The CLI's database-free `help`, `version`, `describe`, and `schema` commands are described in
[CLI and protocol](docs/cli.md). Contributor builds, tests, and full verification live in one place:
[Development](docs/development.md).

## Documentation

- [Documentation map](docs/README.md) routes readers by task.
- [Domain contract](docs/domain.md) defines the business record and transitions.
- [Architecture](docs/architecture.md) defines runtime responsibilities and trust boundaries.
- [Web](docs/web.md), [CLI](docs/cli.md), and [Database](docs/database.md) are the application
  references.
- [Security and operations](docs/operations.md) defines privacy, recovery, and deployment limits.
- [Synthetic walkthrough](examples/README.md) exercises one complete CLI lifecycle.

## Maturity and safety

This repository is a local application, not a production deployment template. Before considering
personal or operational data, read [Security and operations](docs/operations.md). Vulnerabilities and
sensitive reports must follow [SECURITY.md](SECURITY.md), never a public issue. General help and
privacy-safe issue routing are in [SUPPORT.md](SUPPORT.md).

## Contributing and license

Contributor rules are in [CONTRIBUTING.md](CONTRIBUTING.md). ClaimCore is open-source software under
the [Apache License 2.0](LICENSE). The original work is copyright © 2026 Ervins Strauhmanis;
contributors retain copyright in their own contributions.

CI-qualified publish trees for CLI, Database, and Web contain the project license, a .NET CycloneDX
SBOM, and full-text third-party notices. The qualified Web tree also carries its manifest-bound locked
npm SBOM and notices.
