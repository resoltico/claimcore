# Contributing

Use this guide to build, change and verify ClaimCore. User tasks belong in [Case work](case-work.md),
client contracts in [Integrating](integrating.md), and deployment tasks in [Operating](operating.md).

## First checkout

Install the SDK from [global.json](../global.json), Node from [.node-version](../.node-version), and npm
from [web/package.json](../web/package.json). Bash and Python 3 are required. Docker is required for
PostgreSQL, Web and published acceptance tests; workflow checks also require ShellCheck.
NSS `certutil` is required by certificate acceptance (`nss` on Homebrew, `libnss3-tools` on Ubuntu).
The [CI workflow](../.github/workflows/ci.yml) declares the supported verification platforms.

From the repository root:

```sh
dotnet tool restore
dotnet restore ClaimCore.slnx --locked-mode
npm --prefix web ci
dotnet build ClaimCore.slnx --configuration Debug --no-restore
```

Ordinary .NET builds do not run npm or generation. The frontend builds independently with
`npm --prefix web run build`. Publish the complete apphost directory, including its notices and assets.

## Project boundaries

The [adopted foundations design](design/foundations.md) records the ownership and replacement decisions.

| Project | Responsibility |
| --- | --- |
| Json | Strict JSON, codecs, schemas and writers |
| Domain | Values, acts, transitions, authority and business refusals |
| Application.Contracts | Use-case contracts, semantic tasks, typed assembly, outcomes and pages |
| RecordFormat | Current durable grammars, literal spellings, limits and cursors |
| Application | Transactions, replay, operation lookup, projections and verification |
| Presentation | Semantic regions, screen presenters, messages and language policy |
| Wire | Endpoints, transport codecs, catalog, build and legal identity; references Presentation |
| Postgres | Application ports, transactions, projections and database administration |
| HostSecurity | Private files, HTTPS origins and TLS purposes |
| Web | Authentication, request gates, host bindings and static assets |
| Cli | HTTPS client, command grammar, local recovery and terminal rendering |
| Database | Owner administration commands |

The [evaluated edge policy](../tests/ClaimCore.Tests/Architecture/ProjectReferences.fs) owns exact project
references. Domain is independent of transport and JSON. Application and Postgres do not depend on Wire
or Presentation. The published CLI excludes Application and Postgres. Product friendship is limited to tests.

## Source vocabulary

Change a value in its owning Domain module. [CaseFields](../src/ClaimCore.Domain/CaseFields.fs) declares
field kinds and money relationships; typed records retain validated structure through execution.
[QueryFields](../src/ClaimCore.Application.Contracts/QueryFields.fs) owns search and machine summary
selection. [FieldPresentation](../src/ClaimCore.Presentation/Catalog/FieldPresentation.fs) owns human
listing and prominence. Human layout changes do not change Domain or the machine summary selection.
[Identifiers](../src/ClaimCore.Domain/Identifiers.fs) owns UUIDv7 operation IDs and their lifetime.
`Lifecycle.after` and `ReminderState.after` own transitions used by execution and verification.
[TaskDeclarations](../src/ClaimCore.Application.Contracts/TaskDeclarations.fs) owns finite semantic task
inputs and choices; [TaskAssembly](../src/ClaimCore.Application.Contracts/TaskAssembly.fs) assembles
selected authored values with a real reviewed basis. Presentation attaches wording to those identities.
Generated typed constructors live in `contracts/client.ts`; collection row keys stay outside commands.

RecordFormat owns canonical business-input codecs and the single installation format. Wire embeds
those codecs and owns transport choices. Domain vocabularies supply business tokens. [Compiled negative controls](../tests/ClaimCore.Tests/Architecture/NegativeFixtures.fs)
exercise the architecture boundaries, including synchronous and asynchronous calls.

`Codec.validate` admits strict structural JSON without invoking semantic constructors. `Read` and
`Codec.decode` also interpret typed values; `interpret`, `refine` and `buildResult` can apply additional
core constraints without changing the schema. Structural `restrict` declarations provide an explicit
raw validator. Check native and generated structural admission against authored examples; arbitrary
codec records and interpretation predicates do not establish schema equivalence.

[IStore](../src/ClaimCore.Application/LedgerStorePort.fs) supplies snapshot read and head-locked write
callbacks. Writes decide against that locked state and commit only an accepted result. A final absence
lookup uses the write callback without appending. Current authorization governs every disclosure.

