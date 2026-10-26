# ClaimCore

ClaimCore is a claims case register for people and automation. Record incident facts, payment decisions and
payment dates in a browser or CLI. Both use the same authenticated HTTPS service and current actor permissions.
One PostgreSQL database stores accepted operations and the current case records.

[Start a local evaluation](docs/getting-started.md) · [Documentation](docs/README.md) ·
[CLI examples](examples/README.md) · [Operations](docs/operations.md) ·
[Releases](https://github.com/resoltico/claimcore/releases)

## Work with cases

Open or amend a case, record a decision or payment date, close or reopen it, and correct existing facts
atomically. Payment progress and open/closed status are independent. These are recorded assertions;
ClaimCore transfers no money. The [domain guide](docs/domain.md) explains the business record and corrections.

A human data steward can void an entry error, reinstate a voided case or erase its live payload history.
Erasure retains the reference, revision and ledger metadata. Copies, WAL and backups require separate
operator action; read the [erasure limits](docs/operations.md#erasure-limits).

The browser offers English, Latvian and Arabic, right-to-left layout and separate display preferences.
Business dates follow the installation's immutable IANA time zone. Case roles and scoped grants control
work; an installation owner administers authority and needs separate grants for case work.

## Integrate through the service and CLI

| Application | Use |
| --- | --- |
| claimcore-server | HTTPS service and browser, with OIDC sign-in and bearer API authentication |
| claimcore | Human commands and structured JSON calls, using PKCE or client credentials |
| claimcore-database | Separate owner-only initialization, provisioning and verification |

Try `claimcore case open ... --dry-run` to inspect a request without signing in. Human commands and JSON
frames share the [service contract](docs/api.md). Service, browser and CLI must be deployed together.

Every mutation checks current permissions, state and expected revision under the ledger lock. Resending the
same retained accepted request returns its original acceptance without another entry. If delivery is
uncertain, preserve the exact frame and operation ID. Resend it unchanged or use `claimcore operation resolve`
for an accepted receipt or final non-acceptance. A browser reload keeps only the unresolved ID, subject and act;
it retains no submitted body.

## Evaluate

[Getting started](docs/getting-started.md) creates a fresh synthetic Compose installation. Existing incompatible
installations are refused untouched; preserve them with matching software. No conversion is supplied.
Private configuration files support .NET-supported Unix platforms, including musl; Windows refuses them.

## Operate

Operators own identity, TLS, deployment, backups and retention. Ledger verification detects inconsistency
against trusted checkpoints; it cannot establish independent freshness or defeat an administrator rewriting
all evidence. The supplied setup is for evaluation, with no production qualification or support SLA.

## Contribute

[Development](docs/development.md) explains the required gate and separate growth qualification.
[Architecture](docs/architecture.md#cc-arch-001) explains component ownership. Read
[Contributing](CONTRIBUTING.md), [Support](SUPPORT.md) and [Security](SECURITY.md) before submitting work or reports.
The owner reviews and merges changes; a green check records execution evidence.

Original code and documentation use the [Mozilla Public License 2.0](LICENSE). Third-party components retain
their licenses and notices. [CHANGELOG.md](CHANGELOG.md) records release changes.

See the [generated CLI reference](docs/cli.md) for verbs, inputs and choices.
