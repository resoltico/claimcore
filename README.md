# ClaimCore

A claims case register for people and automation. Record claim facts, payment decisions and
payment dates through a browser or structured CLI. Both use one authenticated HTTPS service
that validates changes and actor authorization. One PostgreSQL database holds accepted ledger
entries and current projections.

[Get started](docs/getting-started.md) · [Documentation](docs/README.md) ·
[Service and CLI](docs/api.md) · [Operations](docs/operations.md) ·
[Releases](https://github.com/resoltico/claimcore/releases)

## Case work

The [thirteen business fields](docs/domain.md) cover incident and notification details,
parties, amounts, reference, decision and payment dates, and status. Technical metadata stays
outside that record. Register or amend a case; record or withdraw a decision; record or clear
a payment date; close or reopen; correct facts atomically. Payment progress and open/closed
status are independent. ClaimCore records assertions and transfers no money.

A human data steward can void an entry error, reinstate a voided case, or erase a case.
Erasure clears the case's live ledger payloads and business projections, retaining its
reference, revision and ledger metadata. It cannot delete backups, WAL or copies elsewhere.
Read the [erasure limits](docs/operations.md#erasure-limits).

## Interfaces

| Application          | Responsibility                                                               |
| -------------------- | ---------------------------------------------------------------------------- |
| `ClaimCore.Web`      | HTTPS service, OIDC browser authentication and bearer API                    |
| `ClaimCore.Cli`      | Strict JSON frames over authenticated HTTPS, with PKCE or client credentials |
| `ClaimCore.Database` | Separate owner-only initialization, migration, provisioning and verification |

The browser offers English, Latvian and Arabic, right-to-left layout and independent display
formats. The installation's immutable IANA business time zone governs business dates.
Seven default-deny role presets and scoped grants control case work and administration.
Typed core advice drives browser business, stewardship and authority forms. Both clients use one
[consumer contract](contracts/contract-manifest.json), generated from typed core definitions, with strict byte admission and correlated delivery replies.

Every mutation rechecks locked current state. Replaying a retained accepted request returns
its original acceptance without another ledger entry. If delivery is uncertain, preserve the operation ID, expected
revision and exact request bytes and retry that request. The browser keeps one pending request
for exact retries while the page remains alive; the service stores accepted operations only.

## Run and verify

Follow [Getting started](docs/getting-started.md) for a fresh local Compose installation and
signed-in browser. Runtime private-file admission supports macOS and Linux. This setup is an
evaluation environment; operators own deployment, identity, TLS, backups and retention.
Ledger verification detects inconsistency against trusted checkpoints; it does not provide
independent freshness or protection from an administrator rewriting all evidence.

Unsupported installations are refused untouched; no conversion is supplied. Preserve them
with their matching software. See the [database boundary](docs/database.md).

[Development](docs/development.md) owns the complete gate: typed core and property tests,
architecture rules with rejecting controls, real PostgreSQL and Web integration, frontend
checks, published CLI scenarios and Chromium, Firefox and WebKit tests. PR CI uses explicitly
labeled Chromium smoke; complete qualification runs separately. A green gate is
execution evidence; the repository owner reviews and merges.

Read [Contributing](CONTRIBUTING.md), [Support](SUPPORT.md) and [Security](SECURITY.md).
Report vulnerabilities privately and never include claimant data or credentials in public reports.

Original code and documentation use the [Mozilla Public License 2.0](LICENSE).
Third-party components retain their licenses and notices. See [CHANGELOG.md](CHANGELOG.md)
for release outcomes.