Pre-1.0 durable changes require a fresh installation. There are no migrations or cross-version readers.
The [SQL baseline](../db/baseline.sql) enforces structure, append-only storage and erasure authorization;
RecordFormat decoding and verification enforce act meaning. Do not move business vocabulary into SQL.

## Generated contracts

After changing a declaration, codec, message or language policy:

```sh
dotnet run --project eng/ClaimCore.ContractGenerator --no-restore -- --output contracts
dotnet run --project eng/ClaimCore.ContractGenerator --no-restore -- --check
```

The generator owns only [contracts](../contracts/). Presentation resolves authored catalogs at runtime for the host and CLI.
Check mode compares generated contents and does not repair them. Review changes with their producers.
A custom output directory contains the contract artifacts only.

## Core presentation and copy

Host bindings compute locale-neutral screens with semantic regions through
[screen presenters](../src/ClaimCore.Presentation/Presentation/Screens). Actor replies negotiate distinct
HUMAN documents and DATA projections. A HUMAN document owns Screen, bounded interaction context,
observation and disclosure; DATA carries no Screen. Browser and native text consume supplied documents,
while JSON and CSV consume DATA. Review documents carry changes and bindings without full snapshots.
[Task declarations](../src/ClaimCore.Application.Contracts/TaskDeclarations.fs) own semantic inputs and choices;
[typed assembly](../src/ClaimCore.Application.Contracts/TaskAssembly.fs) owns their command construction.

Keep action eligibility, lifecycle, reminder timing, disclosure and recommendations in their core owners.
A client formats values and handles interaction; it does not compare replies against its submitted
business facts. Preview and execution retain the typed operation-ID echo check.

English strings are the source in three authored catalog directories: Presentation, CLI and web.
Each non-English entry is `{ "text": "…", "source": "…" }`, where `source` is the first twelve lowercase
hex digits of SHA-256 over the English text's UTF-8 bytes. Translate the changed English, then record its
hash. Presentation fills stale or missing entries with English at load time. The generator checks the same resolver and reports translation issues on stderr.

`--check` gates translation coverage for `en`, `lv` and `ar`. Other languages warn; malformed templates,
invalid placeholders and forbidden glossary spellings still refuse generation. Generated outputs must
match in every case. The [language policy](../src/ClaimCore.Presentation/Messages/Localization/LocalizationPolicy.fs)
owns required languages, aliases, direction and regional defaults.

Use the [glossary](glossary.md). Keep GUI hints short and put exact syntax in rules. Shared scalar wording
uses input-specific arguments derived by `ScalarGuidance`; equal rule text does not imply equal bounds.
Merge keys only when their concepts agree. Keep grammatical gender and singular/plural contexts separate.
Use labelled counts, rather than choosing a singular template from a numeric comparison.
Automated checks establish structure, not human linguistic approval; record the language and review scope.

## Theme and layout

[Theme tokens](../web/src/theme/tokens.json) own colours; run `npm --prefix web run theme` after editing
and `npm --prefix web run theme -- --check` to check generation. Components use semantic tokens and shared
`ui/` controls. [layout.css](../web/src/ui/layout.css) owns responsive boundaries. Keep strict CSP,
keyboard access, forced colours, bidi isolation and exact money/date formatting intact.

Browser units live under `web/tests/unit`, interactions under `web/tests/flows`, and fixtures under
`web/tests/support`. Type-aware lint and the strict protocol TypeScript project enforce import boundaries.
Local Vitest name filters may deliberately deselect tests. The completion lint rejects authored skip,
todo, conditional exclusion and focused-test APIs. Required CI rejects any incomplete collected test,
including runner deselection; run the complete selected files there.

## Verification

Start with the least costly check that can reject the change. Build current native inputs before
`--no-build`; check declarations and authored fixtures before starting containers.

```sh
dotnet build tests/ClaimCore.Tests --configuration Debug --no-restore
dotnet test --project tests/ClaimCore.Tests --configuration Debug --no-build --no-restore \
  --zero-tests-policy=strict --filter 'FullyQualifiedName~exact decimal totals' \
  -- --settings=eng/expecto.runsettings
npm --prefix web run test:unit -- tests/unit/presentation/presenter.test.ts
```

Use `bash eng/verify.sh --fast` at coherent checkpoints. For final qualification, use CI on the delivered
commit: PRs run Linux x64 and Chromium; main pushes, merge groups and manual dispatch run Linux x64,
Linux musl, macOS ARM64 and all three engines. Dispatch the full set on a substantial PR before review.
Do not duplicate that full qualification locally. Diagnose failures from their output
and rerun the affected paths; preserve earlier failures in the report. Passing focused checks does not
establish that unrun stages passed. Avoid duplicate full runs solely because both local and CI are available.

