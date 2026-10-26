# Development

Use this guide to build from a clean checkout, choose a verification scope and deliver a complete change.
The required gate and opt-in growth qualification use the same commands locally and in CI.

## First checkout

Use the SDK in [global.json](../global.json), Node/npm in [.node-version](../.node-version) and
[web/package.json](../web/package.json), Bash, Python 3 for the independent durable oracle, and Docker for
integration/acceptance/workflow checks. Install ShellCheck for workflow checks. Use supported Unix platforms for
private-file inputs. The qualified native platforms and controls are listed below.
Unix sink controls use sh, mkfifo, dd, stty, script and Linux prlimit.

```sh
dotnet restore ClaimCore.slnx --locked-mode
dotnet tool restore
npm install --global --ignore-scripts "npm@$(node -p "require('./web/package.json').engines.npm")"
npm --prefix web ci
bash eng/verify.sh browsers
bash eng/verify.sh
```

Ordinary .NET builds run neither npm nor generation. Frontend commands need no .NET/Python. Framework-dependent apphost
publication is qualified; self-contained, trimmed and single-file modes remain unqualified. Preserve the entire publish
directory and reviewed notices from [eng/licenses](../eng/licenses/third-party-notices.json). No C compiler or custom
native library is required; HostSecurity uses managed file APIs.

## Verification stages

`bash eng/verify.sh` is the complete gate. `--fast` selects lint/unit/frontend only and is not a complete
gate. Named stages execute in order, stop at failure and share restore/build work.

| Stage | Checks |
| --- | --- |
| lint | Fantomas, FSharpLint and the maintained browser boundary controls |
| unit | Build, unit/property and compiled architecture suites |
| frontend | npm ci/audit, format/lint/types, browser unit completion, Knip/licenses/build/notices |
| workflows | ShellCheck and pinned Docker Actionlint |
| integration | Required postgres then web suites; growth is separate |
| postgres / web | Required selected isolated suite with its project closure |
| growth | Opt-in contention and verifier qualification |
| browsers | Locked Playwright engine/system dependency installation |
| acceptance | Published service/CLI and real browser qualification |

The [stage assignments](../eng/verify.sh) match test projects to environment requirements: `ClaimCore.Tests` contains pure and compiled
architecture checks, `ClaimCore.IntegrationTests` owns PostgreSQL scenarios, `ClaimCore.WebTests` owns host
integration, and `ClaimCore.AcceptanceTests` owns published processes and browsers. `ClaimCore.TestSupport`
is a library containing shared fixtures and assembly-parameterized guards. Each test assembly registers
those guards once; no source file is linked across project boundaries. Pure test folders follow the owning
production component. The pure tier runs once in Debug with optimization disabled so the compiled
architecture negative controls remain visible; deployed production outputs are built separately in Release.

Native tests run Microsoft Testing Platform with automatic `[<Tests>]` discovery, evaluated Compile membership
and nonempty-run enforcement. Every test project is assigned exactly once to a stage; missing/duplicate
assignments fail before restore. No hand-kept minimum counts exist. Ad hoc focused, filtered, pending, skipped or
retried runs cannot satisfy verification. The named postgres and growth stages select their disjoint scopes explicitly. TRX and sanitized measurements are retained in unique ignored
`artifacts/verification/` directories, including failure evidence.

Default acceptance runs all specifications on Chromium, Firefox and WebKit, sequentially stopping on first
failed engine. `CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE=chromium|firefox|webkit` selects one;
`CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE=smoke` additionally requires an explicit engine. Partial runs are labelled
and never prove full qualification. Browser workers stay serial inside a fixture because actor grants/redirect
state are shared; isolated engine runners provide CI parallelism. Retries are zero. The installer retains
Chromium's headless shell for CLI PKCE, including Firefox/WebKit jobs, and omits unused full Chromium.
Fresh-login tests budget one admission window plus normal OIDC work. Their shared helper follows the actual
browser challenge and recovers only from a validated login-admission refusal.

