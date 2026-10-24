# Development

## First checkout

Use the SDK in [global.json](../global.json), Node/npm in [.node-version](../.node-version) and
[web/package.json](../web/package.json), Bash, Python 3 for the independent durable oracle, and Docker for
integration/acceptance/workflow checks. Runtime private-file support is macOS/Linux.
Unix sink controls use sh, mkfifo, dd, stty, script and Linux prlimit.

```sh
dotnet restore ClaimCore.slnx --locked-mode
dotnet tool restore
npm --prefix web ci
bash eng/verify.sh browsers
bash eng/verify.sh
```

Ordinary .NET builds run neither npm nor generation. Frontend commands need no .NET/Python.
Framework-dependent apphost publication is qualified; self-contained, trimmed and single-file modes remain
unqualified. Preserve the entire publish directory and reviewed notices from
[eng/licenses](../eng/licenses/third-party-notices.json). No C compiler or native library is required.

## Verification stages

`bash eng/verify.sh` is the complete gate. `--fast` selects lint/unit/frontend only and is not a complete
gate. Named stages execute in order, stop at failure and share restore/build work.

| Stage | Checks |
| --- | --- |
| lint | Fantomas and FSharpLint |
| unit | Build, unit/property and compiled architecture suites |
| frontend | npm ci/audit, format/lint/types, browser unit completion, Knip/licenses/build/notices |
| workflows | ShellCheck and pinned Docker Actionlint |
| integration | Complete postgres then web suites |
| postgres / web | Complete selected isolated suite with its project closure |
| browsers | Locked Playwright engine/system dependency installation |
| acceptance | Published service/CLI and real browser qualification |

Native tests run Microsoft Testing Platform with automatic `[<Tests>]` discovery, evaluated Compile membership
and nonempty-run enforcement. Every test project is assigned exactly once to a stage; missing/duplicate
assignments fail before restore. No hand-kept minimum counts exist. Focused, filtered, pending, skipped or
retried runs cannot satisfy verification. TRX and sanitized measurements are retained in unique ignored
`artifacts/verification/` directories, including failure evidence.

Default acceptance runs all specifications on Chromium, Firefox and WebKit, sequentially stopping on first
failed engine. `CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE=chromium|firefox|webkit` selects one;
`CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE=smoke` additionally requires an explicit engine. Partial runs are labelled
and never prove full qualification. Browser workers stay serial inside a fixture because actor grants/redirect
state are shared; isolated engine runners provide CI parallelism. Retries are zero. The installer retains
Chromium's headless shell for CLI PKCE, including Firefox/WebKit jobs, and omits unused full Chromium.
Fresh-login tests budget one admission window plus normal OIDC work. Their shared helper follows the actual
browser challenge and recovers only from a validated login-admission refusal.

CI runs independent lint/unit/frontend/PostgreSQL/Web jobs and requires every scheduled job in `Gate`.
PR, merge queue, main/tag and explicit dispatch use three isolated complete engines.
Every scheduled engine must succeed in the aggregate Gate. The frontend runs in the
pinned Node container without executable .NET. PostgreSQL majors/images come from
[postgresql-baseline.json](../db/postgresql-baseline.json); each listed major needs actual qualification.
`CLAIMCORE_TEST_POSTGRES_MAJOR` selects exactly one listed major; unset selects the first. Invalid selectors
and Docker prerequisites fail before expensive work. PostgreSQL uses [runner
settings](../eng/expecto.integration.runsettings) with at most two owned clusters concurrently; direct runs
must retain those settings. Other suites use general settings. Successful fixtures clean their own resources.
Failed fixtures retain private evidence; never target existing installations or prune unrelated
containers/volumes.

## Disk use and retained evidence

