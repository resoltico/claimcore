# Contributing

Start with the [domain guide](docs/domain.md), then the [architecture](docs/architecture.md) and
[development workflow](docs/development.md). Read the behavior tests and owning code before changing a contract.
Challenge consequential assumptions with direct observations and independent examples.

Keep a change complete across its consumers. Contract owns public models, vocabulary, field identities and
codecs; Domain/Application decide behavior and authorization; RecordFormat owns durable bytes. Postgres persists
decisions, Web composes the service, and CLI owns command syntax. Regenerate the three tracked contracts through
the generator and review their diffs. Update affected callers, SQL, lockfiles, tests, locales and documentation.
Follow the [database compatibility policy](docs/architecture.md#durable-records).

Use synthetic data and owned isolated fixtures. Preserve existing installations, private files and unrelated
work. Run the required gate from an isolated clean checkout, report failures as well as successes, and distinguish
partial checks from complete qualification. Growth qualification is separate and runs before releases or on demand.
Follow the repository guidance in [AGENTS.md](AGENTS.md).

The owner reviews and merges. Gate is the required pull-request check; passing it records execution evidence.
Publishing changes does not authorize merge, approval, auto-merge, tagging, release or protection changes.
Version and published changelog entries remain release-owned.

Original code and contributions use [MPL-2.0](LICENSE). Covered binary/browser redistribution must provide
corresponding covered source and tell recipients how to obtain it. Third-party licenses/notices remain distinct.
