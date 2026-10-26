# Contributing

Start with the [domain guide](docs/domain.md), then the [architecture](docs/architecture.md) and
[development workflow](docs/development.md). Read the behavior tests and owning code before changing a contract.
Challenge consequential assumptions with direct observations and independent examples.

Keep a change complete across its consumers. Contract owns public models, vocabulary, field identities,
codecs and the operation catalog. Domain/Application decide behavior and authorization; RecordFormat owns
durable bytes. Postgres persists decisions, Web composes the service, and the clients derive operation
syntax and bindings from Contract. Regenerate all five outputs through the generator and review their
diffs. Update affected callers, SQL, lockfiles, tests, locales and documentation.
Follow the [database compatibility policy](docs/architecture.md#durable-records).

Use synthetic data and owned isolated fixtures. Preserve existing installations, private files and unrelated
work. Run the required gate from an isolated clean checkout, report failures as well as successes, and distinguish
partial checks from complete qualification. Growth qualification is separate and runs before releases or on demand.
Follow the repository guidance in [AGENTS.md](AGENTS.md).

The owner reviews and merges. Gate is the required pull-request check; passing it records execution evidence.
Publishing changes does not authorize merge, approval, auto-merge, tagging, release or protection changes.
Version bumps, tags and releases remain owner-controlled. Keep the unreleased changelog entry to at most
15 bullets. Keep each released section to at most 10 concise bullets and link its GitHub release notes,
which remain the authoritative detailed historical record. Preserve released sections and requirement anchors.

Original code and contributions use [MPL-2.0](LICENSE). Covered binary/browser redistribution must provide
corresponding covered source and tell recipients how to obtain it. Third-party licenses/notices remain distinct.