NuGet/npm caches retain downloads keyed by lockfiles; locked restore/npm ci/audit still run. SDK-free frontend
and acceptance share the same exact npm download key, without broad fallbacks. Build outputs, node_modules,
assets and test results are not cached. Shared local caches may serve other repositories; do not clear them
indiscriminately. GitHub eviction is not a peak-disk guarantee. Disk use comes from dependencies, images,
selected browsers, build/publication copies, isolated databases and retained evidence. There is no fixed
universal maximum or enforced whole-run quota. Measure the selected concurrency and retain failure evidence;
reclaim only proven owned resources. Properties/fuzzing generate bounded in-memory values, not per-case files
or on-disk corpora.

Managed test processes use bounded readers/timeouts and two-second teardown observation. Cleanup failures
retain the original failure; detached/reparented descendants remain outside process-tree kill guarantees.
Diagnostics exclude payloads, credentials and provider exception text.

## Changes

Read the owner contract and behavior tests before editing. An endpoint belongs in the typed registry; fields
belong in the field table, Domain admission and RecordFormat codec. Update affected consumers, SQL, tests,
source locales and docs together. Before 1.0.0 baseline replacement may explicitly refuse older installations;
the current build admits one exact source-bound baseline. There is no in-place upgrade or conversion;
preserve existing installations with matching software. Production and tests retain
function/lambda/member length 50 and complexity 12. Only inline named rule exceptions with reasons are
allowed. File/module length is not a gate. Required deterministic properties run 200 cases with state
sequences up to 20. Shrinking stops after 100 successful descent steps, retaining the failing replay token;
this is not a bound on candidate count or wall time. Decoder fuzzing uses at most 4,096 random bytes plus
fixed hostile cases, including a 12,008-byte escaped-control string. There is no mutation campaign or
environment switch for a longer/bypassed run. Negative controls must demonstrate that guards reject broken
properties.

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

The generator stages/validates an owned directory before replacement and emits exactly openapi.json, client.ts
and descriptors.json. `--check` compares exact inventory and bytes without repairing them; missing, stale or
extra files fail. Review generated diffs with their typed producer. The generator references
Contract/Domain/Application and builds independently from CLI. Consumer identity hashes canonical OpenAPI
only; editing source locale text preserves it, changing structural enums changes it. CLI embeds descriptors
and source locale JSON; the browser imports source catalogs and generated TypeScript, and interprets OpenAPI.
Web commands never run .NET. Node validates the restricted ICU subset, key/argument parity and Unicode safety.
Pure Contract template checks share independent accept/reject vectors with the reference parser. No generated
catalog copies are tracked. Independent hand-authored wire examples and JSON Schema/OpenAPI parsers check
admission; the Python oracle pins durable bytes. Never regenerate independent oracles from product
implementations. Compiled architecture controls and published CLI closure checks enforce the component
boundaries. Vitest's reporter refuses focused, skipped, todo, interrupted, retried and empty runs using actual
runner results. Module lint checks follow transitive imports, aliases and JavaScript helpers.

## Synthetic growth qualification

The PostgreSQL suite measures 64/256/1,024 synthetic writes, varied case/actor/grant counts and one versus
four writers in disposable containers. Reports under ignored artifacts/verification/growth retain latency,
throughput, heap and cumulative process peak working set. These are environment measurements, not asymptotic
bounds or production sizing. Verification streams rows but retains folded state/identities. Ticket-store
qualification measures bounded local session workloads; published acceptance separately covers real HTTPS
sessions and browsers. Native-language, assistive-technology and terminal bidi quality still require human
review.

## Verification records

CI logs/check status and local TRX record outcomes, never owner approval. Preserve full relevant failures and
state exactly which commands ran. A later passing partial check does not erase a failing full gate. Verify
delivered revisions from an isolated clean checkout; preserve user work and private state. Native CLI sink
controls exercise EPIPE, descriptors/PTYs, Unicode bytes, append/shared offsets, wrapper disposal and
regular-file failure through a managed helper. Linux additionally checks /dev/full. OS write acceptance does
not prove downstream consumption; both-output loss still leaves uncertain mutation exit 4, and sessions stop
before later frames.
