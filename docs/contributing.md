# Contributing

Use this guide to build, change and verify ClaimCore. User tasks belong in [Case work](case-work.md),
client contracts in [Integrating](integrating.md), and deployment tasks in [Operating](operating.md).

## First checkout

Install the SDK from [global.json](../global.json), Node from [.node-version](../.node-version), and npm
from [web/package.json](../web/package.json). Bash and Python 3 are required. Docker is required for
PostgreSQL, Server and published acceptance tests; workflow checks also require ShellCheck.
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

The table below defines project ownership. Supported contracts are documented in
[Integrating](integrating.md), and runtime and recovery procedures in [Operating](operating.md).

| Project | Responsibility |
| --- | --- |
| Json | Strict JSON, codecs, schemas and writers |
| Domain | Values, task declarations, checks, decisions, aggregates, part descriptions and access policy |
| Application.Contracts | Use-case contracts, endpoint declarations, client task metadata, typed assembly and outcomes |
| RecordFormat | Current durable grammars, literal spellings, limits and cursors |
| Application | Execution, reads, verification, storage and administration; PostgreSQL transactions and projections |
| Presentation | Semantic regions, screen presenters, messages and language policy |
| Wire | Endpoints, transport codecs, catalog, build and legal identity; references Presentation |
| HostSecurity | Private files, HTTPS origins and TLS purposes |
| Server | Authentication, request gates, host bindings, static assets and owner administration |
| Cli | HTTPS client, command grammar, local recovery and terminal rendering |
| CommandLine | Shared exit codes, usage errors, help and version frames |

The [evaluated edge policy](../tests/ClaimCore.Tests/Architecture/ProjectReferences.fs) owns exact project
references. Domain is independent of transport and JSON. Application does not depend on Wire
or Presentation. Its internal Storage namespace owns connections, concrete transactions and ledger
writes; Reads owns SQL, and Administration supplies the operator facade. Npgsql is compile-private.
The published CLI excludes Application. Product friendship is limited to tests. Presentation and Wire
use Application.Contracts reply kinds; CLI alone maps those kinds to process exit codes.

## Source vocabulary

Change a value in its owning Domain module. [CaseFields](../src/ClaimCore.Domain/CaseFields.fs) declares
field kinds and money relationships; typed records retain validated structure through execution.
[CaseSearch](../src/ClaimCore.Domain/CaseSearch.fs) owns search projection policy; [QueryFields](../src/ClaimCore.Application.Contracts/QueryFields.fs) owns machine summary selection. [FieldPresentation](../src/ClaimCore.Presentation/Catalog/FieldPresentation.fs) owns human
listing and prominence. Human layout changes do not change Domain or the machine summary selection.
[Identifiers](../src/ClaimCore.Domain/Identifiers.fs) owns UUIDv7 operation IDs and their lifetime.
[Task modules](../src/ClaimCore.Domain/Tasks/Registry.fs) own declarations, checks, availability and decisions.
[CaseFamily](../src/ClaimCore.Domain/Aggregates/CaseFamily.fs) and [Person](../src/ClaimCore.Domain/Aggregates/Person.fs)
apply accepted parts, retaining identity and provenance when private content is erased. The case family
owns case facts, payments, work, reminders and notes; the person combines identity, profile, enablement
and role assignments. Task decisions produce part changes; shared before/after descriptions serve
reviews, receipts and history.
[CaseIndex](../src/ClaimCore.Domain/CaseIndex.fs) derives the family index.
[Engine](../src/ClaimCore.Application/Execution/Engine.fs) shares preview and execution decisions;
[Fold](../src/ClaimCore.Application/Execution/Fold.fs) re-decides available inputs and facts for replay, rebuild and verification.
[TaskDeclarations](../src/ClaimCore.Application.Contracts/TaskDeclarations.fs) combines Domain declarations with
[endpoint declarations](../src/ClaimCore.Application.Contracts/EndpointDeclarations.fs) for clients; [OperationSubmission](../src/ClaimCore.Application.Contracts/OperationSubmission.fs) assembles
selected authored values with a real reviewed basis. Presentation attaches wording to those identities.
Generated types and task metadata live in `contracts/client.ts`; collection row keys stay outside submissions.

Browser operations use one reducer in `web/src/operation/operationState.ts`. Draft authoring,
delivery transitions and disclosed reads have separate owners in the adjacent modules; the reducer
coordinates their transitions. `protocol/ownerScope.ts` defines ephemeral origin/installation/actor/
session ownership; durable recovery records retain principal scope without a session epoch.

RecordFormat owns canonical business-input codecs and the single installation format. Wire owns closed submission maps and transport choices. Domain vocabularies supply business tokens. [Compiled negative controls](../tests/ClaimCore.Tests/Architecture/NegativeFixtures.fs)
exercise the architecture boundaries, including synchronous and asynchronous calls.

