# Coding-agent instructions

Read [README.md](README.md), [the domain contract](docs/domain.md),
[architecture](docs/architecture.md), and [CONTRIBUTING.md](CONTRIBUTING.md).

- `CaseFields` has exactly thirteen business fields. Keep revisions, operation IDs, timestamps,
  attribution, and available commands outside it.
- Domain and Application own validation, transitions, asynchronous core outcomes, and failure meaning.
  Contracts owns exact CLI/Web schemas, pure wire codecs, generated DTOs, and conformance corpora;
  Protocol contains only its generated client-safe .NET bindings and pure bounded codecs, never
  native server implementations. F02 does not convert the current native CLI;
  HostSecurity owns shared handle-first private-file admission; CLI and Web handle process/HTTP
  presentation, Database handles schema-owner administration, and PostgreSQL owns durable storage,
  structural constraints, and technical preparation recovery.
- Use typed `IClaimsCore.Describe`, `Prepare`, `Execute`, `Get`, `List`, `History`, and
  `ObserveOperation`; recovery is available only through `IClaimsCore.Recovery`. Adapters render
  endpoint-specific outcomes and must not expose a store, retained preparation, recovery port, or
  transition callback.
- Keep canonical operation and snapshot encoding in RecordFormat. Preserve the exact operation ID and
  request bytes when retrying an uncertain result.
- Never edit an applied migration. Add an ordered migration and update readers, writers, constraints,
  tests, schemas, and documentation together.
- Use only synthetic test data and isolated test databases. Never print connection strings, secrets,
  or case payloads in diagnostics.
- Keep contract headings in their registered owner documents. Evidence-test leaf names start with one
  matching `[CC-…]` token; update `eng/ClaimCore.Docs/contract-reviews.json` only after reviewing the
  exact heading and registered assertion sources.
- Use the native .NET 10 MTP commands and exact registered counts. Focused, pending, skipped,
  expected-failure, conditional, filtered, retried, or sharded required tests are forbidden.
- Ordinary .NET builds do not run npm. Produce Web assets once with the locked frontend, then require
  its source/lock/contract/toolchain manifest when publishing those exact bytes.
- [`analyzer-suppressions.json`](analyzer-suppressions.json) is the sole exception registry. Every
  entry requires an `owner`, a substantive rationale, and an ISO `reviewOn` or `expiresOn` date.
  Unregistered inline disables, `#nowarn`, and project-level warning exclusions fail policy.
- Report commands actually executed and their outcomes separately from source inspection.

The canonical workflow is locked `dotnet` restore/build, explicit native-MTP projects,
`eng/Check-Fantomas.sh`, `eng/Check-FSharpLint.sh`, frontend assurance, documentation checking, and published acceptance; see
[Development](docs/development.md). Keep generated reports and local state out of the repository.
