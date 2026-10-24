# Contributing

Read [Domain](docs/domain.md), [Architecture](docs/architecture.md) and
[Development](docs/development.md) before changing behavior or verification.

Contract owns shared public models, typed codecs, the endpoint registry, vocabulary and source
locale JSON. Domain/Application own decisions and authorization; RecordFormat owns versioned
durable encodings. Postgres persists decisions and Web owns runtime composition/dispatch.
CLI references Contract/HostSecurity; browser and CLI consume exact interaction bindings and generated executable validation, with typed
descriptors and independently checked wire corpora. Coordinate service, clients and the three tracked generated
artifacts; keep affected callers, lockfiles, tests and docs together. Challenge assumptions against
current code, standards and independent tests before building around them. A design's adoption
does not make a newly discovered defect correct; resolve it explicitly with evidence.
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