`Codec.validate` admits strict structural JSON without invoking semantic constructors. `Read` and
`Codec.decode` also interpret typed values; `interpret`, `refine` and `buildResult` can apply additional
core constraints without changing the schema. Structural `restrict` declarations provide an explicit
raw validator. Check native structural admission against authored examples; arbitrary
codec records and interpretation predicates do not establish schema equivalence.

[IStore](../src/ClaimCore.Application/Storage/Store.fs) is the internal transaction seam,
not an integrator custom-store contract. It supplies snapshot read and head-locked write
callbacks. Writes decide against that locked state and commit only an accepted result. A final absence
lookup uses the write callback without appending. Current authorization governs every disclosure.
Live family acquisition reads note headers and reminder dates; it opens preceding private text/plans
only for selected parts. Public roots and acquired private values retain their hash checks. Reminder
query controls acquire the plans they present. Live child projections consume accepted changed parts;
owner erasure clears all related private projections. Replay/rebuild still reconstruct the full rows.

Pre-1.0 durable changes require a fresh installation. There are no migrations or cross-version readers.
The [SQL baseline](../db/baseline.sql) enforces structure, append-only storage and erasure authorization;
RecordFormat decoding and verification enforce task meaning. The ledger stores opaque task tokens,
public bindings, retained inputs, effects and erasable owner compartments. SQL checks gap-free part
revisions, aggregate ownership and authorized clearing; it does not re-derive tasks, roles or business
bounds. Do not move business vocabulary into SQL.

## Generated contracts

After changing a declaration, codec, message or language policy:

```sh
dotnet run --project eng/ClaimCore.ContractGenerator --no-restore -- --output contracts
dotnet run --project eng/ClaimCore.ContractGenerator --no-restore -- --check
```

The generator owns only [contracts](../contracts/). Presentation resolves authored catalogs at runtime for the host and CLI.
Check mode compares generated contents and does not repair them. Review changes with their producers.
A custom output directory contains the contract artifacts only. HUMAN reads share one document/reply
grammar; affordances and links are generic, while operation submissions and DATA replies remain typed.
Binding definitions are shared by target shape. Review remaining identical schemas by meaning rather
than merging every equal JSON shape.

## Core presentation and copy

Host bindings compute locale-neutral screens with semantic regions through
[screen presenters](../src/ClaimCore.Presentation/Screens). Actor replies negotiate distinct
HUMAN documents and DATA projections. A HUMAN document owns its subject, Screen, affordances, generic query links,
observation, disclosure and cursor. Rows carry their own controls. DATA carries no Screen; browser and native text consume documents,
while JSON and CSV consume DATA. Review documents carry changes and bindings without full snapshots.
[Domain declarations](../src/ClaimCore.Domain/Tasks/Registry.fs) own semantic inputs, retention and choices;
the [client facade](../src/ClaimCore.Application.Contracts/TaskDeclarations.fs) includes endpoint metadata;
[submission assembly](../src/ClaimCore.Application.Contracts/OperationSubmission.fs) owns their command construction.

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
owns development-required languages, aliases, direction and regional defaults.

