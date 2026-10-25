# ClaimCore agent supplement

## Owner guidance

Start with [README](README.md), the [documentation map](docs/README.md) and
[Contributing](CONTRIBUTING.md). Read [Domain](docs/domain.md) and
[Architecture](docs/architecture.md) before changing behavior, contracts, persistence or trust
boundaries. Architecture owns compiled boundaries, authorization, acceptance, recovery, replay,
durable formats and presentation ownership. [API and CLI](docs/api.md),
[Database](docs/database.md) and [Operations](docs/operations.md) own their respective interfaces,
installation admission and operational trust limits.

Follow [Contributing](CONTRIBUTING.md) for branch/PR publication and exact revision reporting.
Green checks do not grant owner approval; do not approve, merge, enable auto-merge, tag, release
or change protections without separate explicit authorization.

## Repository safeguards and checks

- Test only with synthetic data in isolated databases. Preserve adopted cases, database volumes
  and private local state. Never print credentials, connection strings or claimant payloads.
- [Development](docs/development.md#verification-stages) owns commands and qualification.
  `bash eng/verify.sh` is the complete local gate. Native tests use Microsoft Testing Platform
  and automatic `[<Tests>]` discovery, without a hand-kept registry. Focused, filtered, pending,
  skipped or retried runs do not establish qualification; complete browser qualification is
  required for PR, merge queue, main/tag and dispatch CI.
- Follow [generated-contract guidance](docs/development.md#generated-contracts-and-boundary-verification)
  for generation, exact inventory/byte checks and consumer identity. Review generated diffs with
  their typed producers. Ordinary .NET builds run no npm; web commands never invoke .NET.
- Follow [production and test lint rules](docs/development.md#changes). Suppress only one named
  rule inline, with the reason on the same line. There is no file, module or value length limit.
- Contract owner headings require unique, well-formed `CC-…` tokens. Documentation checks
  validate tokens and current relative links; read the owner heading and relevant behavior tests.
- [Private-input admission](docs/operations.md#private-configuration) governs paths, regular-file
  type, permissions and same-handle reads. Only HostSecurity may use the documented system-library
  private-file ABI adapters; ship no custom native binary. Supported ABI qualification belongs
  to [Development](docs/development.md#synthetic-growth-qualification).
- Keep generated reports and private local state in ignored paths under [.gitignore](.gitignore),
  never tracked source.

## Release notes

[CHANGELOG](CHANGELOG.md) records notable net outcomes against the relevant published baseline
for users, operators, integrators and contributors. Lead with the outcome, breaking changes and
necessary action; substantiate claims, retain material commands, public identifiers, limits and
non-guarantees, and link detailed explanations. Do not describe unfinished capabilities as delivered.

Consolidate related pending work into its final outcome. Omit routine churn and wholly reversed,
unpublished work without remaining consequences. Classify against released behavior, not commit
labels; fold repairs to unreleased functionality into the completed feature while retaining material
public-prerelease consequences. Use established categories and omit empty ones. Internal covers
noteworthy implementation, verification or release-process outcomes, not concealed compatibility
changes, build requirements or user-visible effects. Keep technical/protocol identifiers, not
development-phase labels.

Retain Unreleased and published release history, version attribution, dates, links and publication
status. Published entries may be clarified or consolidated without changing what shipped; factual
corrections require evidence. Do not prune or relocate history without explicit policy or request,
or alter tags or published artifacts when editing release notes. Release ownership remains in
[Contributing](CONTRIBUTING.md).
