# ClaimCore

ClaimCore records claims case information, payment decisions and payment entries through a browser or CLI,
backed by one authenticated HTTPS service and PostgreSQL database. Responsibility, handling summaries
and independently versioned reminders support day-to-day case work. It records assertions; it does not
adjudicate coverage or transfer money.

## Try it

Follow [Getting started](docs/getting-started.md) for a fresh synthetic installation, sign-in and first
case. The [examples](examples/README.md) demonstrate changes through JSON calls and human commands.
Operators manage identity, access, backups and erasure of copies outside the live database.
Durable changes before 1.0 require a fresh installation.
Build with the SDK pinned in [global.json](global.json); framework-dependent CLI distributions require its matching .NET runtime.

The [HTTP contract](contracts/openapi.json) defines the endpoints. Actor endpoints negotiate HUMAN documents
with locale-neutral semantic Screens or DATA projections for automation. Native text and browser clients
request HUMAN; JSON and CSV use DATA. A UUIDv7 identifies one change, with a ten-minute first-execution window. Unconfirmed changes
are checked with `operation.get`; the browser checks them automatically.

```sh
claimcore help
claimcore help case open
claimcore schema operation.execute request
claimcore work views
claimcore operation get OPERATION_ID
```

Help and discovery work offline. Live commands require the [CLI configuration](docs/integrating.md#cli-use).

## Find your task

| Task | Guide |
| --- | --- |
| Record facts, handle work and plan reminders | [Case work](docs/case-work.md) |
| Add people and assign roles | [People and access](docs/people-and-access.md) |
| Host, administer, back up or restore | [Operating](docs/operating.md) |
| Integrate a client or automate work | [Integrating](docs/integrating.md) |
| Find verbs and flags | `claimcore help` |
| Find exit codes | `claimcore help exit-codes` |
| Build, change and verify source | [Contributing](docs/contributing.md) |
| Understand terms and language status | [Glossary](docs/glossary.md) |

## Scope comparison

| Need | ClaimCore provides | Operator or external responsibility |
| --- | --- | --- |
| Record case handling | Facts, responsibility, notes, reminders, payment decisions and entries | Coverage adjudication and real-world verification |
| Control access | OIDC principals, scoped roles and current authorization on reads and writes | Identity-provider membership and account security |
| Coordinate changes | Reviewed versions, atomic acceptance and exact-operation recovery | Resolving business conflicts and uncertain external actions |
| Inspect recorded work | Retained changes, verification and independently retained checkpoints | Evidence custody and protection against a trusted administrator rewriting it |
| Remove private contents | Case/profile erasure within the live installation | Retention and disposal of backups, exports and other copies |
| Run the service | HTTPS server, CLI and a local evaluation Compose setup | Production deployment, monitoring, certificates and backups |
| Handle money | Payment decisions and assertions of actual payments | Transfers, refunds, banking and accounting integrations |

## Languages

English is the source. The interface also offers Latvian, Arabic, Korean, Japanese, Simplified Chinese,
Traditional Chinese, Spanish and German. Language and regional formats are independent; Arabic supports
RTL. Release qualification requires current translations in every offered language. Translations are
AI-assisted and await human linguistic review; see the [language status](docs/glossary.md#language-review).

Use [Support](SUPPORT.md), [Security](SECURITY.md) and [Contributing](CONTRIBUTING.md) for reports and changes.
The owner reviews and merges work; releases distribute source through
[GitHub Releases](https://github.com/resoltico/claimcore/releases).
Original code and documentation use [AGPL-3.0-only](LICENSE). See [licensing, source and contribution terms](PROJECT-NOTICE.md) and the [commercial licensing status](LICENSE-COMMERCIAL.md). Third-party terms remain separate.
[CHANGELOG.md](CHANGELOG.md) owns release summaries.