| Stage | Scope |
| --- | --- |
| lint | Fantomas over src, tests and eng |
| unit | Native behavior, properties, vectors and architecture |
| frontend | Formatting, design/theme lint, types, units, Knip, licenses and build |
| advisories | Native transitive vulnerabilities and npm audit at low severity; full CI events only |
| workflows | ShellCheck and pinned Actionlint |
| integration | PostgreSQL and Web suites |
| postgres / web | One isolated suite |
| browsers | Install locked Playwright engines when their version changes |
| acceptance-native | Published service, CLI and deployment cases |
| acceptance-browser | Chromium complete; Firefox/WebKit `@engine` cases |
| acceptance | Native acceptance, then the browser scopes |

With current Release applications, acceptance assembly and frontend assets built, select a browser file:

```sh
CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE=chromium \
CLAIMCORE_ACCEPTANCE_BROWSER_SPEC=localization-editor.spec.ts \
dotnet test --project tests/ClaimCore.AcceptanceTests --configuration Release --no-build --no-restore \
  --zero-tests-policy=strict --filter 'FullyQualifiedName~published browser acceptance' \
  -- --settings=eng/expecto.acceptance.runsettings
```

The file selector passes directly to Playwright. Retain the acceptance settings' one-worker bound so
fixture setup, authentication and teardown do not overlap. Chromium covers the complete journeys;
Firefox/WebKit cover CSP, sessions, IndexedDB, keyboard interaction, RTL and forced colours. The design
matrix uses six representative Chromium contexts and desktop English/narrow Arabic on the other engines.
Do not multiply independent language, theme and viewport choices into a full Cartesian product.

Store contracts use the single pinned PostgreSQL image from [postgresql-baseline.json](../db/postgresql-baseline.json).
Tests discover vocabulary from product declarations; reviewed contract diffs, hand-authored business cases and [durable vectors](../tests/fixtures/durable-vectors.json)
provide independent expectations. Builders supply inputs, never expected business outcomes. There is no
fixture update mode. Keep `--zero-tests-policy=strict` and fail-on-focused controls.

Each native stage writes `artifacts/verification/<stage>/results.trx`. CI uploads evidence on failure.
Strict nonempty discovery and substantive security negative controls remain required.

Private failure directories can contain credentials. Inspect their logs locally and share redacted
findings only. Retain useful failure evidence; remove only owned temporary resources. Teardown and
cancellation must preserve leases until provider cleanup actually finishes.

For an observed problem, the opt-in `CLAIMCORE_TIMING=1` integration probe prints read timings.
It is excluded from the gate, has no thresholds and writes no reports. It is not required verification.

## Dependencies

[Directory.Packages.props](../Directory.Packages.props) and reviewed lockfiles pin .NET dependencies;
[web/package.json](../web/package.json) and its lockfile pin the frontend. Retain container image digests.
Use locked restore and `npm ci`; update affected consumers, notices and checks together.
Keep actual authentication, CSP, origin/antiforgery, erasure, private-file and disclosure controls intact.

## Source releases

Versioning, tags and publication require owner authorization. The
[release workflow](../.github/workflows/release.yml) publishes the tagged commit's dated CHANGELOG section.
Prepare release notes, dependencies and documentation together; use the required CI result for the exact
main commit. Release publication checks the annotated tag, expected SHA and main-branch CI evidence.
Passing checks alone does not authorize merging or releasing. Keep public reports free of private inputs.

## Licensing and contributions

Read the [project notice](../PROJECT-NOTICE.md) before contributing or distributing modified builds.
Outside original copyrightable contributions need documented rights-holder permission covering
the public and intended commercial grants before merge; commercial contributor terms remain pending.
Stage new source files before publishing. Publications capture tracked working files in `SOURCE.tar.gz`;
keep inputs stable through frontend and native publication and inspect the archive before delivery.
Source archives retain a `SOURCE-FILES` inventory for rebuilding publications without Git. When adding
files to an extracted source tree, initialize a Git checkout and stage its source before publishing.
Build and publish require Git (for checkouts), Bash, tar and gzip, in addition to the pinned .NET and frontend toolchains.

Browser operations use one reducer in `web/src/operation/operationState.ts`. Draft authoring,
delivery transitions and disclosed reads have separate owners in the adjacent modules; the reducer
coordinates their transitions. `protocol/ownerScope.ts` defines ephemeral origin/installation/actor/
session ownership; durable recovery records retain principal scope without a session epoch.
