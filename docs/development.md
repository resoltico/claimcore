# Development

## First checkout

Use the pinned SDK in [global.json](../global.json) and Node/npm versions declared by
[.node-version](../.node-version) and [web/package.json](../web/package.json). Docker is required
for integration and published-process/browser acceptance. Integration also uses Bash and OpenSSL
to check the real local configuration producer.
Restore locked NuGet inputs and build the solution with .NET. Run `npm --prefix web ci`
and `npm --prefix web run build` for frontend assets. Ordinary .NET builds do not invoke npm;
frontend consumers use the tracked `contracts/` artifacts and require no .NET process.

Building or publishing applications, and building tests that consume native components, requires the C17 toolchain enforced by [build-native.sh](../eng/build-native.sh):
macOS Command Line Tools with Apple Clang 17+, macOS SDK 15+ and deployment target 14.0;
or Linux with GCC 13+ and glibc development headers. Only matching native `osx-x64`,
`osx-arm64`, `linux-x64` and `linux-arm64` hosts/targets are supported. Cross-RID builds,
Windows and musl are refused; use the matching host instead. Linux private-file tests additionally
require `setfacl` from the `acl` package. CI prepares these prerequisites; Docker builds use the
pinned GCC/Bookworm compiler and headers stage declared in [Dockerfile](../deployment/Dockerfile).
Compiler, SDK, flags, source and header inputs participate in incremental native builds.
Normal builds and framework-dependent `publish --no-build --no-restore` carry the HostSecurity
library into all three applications. Preserve the complete publish directory. Trimming,
single-file and self-contained publication are outside the reviewed notice coverage and refused.

Shared application items copy the reviewed notice superset and manifest from
[eng/licenses](../eng/licenses/native-notices.json), plus SDK-resolved apphost provenance.
Acceptance checks selected dependency/runtime/resource assets and actual recursive publication
contents against this review, including native assets and the apphost outside `.deps.json`.
Package-declared and reviewed source revisions are distinguished where an upstream declared
revision is unavailable; the review does not claim inaccessible contents were inspected.
Source archives include the native sources/headers, build plumbing and canonical notices;
there is no binary-only source dependency.

## Verification stages

`bash eng/verify.sh` with no arguments runs the complete local gate. Named stages run independently or in order,
for example `bash eng/verify.sh unit frontend`; `--fast` runs native lint, unit/architecture and
frontend checks without Docker and must be its only argument. Explicit empty arguments,
unknown stages and `--fast` mixed with other arguments refuse with exit 64 before invoking tools
or Docker. Restore, native build and frontend asset build work are shared
within one invocation; a later acceptance stage reuses the frontend stage's successful asset build.
Stages requiring Docker check the daemon before any selected restore, build or browser download.

| Stage              | Coverage and prerequisites                                                                                           |
| ------------------ | -------------------------------------------------------------------------------------------------------------------- |
| `lint`             | .NET restore, F# formatting and lint                                                                                 |
| `unit`             | .NET build, native/property and unoptimized architecture tests; tracked contract freshness                           |
| `frontend`         | Node only: locked install, audit, formatting, lint, types, unit tests, Knip, licenses, build and notices             |
| `workflows`        | ShellCheck and pinned Actionlint container; Docker required                                                          |
| `integration`      | Isolated PostgreSQL and Web/identity-provider tests; builds their project closures; Docker required                  |
| `postgres` / `web` | Independent complete PostgreSQL or Web/identity suite; restores and builds only its project closure; Docker required |
| `browsers`         | Locked npm install and Playwright browser/dependency installation                                                    |
| `acceptance`       | Published service/CLI and browser tests; .NET, Node, Docker and installed browsers required                          |

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

CI starts independent checks in parallel. PostgreSQL waits only for baseline configuration, Web/identity
starts independently, and acceptance waits only for frontend qualification. Native test jobs restore
and build their selected inputs. PostgreSQL keeps its baseline-major matrix and fail-fast cancellation.
`Gate` requires every native, frontend and acceptance job. The local `integration` stage runs
both suites in order and builds their project closures; an earlier solution build is reused. Use
`postgres` or `web` for a complete individual suite.
PRs and merge queues run eleven-case Chromium smoke, including authority uncertainty and
stewardship localization; main/tag pushes and a manual
CI dispatch with `full_browser_qualification` enabled run all specifications in an isolated
three-engine matrix. A failed engine cancels sibling jobs; `Gate` requires every scheduled job
to succeed. Thus a green PR `Gate` proves its advertised smoke scope, not full browser coverage.
Complete qualification remains separate evidence required for delivery and source releases.
The frontend CI job executes inside the pinned Node image derived from the deployment Dockerfile,
with a writable temporary home. It refuses an executable `dotnet` before running frontend checks;
the hosted runner's preinstalled SDK is outside that container. Format/lint, unit, PostgreSQL and Web/identity jobs prepare .NET and native compiler toolchains without Node. Published-client acceptance also uses Node.
The container build likewise builds frontend assets in a Node stage and publishes applications
in a .NET SDK plus pinned C compiler stage.

