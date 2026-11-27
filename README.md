# ClaimCore

ClaimCore records claims case information through a browser or CLI, backed by an authenticated HTTPS
service and PostgreSQL. Record claim facts, parties, related cases, evidence attachments, payment
decisions, payments and returns. Assign responsibility, plan deadlines and report on the cases you
can read.

## Try it

Follow [Getting started](docs/getting-started.md) for a fresh synthetic installation, sign-in and first
case. Use the [synthetic examples](examples/README.md) to try structured requests and human commands.
For hosting and matching artifacts, follow [Operating](docs/operating.md#install-from-release-artifacts).
For source builds, use the pinned toolchains in [Contributing](docs/contributing.md#first-checkout).

The [HTTP contract](contracts/openapi.json) defines the endpoints. Actor endpoints negotiate HUMAN documents
with locale-neutral semantic screens or DATA projections for automation. Browser and CLI text views
use HUMAN. Automation can read typed DATA JSON or export CSV.
[Delivery recovery](docs/integrating.md#recover-an-uncertain-change) explains how to check an
unconfirmed change without recording it again.

```sh
claimcore help
claimcore help case open
claimcore schema operation.execute request
```

Help and schema inspection work offline. After [configuring the CLI](docs/integrating.md#cli-use),
run `claimcore work views` to inspect work queues or `claimcore operation get OPERATION_ID` to check a change.

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
| Trace a behavior to its source and checks | [Source guide](docs/source-guide.md) |
| Understand terms and language status | [Glossary](docs/glossary.md) |

## Scope comparison

| Need | ClaimCore provides | Operator or external responsibility |
| --- | --- | --- |
| Record case handling | Permanent server-issued case references, claim facts, parties, related cases, notes, assignments, reminders and deadlines | Coverage adjudication, fact checking and handling decisions |
| Handle evidence files | Case-scoped attachments, verified PDF and image previews where eligible, exact downloads and withdrawal | Evidence collection, classification and control of exported copies |
| Handle money | Recorded payment decisions, payments and returns, with net totals by currency | Transfers, refunds, banking and accounting |
| Find and report on work | Authorized case and reminder queues, grouped case summaries, bounded CSV downloads and an optional daily count-only reminder digest | Configuring the mail relay and controlling the use, retention and disposal of exported copies |
| Control access | OIDC sign-in, scoped roles, current read/write authorization and browser-session termination | Identity-provider membership, credential revocation and account security |
| Coordinate changes | Review, version checks, atomic acceptance and exact-operation recovery | Resolving business conflicts and confirming external actions |
| Verify and recover recorded work | Authorized receipts and history, a disclosure trail for covered reads and exports, checkpoint verification, projection reconstruction and exact attachment repair | Independent checkpoint custody, backup and WAL recovery, and trusted administrator access |
| Erase private contents | Case and profile erasure in the live register | Retention and disposal of WAL, backups, replicas and other copies |
| Run the service | Attested server image and CLI archives, a production Compose template and a local evaluation setup | Deployment, capacity, monitoring, HTTPS credentials and backups |

## Languages

English is the source. The interface also offers Latvian, Arabic, Korean, Japanese, Simplified Chinese,
Traditional Chinese, Spanish and German. Language and regional formats are independent. Arabic
supports right-to-left layout. Translations are AI-assisted and await human linguistic review.
See [language review](docs/glossary.md#language-review).

Use [Support](SUPPORT.md), [Security](SECURITY.md) and [Contributing](CONTRIBUTING.md) for reports and changes.
The owner reviews and merges work. Releases provide source and CLI archives with checksums and record
the corresponding server image digest. See [GitHub Releases](https://github.com/resoltico/claimcore/releases).
Original code and documentation use [AGPL-3.0-only](LICENSE). See [licensing, source and contribution terms](PROJECT-NOTICE.md) and the [commercial licensing status](LICENSE-COMMERCIAL.md). Third-party terms remain separate.
[CHANGELOG.md](CHANGELOG.md) owns release summaries.
