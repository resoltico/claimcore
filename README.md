# ClaimCore

ClaimCore records claims case information, payment decisions and payment dates through a browser or CLI,
backed by one authenticated HTTPS service and PostgreSQL database. Responsibility, handling summaries
and independently versioned reminders support day-to-day case work. It records assertions; it does not
adjudicate coverage or transfer money.

## Try it

Follow [Getting started](docs/getting-started.md) for a fresh synthetic installation, sign-in and first
case. The [examples](examples/README.md) demonstrate nine changes through JSON calls and human commands.
Operators manage identity, access, backups and erasure of copies outside the live database.
Durable changes before 1.0 require a fresh installation.

The service has [19 HTTP endpoints](contracts/openapi.json). Successful reads and accepted receipts
include a locale-neutral record/table `view`; browser and CLI render it generically. JSON and CSV retain
typed data. A UUIDv7 identifies one change, with a ten-minute first-execution window. Unconfirmed changes
are checked with `operation.get`; the browser checks them automatically.

```sh
claimcore help
claimcore describe case open
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
| Find verbs, flags and exit codes | [CLI reference](docs/cli.md) |
| Build, change and verify source | [Contributing](docs/contributing.md) |
| Understand terms and language status | [Glossary](docs/glossary.md) |

## Languages

English is the source. Language and regional formats are independent; Arabic supports RTL. Each
translated language below records **AI-assisted revision 2026-11; human review pending**.
Catalog coverage is required for en, lv and ar. Other languages are drafts with generated English
fallback when a translation is missing or stale.

| Language | Status | Workspace and review |
| --- | --- | --- |
| English | Source wording | [Screenshot](docs/images/localization/en.png) |
| Latvian | AI-revised; human review pending | [Screenshot](docs/images/localization/lv.png) |
| Arabic | AI-revised; human review pending | [Screenshot](docs/images/localization/ar.png) |
| Korean | Draft; human review pending | [Screenshot](docs/images/localization/ko.png) |
| Japanese | Draft; human review pending | [Screenshot](docs/images/localization/ja.png) |
| Simplified Chinese | Draft; human review pending | [Screenshot](docs/images/localization/zh-Hans.png) |
| Traditional Chinese | Draft; human review pending | [Screenshot](docs/images/localization/zh-Hant.png) |
| Spanish | Draft; human review pending | [Screenshot](docs/images/localization/es.png) |
| German | Draft; human review pending | [Screenshot](docs/images/localization/de.png) |

Korean 청구 건 (case) and 후속 조치 (reminder) need native confirmation. Automated checks do not establish
human linguistic or assistive-technology approval. See the [glossary](docs/glossary.md#language-review).

Use [Support](SUPPORT.md), [Security](SECURITY.md) and [Contributing](CONTRIBUTING.md) for reports and changes.
The owner reviews and merges work; releases distribute source through
[GitHub Releases](https://github.com/resoltico/claimcore/releases).
Code and documentation use [MPL 2.0](LICENSE); third-party components retain their licenses/notices.
[CHANGELOG.md](CHANGELOG.md) owns release summaries.
