# Contributing

Read [Domain](docs/domain.md), [Architecture](docs/architecture.md) and
[Development](docs/development.md) before changing behavior or verification.

Catalog owns shared vocabulary and authored presentation. Domain/Application own rules and authority; RecordFormat owns durable encodings; Wire owns typed request/reply codecs over inert Catalog vocabulary, the derived OpenAPI contract and delivery/correlation rules. Postgres
owns persistence and Hosting owns runtime composition and typed dispatch. The browser consumes
generated types and schemas; CLI consumes Wire and embedded discovery artifacts. Coordinate the
service, clients and tracked contract outputs. Keep affected callers, locks, tests and docs together.
Follow the [database compatibility policy](docs/architecture.md#durable-records) for schema changes.
Tests use synthetic data and owned isolated fixtures; preserve existing installations and private
state. Report executed commands separately from inspection, including failures and limits.

The owner reviews and merges changes. The required pull-request check is `Gate`; green CI is
not owner approval. Contributors publish exact revisions and do not merge, approve, auto-merge,
tag, release or change protections without explicit authorization. Version and published changelog
entries are release-owned. Follow the engineering principles in [AGENTS.md](AGENTS.md).

Original code and contributions use [MPL-2.0](LICENSE). Redistributors of covered binaries
or browser assets must provide corresponding covered source and tell recipients how to obtain
it. Third-party licenses and notices remain distinct.
