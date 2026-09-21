# Development

This document is the sole owner of contributor verification commands. Product operation belongs in
[Getting started](getting-started.md), [Web](web.md), [CLI](cli.md), and [Database](database.md).

## Prerequisites

- The .NET SDK selected by [`global.json`](../global.json).
- Node 26.9.0 from [`.node-version`](../.node-version) and the npm release declared by
  [`web/package.json`](../web/package.json). On this workstation, run frontend commands through the
  configured Node 26 toolchain rather than the operating-system default Node executable.
- Docker for PostgreSQL integration, published acceptance, and infrastructure checks.
- A C compiler available as `cc` (Apple Command Line Tools on macOS or a distribution compiler on
  Linux); locked .NET builds and publishes compile the private-file descriptor shim.
- PowerShell 7 for policy gates and CI-equivalent test reporting.
- Git, Bash, ShellCheck, Actionlint, Gitleaks, `jq`, `curl`, and OpenSSL.
- The pinned Playwright Chromium, Firefox, and WebKit revisions for browser qualification.

Run commands from the repository root. Use only synthetic data and isolated test databases.

## First checkout

```text
dotnet tool restore
npm --prefix web ci
dotnet restore ClaimCore.slnx --locked-mode
dotnet build ClaimCore.slnx --configuration Release --no-restore
```

Committed NuGet and npm lock files make both dependency graphs reproducible. A missing or stale lock
is a repository defect, not a reason for CI to manufacture a new baseline. An ordinary .NET build
does not produce or consume browser assets.

## Local Docker disk hygiene

Check usage before considering cleanup:

```text
docker system df
docker buildx ls
docker buildx du
```