CI runs independent lint/unit/frontend/PostgreSQL/Web jobs and requires the scheduled required jobs in `Gate`.
The native platform matrix is declared in [CI](../.github/workflows/ci.yml). Growth is available only through an explicit workflow dispatch input, outside Gate. PR, merge queue,
main/tag and explicit dispatch use three isolated complete engines. Every scheduled engine must succeed in the aggregate
Gate. The frontend runs in the pinned Node container without executable .NET. PostgreSQL majors/images come from
[postgresql-baseline.json](../db/postgresql-baseline.json); each listed major needs actual qualification.
`CLAIMCORE_TEST_POSTGRES_MAJOR` selects exactly one listed major; unset selects the first. Invalid selectors and Docker
prerequisites fail before expensive work. PostgreSQL uses [runner settings](../eng/expecto.integration.runsettings) with
at most two owned clusters concurrently; direct runs must retain those settings. Other suites use general settings.
Successful fixtures clean their own resources. Failed fixtures retain private evidence; never target existing
installations or prune unrelated containers/volumes.

## Disk use and retained evidence

[Native report retention](../eng/native-reports.sh) protects active runs and bounds retained completed
managed reports. Unmanaged directories are untouched; review them separately before owner cleanup.
This policy bounds managed report retention, not total artifacts or cache use.

NuGet/npm caches retain downloads keyed by lockfiles; locked restore/npm ci/audit still run. SDK-free frontend
and acceptance share the same exact npm download key, without broad fallbacks. Build outputs, node_modules,
assets and test results are not cached. Shared local caches may serve other repositories; do not clear them
indiscriminately. GitHub eviction is not a peak-disk guarantee. Disk use comes from dependencies, images,
selected browsers, build/publication copies, isolated databases and retained evidence. There is no fixed
universal maximum or enforced whole-run quota. Measure the selected concurrency and retain failure evidence;
reclaim only proven owned resources. Properties/fuzzing generate bounded in-memory values, not per-case files
or on-disk corpora.

A native run creates a unique report directory and protects it while active. If a process is killed before
completion, review its active marker and evidence manually. The retention script does not guess whether that
process is still alive. Managed test processes use bounded readers, timeouts and teardown observation as defined in
[test process support](../tests/ClaimCore.TestSupport/).
Cleanup failures retain the original failure; detached/reparented descendants remain outside process-tree kill guarantees.
Diagnostics exclude payloads, credentials and provider exception text.

## Changes

Find the owning contract and read its behavior tests before editing. Keep endpoints in the typed registry and fields in
the field table, Domain admission and Contract codecs. RecordFormat owns durable encodings. Update affected consumers,
SQL, tests, source locales and docs together. The current build admits one exact source-bound baseline. There is no
in-place upgrade or conversion; preserve incompatible installations with matching software. Lint policy is defined in [FSharpLint settings](../eng/fsharplint.json) and the browser lint configuration.
Only inline named rule exceptions with reasons are allowed. [PropertyHarness](../tests/ClaimCore.Tests/Support/PropertyHarness.fs)
owns deterministic case counts, sequence bounds and shrinking controls; decoder fuzz properties own byte bounds
and hostile cases. Negative controls demonstrate that guards reject broken properties. There is no separate
mutation campaign or environment switch for a longer or bypassed run.

## Dependency pins

[Directory.Packages.props](../Directory.Packages.props) pins direct packages; transitive pinning is disabled
and reviewed packages.lock.json files record resolution. Restore audits all dependencies at low severity and
treats advisories as errors. NuGet.Config uses nuget.org for packages/audit. Regenerate with `dotnet restore
ClaimCore.slnx --force-evaluate`, review diffs, then locked restore. `dotnet list ClaimCore.slnx package
--vulnerable --include-transitive --no-restore` is an explicit audit. Node/npm pins and package-lock.json own
the frontend graph; install with npm ci.

## Generated contracts and boundary verification

```sh
dotnet run --project eng/ClaimCore.ContractGenerator -- --output contracts
```

