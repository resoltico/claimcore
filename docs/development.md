# Development

## First checkout

Use the pinned SDK in [global.json](../global.json) and Node/npm versions declared by
[.node-version](../.node-version) and [web/package.json](../web/package.json). Docker is required
for integration and published-process/browser acceptance. Integration also uses Bash and OpenSSL
to check the real local configuration producer.
Restore locked NuGet inputs and build the solution with .NET. Run `npm --prefix web ci`
and `npm --prefix web run build` for frontend assets. Ordinary .NET builds do not invoke npm;
frontend consumers use the tracked `contracts/` artifacts and require no .NET process.

## Verification stages

`bash eng/verify.sh` runs the complete local gate. Named stages run independently or in order,
for example `bash eng/verify.sh unit frontend`; `--fast` runs native lint, unit/architecture and
frontend checks without Docker. Restore, native build and frontend asset build work are shared
within one invocation; a later acceptance stage reuses the frontend stage's successful asset build.

| Stage         | Coverage and prerequisites                                                                               |
| ------------- | -------------------------------------------------------------------------------------------------------- |
| `lint`        | .NET restore, F# formatting and lint                                                                     |
| `unit`        | .NET build, native/property and unoptimized architecture tests; tracked contract freshness               |
| `frontend`    | Node only: locked install, audit, formatting, lint, types, unit tests, Knip, licenses, build and notices |
| `workflows`   | ShellCheck and pinned Actionlint container; Docker required                                              |
| `integration` | .NET build, isolated PostgreSQL and Web/identity-provider tests; Docker required                         |
| `postgres` / `web` | Independent complete PostgreSQL or Web/identity suite; restores and builds only its project closure; Docker required |
| `browsers`    | Locked npm install and Playwright browser/dependency installation                                        |
| `acceptance`  | Published service/CLI and browser tests; .NET, Node, Docker and installed browsers required              |

Use `bash eng/verify.sh browsers acceptance` to install browsers and then qualify published
clients. Tests use native Microsoft Testing Platform; every module is discovered with `[<Tests>]`
and guarded against missing source membership, undiscovered tests and focused/pending cases.
Floors come from measured unfiltered suites; never lower one to clear a failure. A partial or
filtered run is iteration evidence and cannot establish the complete gate. Native stages retain
TRX results and adjacent sanitized browser-engine count/duration summaries in unique ignored directories
under `artifacts/verification/`; later runs do not overwrite earlier failure evidence.

Acceptance owns fresh labelled PostgreSQL/Keycloak and published processes. By default it runs
every specification on Chromium, Firefox and WebKit. Set `CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE`
to exactly `chromium`, `firefox` or `webkit` for one engine. Set
`CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE=smoke` with an explicit engine for the short smoke selection;
`complete` is the default. The runner reports scope and checks completion for that selection.
Smoke and one-engine success do not establish complete browser qualification. The local gate
validates engine/scope selection before any restore, build or browser installation. The installer also
includes Chromium for shared CLI PKCE authentication when Firefox or WebKit specifications are
selected; this prerequisite does not add Chromium specifications to that selected-engine run.

CI runs native and frontend checks in parallel. PostgreSQL and Web/identity suites use separate
runners after native prerequisites succeed; neither waits for unrelated frontend checks. PostgreSQL
keeps its baseline-major matrix and fail-fast cancellation. `Gate` requires both native integration
jobs as well as frontend and acceptance. The local `integration` stage still runs both suites in
order with one shared solution build; use `postgres` or `web` for a complete individual suite.
PRs and merge queues run eleven-case Chromium smoke, including authority uncertainty and
stewardship localization; main/tag pushes and a manual
CI dispatch with `full_browser_qualification` enabled run all specifications in an isolated
three-engine matrix. A failed engine cancels sibling jobs; `Gate` requires every scheduled job
to succeed. Thus a green PR `Gate` proves its advertised smoke scope, not full browser coverage.
Complete qualification remains separate evidence required for delivery and source releases.
The frontend CI job executes inside the pinned Node image derived from the deployment Dockerfile,
with a writable temporary home. It refuses an executable `dotnet` before running frontend checks;
the hosted runner's preinstalled SDK is outside that container. Native jobs set up .NET only.
The container build likewise builds frontend assets in a Node stage and publishes applications
in an SDK stage.

