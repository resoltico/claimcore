# Contributing

Read [Domain](docs/domain.md), [Architecture](docs/architecture.md) and
[Development](docs/development.md) before changing behavior or verification.

Domain/Application own rules and authority, RecordFormat owns durable encodings, Contracts owns
wire definitions and typed binding, ClientProtocol owns generic admission/delivery, Postgres owns
persistence and Hosting owns runtime composition and protocol dispatch. Clients consume tracked
generated catalogues, builders and complete decoders; keep client release artifacts coordinated. Keep affected
consumers, locks, tests and documentation together. Follow the [database compatibility policy](docs/architecture.md#durable-records)
for schema changes. Tests use synthetic data and owned isolated fixtures; preserve existing installations and
private state. Report executed commands separately from inspection, including failures and limits.

The owner reviews and merges changes. The required pull-request check is `Gate`; green CI is
not owner approval. Contributors publish exact revisions and do not merge, approve, auto-merge,
tag, release or change protections without explicit authorization. Version and published changelog
entries are release-owned. Follow the engineering principles in [AGENTS.md](AGENTS.md).

Original code and contributions use [MPL-2.0](LICENSE). Redistributors of covered binaries
or browser assets must provide corresponding covered source and tell recipients how to obtain
it. Third-party licenses and notices remain distinct.
