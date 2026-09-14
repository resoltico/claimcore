# Coding-agent instructions

Start with the [README](README.md) and [documentation map](docs/README.md) to find owner documents.
Read [Contributing](CONTRIBUTING.md) before edits; read [domain](docs/domain.md) and
[architecture](docs/architecture.md) before changing behavior, contracts, persistence, recovery, or
trust boundaries. [Development](docs/development.md) owns commands and gates.

## Product boundaries

- `CaseFields` has exactly thirteen business fields. Revisions, operation IDs, timestamps,
  attribution, and available commands remain outside it.
- Domain and Application own business rules, transitions, available commands, outcomes, and
  recovery decisions. Normal callers use typed `IClaimsCore`, with recovery only through its
  `Recovery` member. CLI and Web render core outcomes; they do not invent rules or receive stores,
  retained preparations, recovery ports, or transition callbacks.
- RecordFormat owns canonical operation and snapshot encoding. An uncertain retry must preserve the
  exact operation ID and request bytes; never rebase it or infer that a commit failed.
- Operation identity, authority, and knowledge are separate. Accepted history proves acceptance;
  durable revocation ends future unaccepted authority without rewriting old attempt uncertainty.
  Recovery list defaults to pending work, detailed attempts are operation-bound and paged, and a
  pruned revoked preparation may expose only its tombstone.
- Native `IClaimsCore.Prepare` and `Execute` accept Domain `CommandRequest`. CLI and Web bind their
  generated form shape once through `Drafts.bind`; do not add another public raw-field boundary.
- `CORRECT_CASE` has three explicit tagged groups. Keep reads current accepted state; never add a
  business field, broaden historical `AMEND_REGISTRATION`, or accept an adapter-only correction rule.
- Migration 006 stores the installation's immutable canonical IANA business time zone. Runtime
  opening requires it; derive business dates from one captured instant and that stored zone, never
  `DateTime.Today`, `TimeZoneInfo.Local`, or a host environment default.
- Never edit an applied migration. Add an ordered one and update readers, writers, constraints,
  contracts, tests, and documentation together.

## Safety and evidence

- Test only with synthetic data in isolated databases. Preserve adopted cases, database volumes,
  private `.local` state, and applied migration bytes. Never print secrets, connection strings,
  recovery bytes, or claimant payloads.
- Keep contract headings in their registered owner documents and evidence-test leaf names prefixed
  with one matching `[CC-…]` token. Reattest `eng/ClaimCore.Docs/contract-reviews.json` only after
  reviewing the exact heading and assertion sources.
- Required .NET tests use native .NET 10 Microsoft Testing Platform commands and exact registered
  counts. Focused, pending, skipped, expected-failure, conditional, filtered, retried, or sharded
  runs cannot satisfy complete verification.
- Enforce size, complexity, and lint limits across production and test code without grandfathering.
  [`analyzer-suppressions.json`](analyzer-suppressions.json) is the sole source-code exception
  registry.
  Follow [repository quality](docs/development.md#repository-quality): entries need an owner,
  exact file/rule/scope, substantive rationale, and an ISO review or expiry date. Directives need
  nearby rationale and a `suppression-registry: file|rule|scope` reference. Unregistered bypasses
  fail.
- Ordinary .NET builds do not run npm. Produce Web assets with the locked frontend and publish only
  bytes matching its source, lock, contract, and toolchain manifest.
- Report commands actually executed and their outcomes separately from source inspection. Keep
  generated reports and private local state in ignored paths, never tracked source.