CI toolchain jobs cache NuGet packages and npm downloads using the tracked lockfiles. The
SDK-free frontend container mounts a download-only npm cache keyed by its pinned toolchain,
lockfile and deployment image. Every run still performs
locked restore or `npm ci` and the dependency audit; caches do not substitute for checks.
Product build outputs, `node_modules`, generated assets and test results are not cached. GitHub
scopes caches by branch; untrusted pull requests do not populate the default branch cache.

The integration CI matrix derives every major from `db/postgresql-baseline.json` and passes it
as `CLAIMCORE_TEST_POSTGRES_MAJOR`. Both integration and published acceptance fixtures select
that exact listed major and pinned image. Unset selects the first listed entry for deterministic
local use; blank, malformed and unlisted overrides are refused by stage preflight before restore or
build. The shell preflight checks exact numeric major declarations; fixtures still own complete JSON
and pinned-image validation before resources are created.
The selector does not qualify a new major: adding support still requires actual execution and
reviewed evidence for that entry.

The PostgreSQL stage uses [bounded runner settings](../eng/expecto.integration.runsettings) to
overlap at most two independent test cases. Each database-backed case owns a fresh pinned PostgreSQL cluster; role
changes, schema corruption and shutdown tests cannot affect another case. Operations inside a
case retain their sequence and transaction boundaries. Direct PostgreSQL test invocations must
pass `-- --settings=eng/expecto.integration.runsettings` to retain this resource bound. Other
native suites use the general runner settings. PostgreSQL readiness retains both protocol and
mapped-port checks, polls each every 250 ms, and bounds the protocol phase to 60 seconds; the
mapped-port phase retains the Npgsql connection timeout.

Local runs sequence the selected engines in one native test case and stop before creating a later
engine fixture when an earlier engine fails. Successful default execution requires all three
complete engine suites.

Browser workers remain serial within each fixture because tests change shared actor grants and
redirect state. Parallelism comes from isolated engine runners. Retries are disabled. Failures
retain private evidence; successful fixtures remove their resources. Horizontal overflow retains a
fixed `LAYOUT` failure category even if writing its numeric/tag/class report fails. Reports use a
fixed basename in the failing test's private output directory with mode `0600`; measurement
errors remain distinct from report-storage failures. Never target an existing
evaluation stack. Tracked contract artifacts are reviewed with their F# producer; generated
localizations/assets, private inputs and reports stay ignored. Only remove resources created
by the current run. CSS has formatting and browser accessibility/layout checks, with no dedicated
semantic lint stage.

## Test process lifetime

The test-only native supervisor starts a session before launching the target and remains its
process-group anchor until teardown. Anonymous control/status pipes distinguish target exit,
signal and exec failure; controller EOF also triggers teardown. Managed bounded readers preserve
partial evidence and original failure identity. Cleanup has its own bound and observes group
absence after the anchor exits; failed or incomplete cleanup is reported, never inferred from
target exit. Ordinary descendants in the inherited group are covered. Deliberate session/daemon
escape is outside this fixture contract. The supervisor and its resolution probes never ship in
product outputs. Executable resolution follows the supported .NET Unix behavior before launch,
including controller PATH/cwd semantics; isolated child tests qualify that behavior without
changing the test runner's environment. The quiet-deadline control uses a 250 ms deadline and 64-byte per-stream output bound.

## Changes

A migration is a new numbered SQL file under db/migrations. Migration bytes shipped by a release
are immutable, except that a pre-1.0 release may replace the baseline and refuse older installations untouched.
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

## Generated contracts and boundary verification

The generator stages and validates the complete owned artifact set before replacement. Its tracked
inventory permits removal of obsolete generated outputs; unexpected content is refused, never deleted.
Build the generator from Contracts/ClientProtocol independently of CLI, then compile clients from the
tracked outputs. `contracts/ClientContract.fs` is compiled into CLI and `browser-contract.ts` is consumed
by the browser. Neither normal build invokes generation. Final verification includes published CLI
closure/discovery with server assemblies absent and complete positive/negative byte corpora.

Browser unit qualification forbids focused runs and rejects runner-reported skipped, todo, pending,
interrupted or retried outcomes. The completion reporter is tested against the actual runner,
including namespace imports and chained declarations; source spelling is not the completion oracle.

Read-contract changes include the core/store page policy, generated maxItems/correlation and both
byte decoders. Grant review pages must traverse installation-null/case/erased scopes without
omission under current caller authority. FULL history uses the effective server limit and the
last included revision, with retained-format and actual escaping/metadata byte-budget controls.
Browser cursor recovery is local unless an actual access/session refusal is observed.