Release qualification checks every offered language with
`dotnet run --project eng/ClaimCore.ContractGenerator -- --check-translations`; any missing or stale
entry refuses publication. The release workflow checks out the verified tag and uses its pinned SDK
before this check. Ordinary PRs permit development fallback for languages outside en, lv and ar.
See [language review](glossary.md#language-review) for current coverage and human-review status.

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

Domain scenarios under `tests/ClaimCore.Tests/Domain` use Domain and pure builders:
[DomainInputs](../tests/ClaimCore.TestSupport/DomainInputs.fs),
[CaseFixtures](../tests/ClaimCore.TestSupport/CaseFixtures.fs) and
[CaseBuilders](../tests/ClaimCore.Tests/Domain/CaseBuilders.fs). Keep codec, presentation and compiler
boundary checks with Contract or Architecture. Builders supply inputs; expected business outcomes
remain hand-authored. Retain storage/transaction checks, security controls and user journeys when
removing repeated per-task adapter cases.

Browser units live under `web/tests/unit`, interactions under `web/tests/flows`, and fixtures under
`web/tests/support`. Type-aware lint and the strict protocol TypeScript project enforce import boundaries.
The existing frontend lint also rejects task/endpoint literals outside generated contracts and the
transport/types/routing adapters. Select operations from Core controls and definitions; lookups use
choicesFrom, and all matching choicesWhen conditions constrain choices. Core owns business wording
in all nine catalogs; interaction chrome stays with the browser.
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
commit: PRs run Linux x64 and two Chromium shards; main pushes, merge groups and manual dispatch run Linux x64,
Linux musl, macOS ARM64 and all three engines. Use the automatic full main-push result for a release candidate; do not dispatch a duplicate matrix.
Deployment, schema and database-host changes also run `bash eng/compose-restart-smoke.sh` in the separate
[Compose restart workflow](../.github/workflows/compose.yml) (weekly as well); it is outside the Gate, so check it.
Each native platform publishes and runs the CLI apphost offline and qualifies its dependency closure, notices and source archive. Complete
published service/client integration runs on Linux x64.
Docs-only PRs run configuration, lint and unit. All changed paths must be under `docs/` or be Markdown
outside `src/`, `web/` and `tests/`; `LICENSE`, `PROJECT-NOTICE.md` and `LICENSE-COMMERCIAL.md` always
require the full PR set. Main pushes, merge groups and manual dispatch always run the full set.
Offline CLI usage, help, schema and dry-run checks run in-process in unit; acceptance keeps one
published smoke for each category.

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
| postgres / server | One isolated suite |
| browsers | Install locked Playwright engines when their version changes |
| acceptance-native | Published service, CLI and deployment; `CLAIMCORE_ACCEPTANCE_SHARD=walkthrough` or `remaining` selects one shard |
| acceptance-browser | Chromium complete in two `CLAIMCORE_ACCEPTANCE_BROWSER_SHARD=1/2` or `2/2` shards; Firefox/WebKit `@engine` cases |
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
Its 200% equivalent reflow case uses the resulting 320-CSS-pixel viewport, including responsive media
queries; it does not inject CSS `zoom` or claim actual browser zoom coverage. See
[Reflow guidance](https://www.w3.org/WAI/WCAG22/Understanding/reflow.html).
Do not multiply independent language, theme and viewport choices into a full Cartesian product.
Failed browser jobs retain synthetic failed-spec traces, screenshots and bounded service captures for
three days in their verification artifact. Authentication-state files and private fixture configuration
are excluded. Individual request/assertion bounds remain separate from multi-step workflow budgets.

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
The Playwright version in [web/package.json](../web/package.json) is constrained by the
[Firefox accessible-name regression](https://github.com/microsoft/playwright/issues/43967). Verify
that accessible-name references work across execution worlds before changing that pin.
Keep actual authentication, CSP, origin/antiforgery, erasure, private-file and disclosure controls intact.

## Source releases

Versioning, tags and publication require owner authorization. The
[release workflow](../.github/workflows/release.yml) publishes the tagged commit's dated CHANGELOG section.
Prepare dependencies, documentation, the version in [Directory.Build.props](../Directory.Build.props)
and the dated release notes together. Regenerate contracts after changing the version. Keep an empty
`Unreleased` section above the dated section and leave published release history unchanged.

Qualify the complete candidate in one pull request, including its dependency, documentation, version
and release-note changes. Merge after review and the automatic PR checks pass. Use the automatic
main-push CI result for the exact merged commit; do not dispatch a duplicate qualification run. Deployment
changes also require the separate Compose restart workflow. After those checks succeed, create an
annotated `vMAJOR.MINOR.PATCH` tag at that commit, push it and dispatch publication from main.
Start from a clean main checkout synchronized with origin, and confirm its SHA matches the successful CI run:

```sh
claimcore_release_sha="$(git rev-parse main)"
claimcore_release_tag="v$(sed -nE 's/.*<Version>([^<]+)<\/Version>.*/\1/p' Directory.Build.props)"
git tag -a "$claimcore_release_tag" "$claimcore_release_sha" -m "Release $claimcore_release_tag"
git push origin "$claimcore_release_tag"
gh workflow run release.yml --ref main -f tag="$claimcore_release_tag" -f expected_sha="$claimcore_release_sha"
```

Publication checks the annotated tag, expected SHA, main-branch CI evidence and all offered translations.
Confirm the workflow succeeded and the public release contains the dated changelog section. Passing
checks alone does not authorize merging or releasing. Keep public reports free of private inputs.

## Licensing and contributions

Read the [project notice](../PROJECT-NOTICE.md) before contributing or distributing modified builds.
Submitting a contribution accepts the [contribution terms](../PROJECT-NOTICE.md#contribution-terms).
Confirm your agreement using the checkbox in the pull request template.

Stage new source files before publishing. Publications capture tracked working files in `SOURCE.tar.gz`;
keep inputs stable through frontend and native publication and inspect the archive before delivery.
Source archives retain a `SOURCE-FILES` inventory for rebuilding publications without Git. When adding
files to an extracted source tree, initialize a Git checkout and stage its source before publishing.
Build and publish require Git (for checkouts), Bash, tar and gzip, in addition to the pinned .NET and frontend toolchains.
