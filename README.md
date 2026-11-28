# ClaimCore

ClaimCore is a claims register with a Web workspace, CLI and authenticated HTTPS API backed by
PostgreSQL. Record claim facts, parties, related cases and evidence. Assign responsibility, plan work
and record financial decisions and transfers. Work queues and reports respect current read access.

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
| Handle evidence files | Case-scoped attachments, eligible PDF and image previews, exact downloads and withdrawal | Evidence collection, classification, malware scanning and control of downloaded copies |
| Record financial activity | Payment decisions, payments and returns, selective corrections and exact net balances by currency. Financial exceptions appear in case attention | Executing transfers and refunds, reconciling bank records and accounting |
| Find and report on work | Case and reminder queues, grouped summaries, case-and-currency CSV rows, bounded downloads and CLI page streaming. Optional daily reminder emails contain counts and a queue link | Mail relay configuration and export retention and disposal |
| Control access | OIDC sign-in, scoped roles, current read and write authorization, and browser-session termination | Identity-provider membership, provider credential revocation and account security |
| Coordinate changes | Review, version checks, atomic acceptance and recovery by the original operation ID | Resolving business conflicts and confirming external actions |
| Verify and recover recorded work | Authorized receipts and history, an access and export log, checkpoint verification, projection rebuild and exact attachment payload repair | Running the independent PostgreSQL recovery witness, keeping trusted checkpoints and restoring backups and WAL |
| Erase private contents | Erasure of private case and profile contents in the live register. Permanent case references and retained ledger metadata remain | Managing retention and disposal of WAL, backups, replicas and other copies |
| Run the service | Attested Linux x64 server image and CLI archives, a production Compose template and a separate evaluation setup | Hosting, capacity, monitoring, HTTPS credentials, key custody and backups |

## Languages

English is the source. The interface also offers Latvian, Arabic, Korean, Japanese, Simplified Chinese,
Traditional Chinese, Spanish and German. Language and regional formats are independent. Arabic
supports right-to-left layout. Translations are AI-assisted and await human linguistic review.
See [language review](docs/glossary.md#language-review).

## Support and licensing

Use [Support](SUPPORT.md), [Security](SECURITY.md) and [Contributing](CONTRIBUTING.md) for reports and
changes. Releases provide source and CLI archives, checksums and the server image digest.
See [GitHub Releases](https://github.com/resoltico/claimcore/releases) and the [changelog](CHANGELOG.md).

Original code and documentation use [AGPL-3.0-only](LICENSE). Third-party components retain their
own terms. See [licensing, source and contribution terms](PROJECT-NOTICE.md) and the
[commercial licensing status](LICENSE-COMMERCIAL.md).
