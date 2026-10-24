# ClaimCore

A claims case register for people and automation. Record claim facts, payment decisions and
payment dates through a browser or human verbs and structured CLI. Both use one authenticated HTTPS service
that validates changes and actor authorization. One PostgreSQL database holds accepted ledger
entries and current projections.

[Get started](docs/getting-started.md) · [Documentation](docs/README.md) ·
[Service and CLI](docs/api.md) · [Operations](docs/operations.md) ·
[Releases](https://github.com/resoltico/claimcore/releases)

## Case work

The [business fields](docs/domain.md) cover incident and notification details,
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
| `ClaimCore.Cli`      | Human verbs and JSON frames over HTTPS, with PKCE or client credentials |
| `ClaimCore.Database` | Separate owner-only initialization, provisioning and verification |

The browser offers English, Latvian and Arabic, right-to-left layout and independent display
formats. The installation's immutable IANA business time zone governs business dates.
Seven default-deny role presets and scoped grants control case work and administration.
Typed core advice drives browser business, stewardship and authority forms. Both clients use one
[OpenAPI contract](contracts/openapi.json), generated from typed core definitions, with strict byte admission and correlated delivery replies.

`claimcore case open ...`, `claimcore case get <reference>` and `claimcore authority grant ...`
serve human callers; `--dry-run` prints the equivalent JSON frame without authentication.
See [examples](examples/README.md). The nine-project [architecture](docs/architecture.md#cc-arch-001)
keeps one pure Contract model/codec/registry, Domain/Application decisions and Web composition.

Every mutation rechecks locked current state. Replaying a retained accepted request returns
its original acceptance without another ledger entry. If delivery is uncertain, preserve the operation ID, expected
revision and exact request bytes and retry that request. Submitted bodies are protected for seven days for explicit actor-bound recovery; reload lists metadata only.
Known-result acknowledgement reclaims bounded journal capacity. Forget/expiry never prove non-acceptance.

## Run and verify

Follow [Getting started](docs/getting-started.md) for a fresh local Compose installation and
signed-in browser. Private-file admission supports macOS x64/arm64 and glibc Linux x64/arm64; other ABIs refuse. This setup is an
evaluation environment; operators own deployment, identity, TLS, backups and retention.
Ledger verification detects inconsistency against trusted checkpoints; it does not provide
independent freshness or protection from an administrator rewriting all evidence.

Unsupported installations are refused untouched; no conversion is supplied. Preserve them
with their matching software. See the [database boundary](docs/database.md).

[Development](docs/development.md) owns the complete gate: typed core and property tests,
architecture rules with rejecting controls, real PostgreSQL and Web integration, frontend
checks, published CLI scenarios and Chromium, Firefox and WebKit tests. PR and merge queue CI require complete qualification on Chromium, Firefox and WebKit. A green gate is
execution evidence; the repository owner reviews and merges.

Read [Contributing](CONTRIBUTING.md), [Support](SUPPORT.md) and [Security](SECURITY.md).
Report vulnerabilities privately and never include claimant data or credentials in public reports.

Original code and documentation use the [Mozilla Public License 2.0](LICENSE).
Third-party components retain their licenses and notices. See [CHANGELOG.md](CHANGELOG.md)
for release outcomes.