The [generator program](../eng/ClaimCore.ContractGenerator/Program.fs) owns its output inventory.
Normal generation writes [contracts](../contracts/), [the CLI reference](cli.md) and
[browser chrome types](../web/src/generated/chrome-messages.ts). Default `--check` checks these outputs without
repairing them. Custom destinations contain only contract artifacts. Missing, stale or extra contract files fail.
Review generated diffs alongside their typed producer.

[Catalog ownership](architecture.md#cc-cat-001) explains schema derivation and locale partitioning.
CLI reads the typed catalog and embeds core plus CLI locales; the browser imports core plus browser locales.
The generator validates Template syntax, language parity, typed producers and renderer references, including
unreferenced-key controls. Frozen renderings and independent quoting vectors check both clients.
Web commands run neither generation nor .NET.

OpenAPI supplies read-only protocol summaries. Consumer identity hashes canonical OpenAPI only; changing
source prose preserves it. Independent wire examples and JSON Schema/OpenAPI parsers check admission;
the Python oracle pins durable bytes. Never compute independent expectations with the product under test.
Compiled architecture controls and published CLI closure checks enforce component boundaries. Maintained
oxlint rules enforce browser imports, cycles and protocol effect restrictions; their injected violations
run in the lint stage. Full type-aware application lint remains a separate frontend check.

## Synthetic growth qualification

Run `bash eng/verify.sh growth` before a release and on demand. It is outside the required gate; the manual
CI growth input runs the same stage. [GrowthQualificationTests](../tests/ClaimCore.IntegrationTests/GrowthQualificationTests.fs)
and its supporting modules own profiles, contention, cardinality, sessions and measurement settings.
`CLAIMCORE_GROWTH_PROFILE=writes:writers` selects one declared profile for iteration and marks the report as
incomplete qualification. Reports under ignored `artifacts/verification/growth` record revision, dirty state,
counts, latency, throughput, verifier memory/time and database temporary work. Unmeasured metrics remain
explicit. These measurements promise no production SLA or constant total-system memory.

Native-language/assistive-technology review remains human.

Private-file qualification exercises real objects on linux-x64, linux-musl-x64 and osx-arm64.
The managed reader checks final link targets, pre-open lengths, handle permissions and exact bounded
positional reads. FIFO refusals, descriptor-zero ownership, disposal, path substitution and failed-read
buffer clearing are checked. The [private-input limits](operations.md#private-configuration) include
the directory-writer/FIFO race; Windows refuses private inputs.

## Source releases

Public releases distribute tagged source archives through [GitHub Releases](https://github.com/resoltico/claimcore/releases).
They contain no installable application packages. Build and publication instructions above apply to that source.

Prepare pending release summaries under the policy in [CHANGELOG.md](../CHANGELOG.md).
Version, date and tag assignment require the owner's release authorization.
The release commit must agree across [Directory.Build.props](../Directory.Build.props), the dated changelog
heading and the annotated version tag. Complete the required verification and separate growth qualification
for the release candidate before publishing.

The [source release workflow](../.github/workflows/release.yml) runs only from main and accepts an existing
annotated tag and its full expected commit SHA. It checks that the tagged commit belongs to main, its version
matches the tag, and push CI for that tag succeeded. It extracts only that version's dated changelog section
and publishes it as the release body. An Unreleased section, an unqualified tag or a branch CI result alone
does not satisfy those checks.

## Verification records

CI logs/check status and local TRX record outcomes, never owner approval. Preserve full relevant failures and
state exactly which commands ran. A later passing partial check does not erase a failing full gate. Verify
delivered revisions from an isolated clean checkout; preserve user work and private state. Native CLI sink
controls exercise EPIPE, descriptors/PTYs, Unicode bytes, append/shared offsets, wrapper disposal and
regular-file failure through a managed helper. Linux additionally checks /dev/full. OS write acceptance does
not prove downstream consumption; both-output loss still leaves uncertain mutation exit 4, and sessions stop
before later frames.

See the [glossary](glossary.md) for project terminology.
