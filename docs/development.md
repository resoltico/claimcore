# Development

## First checkout

Use the pinned SDK in global.json and Node/npm versions declared by .node-version and
web/package.json. Docker is required for integration and published-process/browser acceptance.
Restore locked NuGet inputs and build the solution with .NET. Run `npm --prefix web ci`
and `npm --prefix web run build` for frontend assets. Ordinary .NET builds do not invoke npm;
frontend consumers use the tracked `contracts/` artifacts and require no .NET process.

## Verification stages

`bash eng/verify.sh` runs the complete local gate. Named stages run independently or in order,
for example `bash eng/verify.sh unit frontend`; `--fast` runs native lint, unit/architecture and
frontend checks without Docker. Restore and build work is shared within one invocation.

| Stage         | Coverage and prerequisites                                                                               |
| ------------- | -------------------------------------------------------------------------------------------------------- |
| `lint`        | .NET restore, F# formatting and lint                                                                     |
| `unit`        | .NET build, native/property and unoptimized architecture tests; tracked contract freshness               |
| `frontend`    | Node only: locked install, audit, formatting, lint, types, unit tests, Knip, licenses, build and notices |
| `workflows`   | ShellCheck and pinned Actionlint container; Docker required                                              |
| `integration` | .NET build, isolated PostgreSQL and Web/identity-provider tests; Docker required                         |
| `browsers`    | Locked npm install and Playwright browser/dependency installation                                        |
| `acceptance`  | Published service/CLI and browser tests; .NET, Node, Docker and installed browsers required              |

Use `bash eng/verify.sh browsers acceptance` to install browsers and then qualify published
clients. Tests use native Microsoft Testing Platform; every module is discovered with `[<Tests>]`
and guarded against missing source membership, undiscovered tests and focused/pending cases.
Floors come from measured unfiltered suites; never lower one to clear a failure. A partial or
filtered run is iteration evidence and cannot establish the complete gate.

Acceptance owns fresh labelled PostgreSQL/Keycloak and published processes. By default it runs
every specification on Chromium, Firefox and WebKit. Set `CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE`
to exactly `chromium`, `firefox` or `webkit` for one engine. Set
`CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE=smoke` with an explicit engine for the short smoke selection;
`complete` is the default. The runner reports scope and checks completion for that selection.
Smoke and one-engine success do not establish complete browser qualification.

CI runs native and frontend checks in parallel, then starts expensive fixtures after their
prerequisites succeed. PRs and merge queues run Chromium smoke; main/tag pushes and a manual
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
local use; blank, malformed and unlisted overrides are refused before resources are created.
The selector does not qualify a new major: adding support still requires actual execution and
reviewed evidence for that entry.

Browser workers remain serial within each fixture because tests change shared actor grants and
redirect state. Parallelism comes from isolated engine runners. Retries are disabled. Failures
retain private evidence; successful fixtures remove their resources. Never target an existing
evaluation stack. Tracked contract artifacts are reviewed with their F# producer; generated
localizations/assets, private inputs and reports stay ignored. Only remove resources created
by the current run. Stylelint has been removed deliberately; CSS semantic linting is reduced,
while formatting and browser accessibility/layout checks remain.

## Changes

A migration is a new numbered SQL file under db/migrations; never rewrite an applied script.
Update readers/writers and prove atomic application, exact prefix/checksum admission and refusal
of unsupported history. An endpoint is one catalogue entry with schemas, typed core operation,
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
