# ClaimCore

ClaimCore is a claims case register for people and automation. It records incident facts, payment
decisions and payment dates through a browser or CLI, backed by one authenticated HTTPS service and
PostgreSQL database. It records assertions about a claim; it does not adjudicate coverage or transfer money.

## Try it

Follow [Getting started](docs/getting-started.md) to create a fresh synthetic installation, sign in and
record a case. The walkthrough includes isolated certificate trust and owner setup.

For a source checkout with the [declared toolchains](docs/contributing.md#first-checkout) installed:

```sh
bash eng/verify.sh --fast
```

The [contribution guide](docs/contributing.md) distinguishes fast checks, complete qualification and growth.
Its [project boundaries](docs/contributing.md#project-boundaries) and
[source vocabulary](docs/contributing.md#source-vocabulary) explain the implementation responsibilities.

## Find your task

| Task | Guide |
| --- | --- |
| Record and correct case facts | [Case work](docs/case-work.md) |
| Host, administer, back up or restore an installation | [Operating](docs/operating.md) |
| Connect a client or automate work | [Integrating](docs/integrating.md) |
| Find a CLI verb or flag | [Generated CLI reference](docs/cli.md) |
| Build or change the source | [Contributing](docs/contributing.md) |
| Understand project terms | [Glossary](docs/glossary.md) |

The browser offers English, Latvian and Arabic, with right-to-left layout and separate display preferences.
The [synthetic examples](examples/README.md) demonstrate equivalent human commands and JSON calls.
Operators manage identity, access and data custody; start with the operating guide before using real data.

ClaimCore is pre-1.0 software supplied for evaluation, without production qualification or a support SLA.
See [Support](SUPPORT.md), [Security](SECURITY.md) and [Contributing](CONTRIBUTING.md) for reports and changes.
The owner reviews and merges work. Releases distribute source through
[GitHub Releases](https://github.com/resoltico/claimcore/releases).

Original code and documentation use the [Mozilla Public License 2.0](LICENSE). Third-party components retain
their licenses and notices. [CHANGELOG.md](CHANGELOG.md) records release changes.
