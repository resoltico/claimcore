# ClaimCore

ClaimCore records claims case information, payment decisions and payment entries through a browser or CLI,
backed by one authenticated HTTPS service and PostgreSQL database. Responsibility, handling summaries
and independently versioned reminders support day-to-day case work. It records assertions; it does not
adjudicate coverage or transfer money.

## Try it

Follow [Getting started](docs/getting-started.md) for a fresh synthetic installation, sign-in and first
case. The [examples](examples/README.md) demonstrate nine changes through JSON calls and human commands.
Operators manage identity, access, backups and erasure of copies outside the live database.
Durable changes before 1.0 require a fresh installation.
Build with the SDK pinned in [global.json](global.json); framework-dependent CLI distributions require its matching .NET runtime.

The service has [21 HTTP endpoints](contracts/openapi.json). Actor endpoints negotiate HUMAN documents
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

## Languages

English is the source. Language and regional formats are independent; Arabic supports RTL. Each
translated language below records **AI-assisted revision 2026-11; human review pending**.
Catalog coverage is required for en, lv and ar. Other languages are drafts with runtime English
fallback when a translation is missing or stale.

| Language | Status |
| --- | --- |
| English | Source wording |
| Latvian | AI-revised; human review pending |
| Arabic | AI-revised; human review pending |
| Korean | Draft; human review pending |
| Japanese | Draft; human review pending |
| Simplified Chinese | Draft; human review pending |
| Traditional Chinese | Draft; human review pending |
| Spanish | Draft; human review pending |
| German | Draft; human review pending |

See the current [English workspace](docs/images/foundations/case-workspace-light.png),
[Arabic RTL payment cards](docs/images/foundations/payment-cards-ar.png) and the
[workspace examples](docs/workspace-examples.md).

Korean 청구 건 (case) and 후속 조치 (reminder) need native confirmation. Automated checks do not establish
human linguistic or assistive-technology approval. See the [glossary](docs/glossary.md#language-review).

Use [Support](SUPPORT.md), [Security](SECURITY.md) and [Contributing](CONTRIBUTING.md) for reports and changes.
The owner reviews and merges work; releases distribute source through
[GitHub Releases](https://github.com/resoltico/claimcore/releases).
Original code and documentation use [AGPL-3.0-only](LICENSE). See [licensing, source and contribution rights](PROJECT-NOTICE.md) and the [pending commercial route](LICENSE-COMMERCIAL.md). Third-party terms remain separate.
[CHANGELOG.md](CHANGELOG.md) owns release summaries.