[`docker system df`](https://docs.docker.com/reference/cli/docker/system/df/) summarizes daemon
storage; [`docker buildx du`](https://docs.docker.com/reference/cli/docker/buildx/du/) reports cache
for the selected builder. These commands are read-only. A volume marked *reclaimable* is merely
unused by a current container, not known to be disposable: this Docker daemon may also hold other
projects' data. ClaimCore's Compose `postgres-data` volume is persistent. Normal
`docker compose down` retains it; do not use `docker compose down --volumes` for an adopted database.

Fallback test-container cleanup is limited to containers with the exact current ClaimCore test-run
label and their anonymous volumes. It never targets the named Compose volume. Do not schedule
host-wide `docker system prune` or `docker volume prune`, or infer ownership from a volume's name or
reclaimable status.
If build cache itself needs attention, identify a builder you own with `docker buildx ls`, then
*explicitly* run `docker buildx prune --builder BUILDER_NAME --filter 'until=168h'` after replacing
`BUILDER_NAME`. [Buildx prune](https://docs.docker.com/reference/cli/docker/buildx/prune/) affects
that builder's eligible cache records, not just ClaimCore's, and prompts before removal without
`--force`; do not use it on a shared builder without coordinating with its other users.

## Complete verification

A complete result is conjunctive: locked restore, compiler build, repository policy, every required
test project, generated semantic/CLI-v3/Web-v2 contract check, frontend assurance, documentation,
fresh and upgrade-through-006 database qualifications, published CLI acceptance, published browser
lifecycle, coverage, and evidence must all succeed for the same source. Do not relabel one green
family as the whole gate.

Required tests must be zero-retry and unfiltered. Focused, pending, skipped, expected-failure,
conditional, filtered, retried, or sharded required tests fail policy. Commands that actually ran and
their outcomes must be reported separately from source inspection.

### .NET tests

.NET 10 uses Microsoft Testing Platform v2 as the test driver. Expecto is the test DSL and its adapter
runs through Microsoft's supported bridge. There is no `Microsoft.NET.Test.Sdk`, VSTest command path,
manual test entry point, dual runner, or VSTest coverage collector.

Run every project explicitly:

```sh
dotnet test --project tests/ClaimCore.Tests/ClaimCore.Tests.fsproj \
  --configuration Release --no-build --no-restore \
  --minimum-expected-tests=205 --zero-tests-policy=strict --timeout=10m -- \
  --settings="$PWD/eng/expecto.runsettings"
dotnet test --project tests/ClaimCore.WebTests/ClaimCore.WebTests.fsproj \
  --configuration Release --no-build --no-restore \
  --minimum-expected-tests=55 --zero-tests-policy=strict --timeout=10m -- \
  --settings="$PWD/eng/expecto.runsettings"
dotnet test --project tests/ClaimCore.DocsTests/ClaimCore.DocsTests.fsproj \
  --configuration Release --no-build --no-restore \
  --minimum-expected-tests=54 --zero-tests-policy=strict --timeout=10m -- \
  --settings="$PWD/eng/expecto.runsettings"
dotnet test --project tests/ClaimCore.IntegrationTests/ClaimCore.IntegrationTests.fsproj \
  --configuration Release --no-build --no-restore \
  --minimum-expected-tests=100 --zero-tests-policy=strict --timeout=30m -- \
  --settings="$PWD/eng/expecto.runsettings"
dotnet test --project tests/ClaimCore.RecoveryQualificationTests/ClaimCore.RecoveryQualificationTests.fsproj \
  --configuration Release --no-build --no-restore \
  --minimum-expected-tests=19 --zero-tests-policy=strict --timeout=20m -- \
  --settings="$PWD/eng/expecto.runsettings"
dotnet test --project tests/ClaimCore.ConcurrencyQualificationTests/ClaimCore.ConcurrencyQualificationTests.fsproj \
  --configuration Release --no-build --no-restore \
  --minimum-expected-tests=5 --zero-tests-policy=strict --timeout=20m -- \
  --settings="$PWD/eng/expecto.runsettings"
dotnet test --project tests/ClaimCore.MigrationQualificationTests/ClaimCore.MigrationQualificationTests.fsproj \
  --configuration Release --no-build --no-restore \
  --minimum-expected-tests=7 --zero-tests-policy=strict --timeout=20m -- \
  --settings="$PWD/eng/expecto.runsettings"
dotnet test --project tests/ClaimCore.FuzzQualificationTests/ClaimCore.FuzzQualificationTests.fsproj \
  --configuration Release --no-build --no-restore \
  --minimum-expected-tests=5 --zero-tests-policy=strict --timeout=15m -- \
  --settings="$PWD/eng/expecto.runsettings"
```

`ClaimCore.FuzzQualificationTests` runs on all three platforms in CI and its TRX is reconciled in
final evidence like every other required suite. It needs no database. It drives every boundary that turns externally
supplied bytes or opaque tokens into typed values - strict JSON, CLI invocation framing, canonical
request and snapshot records, recovery envelopes, and history and recovery cursors - with arbitrary
bytes, mutated valid encodings, adversarial JSON, and invalid UTF-8. A boundary passes only by
refusing hostile input with a typed result; an escaping exception fails the property and prints a
deterministic recheck token. It shares the property profile and base seed described below, so the
scheduled extended run explores the same boundaries at 5,000 cases.

The integration and qualification processes create exactly labelled isolated PostgreSQL containers.
The separate qualification executables prevent a generic integration pass from being reported as
recovery, concurrency, or migration evidence.

`ClaimCore.WebTests` includes production-route `TestServer` requests for all nineteen generated
Web-v2 endpoints, real session cookies and antiforgery admission, retired-route 404 behavior, raw
import bounds, and typed host failures. Its direct-context tests still cover narrower decoder and
wire projection seams; those do not substitute for route execution.
Windows CI builds and exercises fail-closed private-file branches, but the current private-file
runtime contract supports macOS and Linux only; Windows is not a published first-run target.

The deterministic unit and fuzz profiles run 200 cases per property. The scheduled extended profile
runs 5,000 for both:

```sh
CLAIMCORE_PROPERTY_PROFILE=extended CLAIMCORE_PROPERTY_BASE_SEED=<unsigned-seed> \
dotnet test --project tests/ClaimCore.Tests/ClaimCore.Tests.fsproj \
  --configuration Release --no-build --no-restore \
  --minimum-expected-tests=205 --zero-tests-policy=strict --timeout=20m -- \
  --settings="$PWD/eng/expecto.runsettings"
```

The base seed must be a canonical unsigned integer. CI chooses and records the weekly seed through
`eng/Select-PropertySeed.ps1` so a failure can be reproduced exactly.

### Architecture inspection

The architecture suite supplements the Release behavioral tests with non-optimised Debug
implementation inspection. It uses the existing Expecto/Microsoft Testing Platform driver, not a
second test framework. After the locked solution restore:

```sh
dotnet build tests/ClaimCore.ArchitectureTests/ClaimCore.ArchitectureTests.fsproj \
  --configuration Debug --no-restore -p:Optimize=false
claimcore_arch_results="artifacts/architecture-inspection/run-$(date -u +%Y%m%dT%H%M%SZ)"
test ! -e "$claimcore_arch_results"
CLAIMCORE_ARCHITECTURE_REPORT="$PWD/$claimcore_arch_results/architecture-report.json" \
dotnet test --project tests/ClaimCore.ArchitectureTests/ClaimCore.ArchitectureTests.fsproj \
  --configuration Debug --no-build --no-restore --results-directory="$claimcore_arch_results" \
  --minimum-expected-tests=86 --zero-tests-policy=strict --timeout=10m -- \
  --settings="$PWD/eng/expecto.runsettings"
```

CI runs this suite on Linux, macOS and Windows. Historical test-baseline registrations remain
immutable; new explicitly registered producers extend the live inventory without rewriting that
baseline. Its named TRX results and stage manifests are required by the same final evidence
reconciliation as the existing suites. No coverage collector rewrites these inspection inputs.
Required assembly/selector preflight and positive/negative F# fixtures qualify the compiled
inspection mechanism. Each architecture run requires `CLAIMCORE_ARCHITECTURE_REPORT` and writes a
bounded observed type/edge report under its fresh ignored results directory; an unset/blank path,
missing parent, or preexisting report fails the suite rather than silently omitting the observation.
CI scans and binds all three reports to stage manifests, rechecks
their schema and hashes at final evidence, and displays a compact graph in the job summary. Type
counts are observations, not fixed thresholds. Raw and evaluated project-reference checks reject
forbidden unused edges and stale permissions alike; selected ambient-effect and direct-call rules
are deliberately narrower than full effect or semantic proofs.
See [Architecture](architecture.md#compiled-architecture-enforcement).

#### Changing the component graph

[`architecture.json`](../architecture.json) is the only place a component's tier, layer,
responsibility, direct project edges, NuGet packages, or `InternalsVisibleTo` grants are declared.
To add, split, or retire a component:

1. Edit `architecture.json` and the affected `.fsproj` files together. Every `.fsproj` under `src/`,
   `eng/`, and `tests/` must be classified exactly once, and every declared edge must match the
   manifest in both directions.
2. Keep each `InternalsVisibleTo` attribute and its manifest entry in step. A grant must name a
   classified component that already references the granting one; an unmatched grant on either side
   fails.
3. Declare only edges, packages, and shared frameworks that the component actually uses. For the
   product tier the compiled model is compared to the manifest, so a permitted-but-unused edge fails
   as a stale permission.
4. Run the architecture suite above, then refresh the generated component table with
   `ClaimCore.Docs write` so [Architecture](architecture.md#the-component-contract) cannot drift.
5. Regenerate the affected `eng/ClaimCore.Docs/test-inventory/*.json` entries, register any new test
   identity in `eng/assurance-matrix.json`, and update the expected counts in this document and in
   the workflows.

Do not add a compatibility edge, a transitional package, or a temporary grant: removing one requires
no allowance, and the manifest records only what the reviewed architecture permits today.

### Frontend assurance

Install the exact locked graph once, then run the frontend gates:

```text
npm --prefix web run format:check
npm --prefix web run typecheck
npm --prefix web run lint
npm --prefix web run lint:styles
npm --prefix web run dead-code
npm --prefix web run contract:check
npm --prefix web audit --audit-level=low
npm --prefix web audit signatures
npm --prefix web run licenses:check
npm --prefix web run sbom
npm --prefix web run test:unit
npm --prefix web run build
```

`npm run build` is the sole frontend asset producer. The native TypeScript compiler uses composite
project references and the Vitest suite uses isolated, machine-scaled file workers. Publication
requires the resulting manifest to match source, npm lock, generated semantic/CLI-v3/Web-v2 contract,
Node/npm versions, notices, and asset bytes. See [`web/README.md`](../web/README.md) for frontend
structure and the current compiler-API compatibility arrangement.

Contract generation is two deterministic stages: the F# generator writes canonical schemas, pure
codec corpora, and split DTO modules; the locked Node stage compiles the aggregate Web response graph
to typed AJV standalone core and recovery validator groups and finalizes the combined manifest. The
generated minified validator groups are the only source-analyzer exception for that output, are each
independently limited to 600 KiB, and are dynamically selected before response acceptance; their
exact exclusions remain registered in `analyzer-suppressions.json`.

Frontend corpus tests compare every generated CLI endpoint outcome kind and Web endpoint outcome tag
against the exact response schemas, in addition to validating positive, malformed, and cross-endpoint
samples. This is wire-conformance evidence, not a claim that every runtime branch was exercised.

### Repository quality

```text
bash eng/Check-Fantomas.sh
node --test eng/release/*.test.mjs
pwsh -NoProfile -File eng/Check-AnalyzerSuppressions.ps1
pwsh -NoProfile -File eng/Test-AnalyzerSuppressionPolicy.ps1
pwsh -NoProfile -File eng/Test-SensitiveOutputPolicy.ps1
pwsh -NoProfile -File eng/Test-CoverageInputPolicy.ps1
pwsh -NoProfile -File eng/Test-MergedCoveragePolicy.ps1
pwsh -NoProfile -File eng/Test-PropertySeedPolicy.ps1
pwsh -NoProfile -File eng/Test-TestDiagnosticPrivacy.ps1
pwsh -NoProfile -File eng/Test-ArtifactSecretScanPolicy.ps1
pwsh -NoProfile -File eng/Check-ArtifactUploadPolicy.ps1
pwsh -NoProfile -File eng/Check-WorkflowToolchainPolicy.ps1
pwsh -NoProfile -File eng/Test-WorkflowToolchainPolicy.ps1
pwsh -NoProfile -File eng/Test-ArtifactUploadPolicy.ps1
pwsh -NoProfile -File eng/Check-ConvergenceAssurance.ps1
pwsh -NoProfile -File eng/Test-ConvergenceAssurancePolicy.ps1
pwsh -NoProfile -File eng/Check-DependencyCurrency.ps1
bash eng/Check-FSharpLint.sh
actionlint -color .github/workflows/*.yml
shellcheck eng/*.sh db/*.sh
bash eng/Test-LabeledTestContainerCleanup.sh
bash eng/Test-ComposePolicy.sh
pwsh -NoProfile -File eng/Check-GitIgnorePolicy.ps1
pwsh -NoProfile -File eng/Test-SourceSecretScanPolicy.ps1
pwsh -NoProfile -File eng/Scan-SourceSecrets.ps1
```

Use Fantomas without `--check` to format changed F# files. The FSharpLint gate applies its configured
syntax-tree rules after the strict compiler has type-checked the solution. FSharpLint, ESLint,
Stylelint, and the centralized physical-line policy enforce size and complexity limits across product
and test code; repair findings instead of weakening a rule.

Every workflow selects its toolchain through [`.github/actions/toolchain`](../.github/actions/toolchain/action.yml),
which sets up the SDK from `global.json`, the Node release from `.node-version`, and then proves the
runner is actually using both, including the npm release that no setup input pins. A workflow that
selected a toolchain itself, checked out with persisted credentials, or referenced an action by tag
would fail `Check-WorkflowToolchainPolicy.ps1`; its negative controls keep that gate honest. The same
check covers composite actions, and Dependabot scans their directory alongside the workflows.

The Git-ignore gate checks private/generated probes and public release inputs through an isolated
temporary Git database; it never initializes the working tree. The source-secret gate snapshots
exactly the tracked plus nonignored-untracked source inventory. It
uses the same Git ignore semantics before and after repository initialization, includes an ignored
file if it was force-tracked, rejects reparse points and path collisions, and scans with redaction and
no repository allowlist or inline `gitleaks:allow` bypass. Ignored private or generated state is
deliberately outside this source gate. Every GitHub Actions artifact family is independently scanned
after production and before upload; a missing path, scanner failure, or detected secret prevents its
upload. The final evidence job also rescans the downloaded producer artifacts and its own report.
`eng/Check-ArtifactUploadPolicy.ps1` and its negative controls keep the upload guards complete when
workflows change. An artifact scan does not replace the source inventory or the browser harness's
known-secret output checks.

[`analyzer-suppressions.json`](../analyzer-suppressions.json) is the sole source-code exception
registry. Every suppression or generated exclusion requires an owner, exact file/rule/scope,
substantive rationale, and ISO `reviewOn` or `expiresOn` date. Exceptional directives require a
nearby rationale and `suppression-registry: file|rule|scope` reference. File-size, function-size,
complexity, focused-test, contract-drift, and security-boundary rules are non-suppressible.

`eng/test-baseline-v0.1.json` is immutable evidence of the pre-convergence test identity set.
`eng/test-lineage.json` maps every baseline identity to a retained or stronger replacement test, and
`eng/assurance-matrix.json` maps every typed endpoint, outcome, recovery transition, cancellation
boundary, CLI-v3/Web-v2 transport branch, and GUI workflow to live tests. Run the convergence check
and its negative controls above after every test identity or matrix change. Each endpoint matrix row
must list the exact outcome tags parsed from its generated response schema; deleting an endpoint,
branch, outcome tag, or registered assurance subject fails policy. Test counts alone are not
evidence of coverage, and a codec corpus does not prove a runtime branch was exercised.

CI resolves the exact linux/amd64 and linux/arm64 children of the digest-pinned PostgreSQL image
index, creates a separate CycloneDX SBOM for each, and scans both for fixed high and critical
vulnerabilities. Its immutable Trivy invocation, image-index policy, and temporary exception policy
live in [`verify-quality.yml`](../.github/workflows/verify-quality.yml),
[`Check-PostgresImageAssurance.sh`](../eng/Check-PostgresImageAssurance.sh), and
[`container-vulnerability-exceptions.yaml`](../container-vulnerability-exceptions.yaml). The
manually dispatched [`publisher`](../.github/workflows/publish-postgres-image.yml) builds the
maintained PostgreSQL 18.6/Trixie derivative from a pinned official base and signed Debian snapshot;
it qualifies each architecture before pushing, then verifies the published child and index digests.
The application baseline changes only after that publication is qualified. Do not point an adopted
volume at a newly selected image without the operator's backup and planned downtime.

### Documentation assurance

Build the solution first, then check every Markdown file, generated help block, exact-case local link
and anchor, contract declaration, and current hash-bound review attestation:

```text
dotnet artifacts/bin/ClaimCore.Docs/release/ClaimCore.Docs.dll check
```

Maintainers use `ClaimCore.Docs write` only to refresh registered generated bodies. A second write
must be byte-idle. Contract IDs remain in their registered owner documents, evidence-test leaf names
start with one matching `[CC-…]` token, and review hashes are refreshed only after reviewing the exact
contract and assertion sources. The hash detects later drift; it does not independently prove the
quality or identity of the reviewer.

### Published acceptance

The CLI acceptance harness builds fresh CLI and Database publish trees with their license, .NET SBOM,
third-party notices, and immutable manifests, then runs the registered process tests against an
isolated migrated database:

```text
bash eng/Run-PublishedCliAcceptance.sh
```

For a local three-engine Web lifecycle, first install the pinned browser revisions. Produce Web
assets, publish Web and Database into new ignored directories, then invoke the harness:

```sh
npm --prefix web exec -- playwright install --with-deps chromium firefox webkit
npm --prefix web run build
claimcore_browser_publish="artifacts/browser-publish/run-$(date -u +%Y%m%dT%H%M%SZ)"
test ! -e "$claimcore_browser_publish"
dotnet publish src/ClaimCore.Web/ClaimCore.Web.fsproj --configuration Release --no-restore --output "$claimcore_browser_publish/web" -p:UseAppHost=false
dotnet publish src/ClaimCore.Database/ClaimCore.Database.fsproj --configuration Release --no-restore --output "$claimcore_browser_publish/database" -p:UseAppHost=false
bash eng/Run-PublishedWebE2E.sh "$claimcore_browser_publish/web" "$claimcore_browser_publish/database" all
```

The harness rejects a Vite server. It creates a separate database, HTTPS host, state directory,
credential, case references, and operations per engine, then exercises login, every command,
current/history reads, exact recovery submission/export/dismissal, downloads, and accessibility
against published bytes. Frontend unit tests cover clipboard success and fallback behavior. The `all`
scope runs the isolated engines concurrently. Private diagnostics are destroyed; only the sanitized
versioned result is retained.

### Coverage and evidence

Frontend unit tests enforce their configured coverage floors. The CI unit, integration, and browser
jobs emit independent .NET Cobertura inputs; the coverage job first validates that exact input set,
then merges it and enforces repository and Web-specific line and branch floors. It cannot be replaced
by rerunning one convenient test family after the fact.
Each published-browser input must contain measured `ClaimCore.Web` production branches; a successful
browser lifecycle with an empty instrumentation report is not coverage evidence.

The same merge/floor procedure runs locally and in CI after all six independent Cobertura inputs are
available. Choose a new ignored output directory; the script refuses to overwrite an existing one:

```sh
pwsh -NoProfile -File eng/Invoke-MergedCoverage.ps1 \
  -InputRoot artifacts/coverage-input \
  -OutputRoot artifacts/coverage-report/local-$(date -u +%Y%m%dT%H%M%SZ)
```

`eng/CoverageThresholds.psm1` enforces the unchanged merged line/branch floors of 60%/40% and each
ClaimCore.Web package at 80%/70%. The floor negative controls cover exact pass boundaries, just-below
failures, missing or weak Web packages, nonfinite rates, and ignored DTD entity references.

The final evidence job reconciles source identity, locks, stage manifests, test inventories, TRX,
browser reports, coverage, publish manifests, documentation synchronization, and repository review
attestations for the same attempt. Generated reports belong under ignored `artifacts/` or
current-attempt CI artifacts, not in source.

## Dependency updates

Dependency ownership is ecosystem-specific:

| Dependency class | Version owner | Locked graph or immutable identity |
|---|---|---|
| .NET SDK | `global.json` | Exact SDK with roll-forward disabled. |
| NuGet packages | `Directory.Packages.props` | Per-project `packages.lock.json` files. |
| .NET repository tools | `.config/dotnet-tools.json` | The tool manifest itself. |
| Node.js and npm | `.node-version` and `web/package.json` | Exact engine and package-manager declarations. |
| Frontend packages | `web/package.json` | `web/package-lock.json`. |
| PostgreSQL container | `db/postgresql-baseline.json` | Exact image tag and digest. |
| GitHub Actions | Workflow `uses` entries | Full action commit SHA with reviewed version comment. |

For an intentional NuGet update:

1. Edit the central package version.
2. Regenerate all affected locks with
   `dotnet restore ClaimCore.slnx --force-evaluate -p:RestoreLockedMode=false`.
3. Review every direct and transitive change.
4. Restore in locked mode and run complete verification.

For an intentional frontend update, edit the manifest through npm so it updates `package-lock.json`,
review the exact graph and lifecycle scripts, run `npm ci`, then run frontend and complete
verification. Apply the equivalent owner-and-lock discipline to SDKs, tools, images, and actions. Do
not delete selected locks, hand-edit generated locks, or let CI choose a new graph.

`eng/Check-DependencyCurrency.ps1` rejects stale, deprecated, or vulnerable direct packages and
unreviewed transitive updates. A temporary compatibility boundary must be exact, owned, justified,
and review-dated in [`dependency-holds.json`](../dependency-holds.json); a hold is not permission to
leave an update unexamined.

## Reporting results

List commands actually executed with their outcomes. Separately identify conclusions drawn from
source or configuration inspection. If Docker, a browser engine, or another prerequisite was
unavailable, state that explicitly; never imply the corresponding gate ran.
