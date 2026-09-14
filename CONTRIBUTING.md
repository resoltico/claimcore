# Contributing

ClaimCore favors explicit boundaries and executable evidence. Read the [domain
contract](docs/domain.md) and [architecture](docs/architecture.md) before changing behavior.

## Setup and verification

Install the prerequisites and complete the locked first-checkout workflow in
[Development](docs/development.md#first-checkout). That document is the sole owner of contributor
build, test, lint, coverage, published-acceptance, and evidence commands. Run its
[complete verification](docs/development.md#complete-verification) before claiming a change is ready.

Docker is required by PostgreSQL and published-application tests. Required test projects must run
through the native .NET 10 Microsoft Testing Platform commands with the exact registered counts.
Focused, pending, skipped, expected-failure, conditional, filtered, retried, or sharded required
tests are policy failures.

## Change rules

- Preserve the thirteen-field contract unless the change explicitly revises product scope.
- Put business decisions in Domain/Application, durable encoding in RecordFormat, wire schemas and
  codecs in Contracts, persistence/runtime composition in Postgres, and process/HTTP presentation in
  CLI/Web.
- Add tests at the narrowest useful layer. Include PostgreSQL tests for SQL, schema changes,
  transactions, connection admission, and stored representations.
- Never edit an applied migration. Add an ordered migration and test a fresh database and upgrade
  from the previous supported schema state.
- Keep protocol, capability, Web contract, example, documentation, schema, and migration projections
  synchronized with behavior.
- Keep contract headings, `[CC-…]` evidence leaves, and review-subject hashes synchronized. A passing
  tagged test does not substitute for semantic review of its assertions.
- Review architecture policy changes against the actual module boundaries, compiled inspection,
  evaluated project graph, and required report evidence; do not widen permitted edges merely to
  clear the gate.
- Update each dependency in its owning manifest and commit reviewed lock-file changes.
- Do not commit local connections, credentials, build output, test reports, container state, or real
  case data.

FSharpLint, ESLint, and Stylelint own product/test file-size, function-size, complexity, nesting, and
specificity limits. A limit failure is a design prompt: split a real responsibility instead of
adding wrappers or suppressing the rule.

[`analyzer-suppressions.json`](analyzer-suppressions.json) is the sole authoritative source-code
exception registry. Do not add an inline or project-level bypass merely to clear a gate; the exact
registry schema and non-suppressible rules are owned by [Repository
quality](docs/development.md#repository-quality).

Container vulnerability exceptions are separate. Every entry in
[`container-vulnerability-exceptions.yaml`](container-vulnerability-exceptions.yaml) must be narrowly
scoped, technically justified, and short-lived.

## Pull-request evidence

Describe the behavior changed, compatibility or schema impact, and the exact commands actually run.
Do not report a configured or source-inspected check as executed. State unavailable prerequisites
explicitly.

Use only synthetic examples. Security-sensitive findings and data-bearing diagnostics follow
[SECURITY.md](SECURITY.md), not a public issue or ordinary test attachment. General issue guidance is
in [SUPPORT.md](SUPPORT.md).

## Licensing contributions

ClaimCore is licensed under the [Apache License 2.0](LICENSE). Unless explicitly designated in
writing as “Not a Contribution,” an intentional contribution submitted for inclusion in ClaimCore is
provided under Apache-2.0 as described by section 5 of the license. Submit only work that you have
the right to license, and retain applicable third-party copyright and attribution notices.