The integration CI matrix derives every major from `db/postgresql-baseline.json` and passes it
as `CLAIMCORE_TEST_POSTGRES_MAJOR`. Both integration and published acceptance fixtures select
that exact listed major and pinned image. Unset selects the first listed entry for deterministic
local use; blank, malformed and unlisted overrides are refused by stage preflight before restore or
build. The shell preflight checks exact numeric major declarations; fixtures still own complete JSON
and pinned-image validation before resources are created.
The selector does not qualify a new major: adding support still requires actual execution and
reviewed evidence for that entry.

The PostgreSQL stage uses [bounded runner settings](../eng/expecto.integration.runsettings) to
overlap at most two independent test cases. Each owns a fresh pinned PostgreSQL cluster; role
changes, schema corruption and shutdown tests cannot affect another case. Operations inside a
case retain their sequence and transaction boundaries. Direct PostgreSQL test invocations must
pass `-- --settings=eng/expecto.integration.runsettings` to retain this resource bound. Other
native suites keep their existing runner settings.

Local runs sequence the selected engines in one native test case and stop before creating a later
engine fixture when an earlier engine fails. Successful default execution requires all three
complete engine suites.

Browser workers remain serial within each fixture because tests change shared actor grants and
redirect state. Parallelism comes from isolated engine runners. Retries are disabled. Failures
retain private evidence; successful fixtures remove their resources. Never target an existing
evaluation stack. Tracked contract artifacts are reviewed with their F# producer; generated
localizations/assets, private inputs and reports stay ignored. Only remove resources created
by the current run. CSS has formatting and browser accessibility/layout checks, with no dedicated
semantic lint stage.

## Changes

A migration is a new numbered SQL file under db/migrations. Migration bytes shipped by a release
are immutable.
Update readers/writers and prove atomic application, exact prefix/checksum admission and refusal
of unsupported history. An endpoint is one catalogue entry with accepting/receipt scope, schemas, typed core operation,
codec/rendering and every outcome/host-failure test. Regenerate tracked artifacts and review their diff.
A command field belongs to Domain validation and RecordFormat encoding, then its authoritative
contract projection, consumers and independent tests. Do not add adapter-only rules or fields.

Function/member/lambda length 50 and complexity 12 remain absolute across production and tests.
Inline tool exceptions name one rule and their reason. File/module/value size is not a gate;
organize by capability. Required properties execute fixed 200 cases with state sequences up to 20
and deterministic seeds. Failure diagnostics retain safe replay information, not generated payload.
No environment switch selects a longer campaign or silently bypasses other properties.

## Dependency pins

`Directory.Packages.props` pins packages directly referenced by projects and shared build
properties. Central transitive pinning is disabled; dependency constraints select transitive
versions and the reviewed `packages.lock.json` files record their exact resolved graphs.
There are no additional transitive constraints. A future constraint must be effective, identify
the advisory or dependency conflict it addresses, and be verified through restore and build;
a non-direct `PackageVersion` entry would be inert with this setting.

Restore audits every dependency at the low severity threshold and treats advisory warnings as
errors. `NuGet.Config` selects nuget.org for both packages and audit data. Regenerate lockfiles
with `dotnet restore ClaimCore.slnx --force-evaluate`, review their dependency changes, then
verify with `dotnet restore ClaimCore.slnx --locked-mode`. An explicit audit is
`dotnet list ClaimCore.slnx package --vulnerable --include-transitive --no-restore`.
The testing platform's telemetry packages are library dependencies; SDK and platform telemetry
remain disabled in CI.

The generator stages and validates the complete owned artifact set before replacement. Its tracked
inventory permits removal of obsolete generated outputs; unexpected content is refused, never deleted.
Build the generator from Contracts/ClientProtocol independently of CLI, then compile clients from the
tracked outputs. `contracts/ClientContract.fs` is compiled into CLI and `browser-contract.ts` is consumed
by the browser. Neither normal build invokes generation. Final verification includes published CLI
closure/discovery with server assemblies absent and complete positive/negative byte corpora.

Browser unit qualification forbids focused runs and rejects runner-reported skipped, todo, pending,
interrupted or retried outcomes. The completion reporter is tested against the actual runner,
including namespace imports and chained declarations; source spelling is not the completion oracle.
