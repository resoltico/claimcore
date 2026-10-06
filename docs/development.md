# Development

This document is the sole owner of contributor verification commands. Product operation belongs in
[Getting started](getting-started.md), [Web](web.md), [CLI](cli.md), and [Database](database.md).

## Prerequisites

Run `node eng/ci/doctor.mjs` to see which of these a machine lacks and how to fix it. Every pin
is read from its owning file; the tool table below is generated.

- The .NET SDK selected by [`global.json`](../global.json).
- The Node release in [`.node-version`](../.node-version) and the npm release declared by
  [`web/package.json`](../web/package.json) and [`eng/package.json`](../eng/package.json). Run
  frontend and engineering commands through that toolchain rather than an operating-system default.
- Docker for PostgreSQL integration, published acceptance, and infrastructure checks.
- A C compiler available as `cc` (Apple Command Line Tools on macOS or a distribution compiler on
  Linux); locked .NET builds and publishes compile the private-file descriptor shim. It projects
  platform open flags and file metadata through a fixed-width ABI rather than managed guesses at
  libc constants or structure offsets. Deploy the shim and managed host from the same publish tree.
- Git, Bash, ShellCheck, `jq`, `curl`, and OpenSSL.
- The pinned downloadable tools in [`config/tools.json`](../config/tools.json) (actionlint, Gitleaks,
  shfmt, uv). Each entry names a version and a SHA-256 per platform; `node eng/ci/tools.mjs`
  installs them verified into `artifacts/tools/bin`, and the stage runner installs what a stage needs.
- The pinned Playwright Chromium, Firefox, and WebKit revisions for browser qualification.

<!-- generated:begin pinned-tools -->
| Tool | Version | Pinned platforms |
| --- | --- | --- |
| actionlint | 1.7.12 | darwin-arm64, darwin-x64, linux-arm64, linux-x64, win32-x64 |
| gitleaks | 8.30.1 | darwin-arm64, darwin-x64, linux-arm64, linux-x64, win32-arm64, win32-x64 |
| shfmt | 3.14.1 | darwin-arm64, darwin-x64, linux-arm64, linux-x64, win32-x64 |
| uv | 0.12.23 | darwin-arm64, darwin-x64, linux-arm64, linux-x64, win32-x64 |
<!-- generated:end pinned-tools -->

Run commands from the repository root. Use only synthetic data and isolated test databases.

## First checkout

```text
node eng/ci/setup.mjs
```

runs, in order, the locked `dotnet restore`, `dotnet tool restore`, `npm ci` for `eng` and `web`, the
pinned tool installation, contract generation, and the strict-compiler Release build. The same steps
by hand:

```text
dotnet restore ClaimCore.slnx --locked-mode
dotnet tool restore
npm --prefix eng ci
npm --prefix web ci
node eng/ci/tools.mjs
npm --prefix web run contract:generate
dotnet build ClaimCore.slnx --configuration Release --no-restore
```

Committed NuGet and npm lock files make both dependency graphs reproducible. npm installs enforce
the exact declared engines through each package's `.npmrc`. Repository NuGet configuration clears
inherited package sources, audit sources and package mappings before declaring the supported source.
Lock files retain ordinary Git text diffs so reviewers can inspect the full dependency changes. A missing or stale lock
is a repository defect, not a reason for CI to manufacture a new baseline. An ordinary .NET build
does not produce or consume browser assets.

## Orchestration tooling

Orchestration, policy checks and report verification are Node programs under `eng/ci/`, tested with
`node:test` in `eng/` (`npm --prefix eng test`). F# is used for the product and for the documentation
tool that needs product-independent Markdown parsing; Python only for the backup drills; Bash only for
container and PostgreSQL drills. There is no PowerShell. The pieces:

| Concern                | Entry point                                                    |
| ---------------------- | -------------------------------------------------------------- |
| Test suites            | `node eng/ci/suites/suite.mjs run ...`                         |
| Test inventories       | `node eng/ci/suites/inventory.mjs --check` / `--write`         |
| Stage plans            | `node eng/ci/run-stages.mjs <plan>`                            |
| Whole local CI         | `node eng/ci/run-local.mjs`                                    |
| Merged coverage        | `node eng/ci/coverage-policy/merge.mjs`                               |
| Secret scans           | `node eng/ci/scan/main.mjs source` / `artifacts <path>...`     |
| Published applications | `node eng/ci/publish/main.mjs build` / `verify`                |
| Pinned tools           | `node eng/ci/tools.mjs`, `node eng/ci/doctor.mjs`              |

## Local Docker disk hygiene

Check usage before considering cleanup:

```text
docker system df
docker buildx ls
docker buildx du
```

[`docker system df`](https://docs.docker.com/reference/cli/docker/system/df/) summarizes daemon
storage; [`docker buildx du`](https://docs.docker.com/reference/cli/docker/buildx/du/) reports cache
for the selected builder. These commands are read-only. A volume marked _reclaimable_ is merely
unused by a current container, not known to be disposable: this Docker daemon may also hold other
projects' data. ClaimCore's Compose `postgres-data` volume is persistent. Normal
`docker compose down` retains it; do not use `docker compose down --volumes` for an adopted database.

Fallback test-container cleanup is limited to containers with the exact current ClaimCore test-run
label and their anonymous volumes. It never targets the named Compose volume. Do not schedule
host-wide `docker system prune` or `docker volume prune`, or infer ownership from a volume's name or
reclaimable status.
If build cache itself needs attention, identify a builder you own with `docker buildx ls`, then
_explicitly_ run `docker buildx prune --builder BUILDER_NAME --filter 'until=168h'` after replacing
`BUILDER_NAME`. [Buildx prune](https://docs.docker.com/reference/cli/docker/buildx/prune/) affects
that builder's eligible cache records, not just ClaimCore's, and prompts before removal without
`--force`; do not use it on a shared builder without coordinating with its other users.

## Complete verification

Failed synthetic physical restores retain bounded PostgreSQL startup logs, container state and port
mappings under ignored owner-private `artifacts/restore-failures/` before container/scratch cleanup.
Console diagnostics remain stage-only; inspect retained files privately and do not paste or publish
them as ordinary test output. They do not establish a successful restore.

A complete result is conjunctive: locked restore, compiler build, repository policy, every required
test suite, the generated semantic/CLI-v4/Web-v3 contract lock, frontend assurance, documentation,
fresh-baseline creation/refusal database qualifications, published CLI acceptance, published browser
lifecycle, actual container operation, and merged coverage must all succeed for the same source. Do not relabel one green family
as the whole gate. CI's `Gate` job succeeds only when every family it lists in `needs` succeeded.

Required tests must be zero-retry and unfiltered. Focused, pending, skipped, expected-failure,
conditional, filtered, retried, or ad hoc sharded required tests fail policy. The only sharding is the
registered partitioning of the PostgreSQL integration suite. Commands that actually ran and their
outcomes must be reported separately from source inspection.

### Running CI locally

```sh
node eng/ci/run-local.mjs
```

runs, in order and stopping before spending more time once a job has failed, the CI jobs that can run on
one machine: the locked restore and strict-compiler build, the documentation check, the source and
dependency gates, the frontend product and gates, the cross-platform suites for this platform, and the
partitioned PostgreSQL suites and persistent Docker operation qualification. Each job runs the command CI runs. The jobs are registered in
[`eng/ci/local-plan.json`](../eng/ci/local-plan.json), which also lists every CI family with no local
equivalent (the other operating systems, the published-browser lifecycles and merged coverage) with the
reason, and a test holds that list to `ci.yml`. Jobs run exclusively one after another because they share one working
tree; each uses the machine's cores internally. Generated outputs under `artifacts/` are removed first.

By default a job runs only when a changed file, measured against the merge base with `origin/main` and
including uncommitted and untracked files, could affect it, so a documentation-only change skips the frontend
and database suites; `--changed-since REF` moves the base and `--all` runs everything. `--include published`
adds the published CLI acceptance (it publishes the applications and uses Docker). `--only id,id` and
`--skip id,id` select jobs (unknown IDs fail), `--no-fail-fast` continues past a failure, and logs go to
`artifacts/local-ci/<time>/<job>.log` with the tail of a failing log printed. A green local run is verification
of what ran here, not of the platforms and families it lists as not run; use the summary it prints.

### .NET tests

.NET 10 uses Microsoft Testing Platform v2 as the test driver. Expecto is the test DSL and its adapter
runs through Microsoft's supported bridge. There is no `Microsoft.NET.Test.Sdk`, VSTest command path,
manual test entry point, dual runner, or VSTest coverage collector.

Every suite is registered once in [`config/test-suites.json`](../config/test-suites.json): its kind, assembly,
project, build configuration, whether it collects coverage, the platforms it runs on, its timeout and, for the
integration suite, the partitions. **The registry holds no counts.** The expected tests of a suite are the lines
of its generated inventory, `tests/inventory/<assembly>.txt` (one display name per line, sorted, unique), which
is written by `node eng/ci/suites/inventory.mjs --write` from the built test executables and never edited by
hand. Changing, adding or removing a test therefore changes that file in the same commit, where review and
`CODEOWNERS` see it.

Reports must reconcile unique executed names against that inventory, with one flat result, definition
and entry section. Duplicate names cannot stand in for missing tests or partition coverage; misplaced
records and contradictory sections refuse qualification. The shared XML reader rejects malformed
version declarations without waiting for a job timeout and preserves every attribute for validation.
CLI fixture children clear inherited ClaimCore credentials/configuration and coverage instrumentation
before applying explicit synthetic settings; local operator configuration cannot select their service.

```sh
node eng/ci/suites/suite.mjs run unit web --build   # named suites
node eng/ci/suites/suite.mjs run --cross-platform   # every suite that runs on this platform
node eng/ci/suites/suite.mjs run --group postgres --build
```

For each suite, `--build` performs a locked restore and builds its declared inputs; shared inputs build once per configuration. The runner compares the built test executable's native discovery with the
inventory (and proves partitions are disjoint and cover exactly the inventory), runs `dotnet test` with
`--minimum-expected-tests` taken from the inventory, then verifies the TRX report structurally: one completed
run, exact counters, zero failures, every inventoried test passed exactly once, nothing else ran. Suites of the
`postgres` group (integration, witness, recovery, concurrency, migration, backup) run concurrently; the integration
suite runs as its registered partitions, each in its own process against its own primary and witness clusters
(`CLAIMCORE_INTEGRATION_PARTITION` selects one, no selection is the whole suite). Each measured partition runs a
private copy of the test output directory under its own results directory because Coverlet rewrites assemblies on disk. Rebalance partitions in
`tests/ClaimCore.IntegrationTests/Suite.fs`, then rewrite the inventory and the partition list in the registry.
`CLAIMCORE_PARALLEL_JOBS` or `--parallel` bounds concurrency. Results land in `artifacts/test-results/<suite>/`.

Contract tokens connect the documents to the tests: a contract heading `CC-xxx-nnn` in its owner document
must be named by at least one test in a registered suite whose name contains `[CC-xxx-nnn]`, and every `[CC-…]` token in a registered inventory
must name a declared contract. `ClaimCore.Docs check` enforces both directions and checks the generated [contract-test map](contract-tests.md). Orphan inventories cannot provide contract evidence.

`ClaimCore.FuzzQualificationTests` needs no database and runs on Linux in CI (its inputs are operating-system
independent). It exercises strict JSON, contract-owned HTTP input codecs, CLI invocation framing,
canonical request and snapshot records, recovery envelopes, and history, recovery and case-list cursors
with hostile byte/token corpora. Request, snapshot and encrypted recovery seeds have positive controls;
their byte mutations reach format parsing, and recovery uses matching synthetic keys. Authentication
normally refuses mutated envelopes before decryption, so this does not prove arbitrary authenticated
plaintext coverage. A mutation may remain valid: totality requires either a decoded value or typed
refusal, never an escaping exception. Failures print a deterministic recheck token without payloads.
The suite shares the property profile and base seed described below. Diagnostic rechecks can return
without executing non-target properties, so the required suite runner rejects recheck configuration
before restore or evidence creation. Use the native test executable directly for diagnostic replay;
its results cannot establish complete suite verification.

Unit transition sequences compare acceptance and complete resulting state against a separate finite
fixture model, including stale requests and no-op corrections. Negative controls reject refusal-only,
missing-payment, wrong-revision and payment-loss-on-closure outcomes. Canonical properties traverse all
eighteen correction group shapes in each generated case and verify literal modes. Identity checks vary
operation ID, revision, reference and command. Round trips and identity relations complement the fixed
independent encoding/digest vectors; they do not replace byte expectations or real storage evidence.

The integration and qualification processes create exactly labelled isolated PostgreSQL containers from the
official, digest-pinned image named in [`db/postgresql-baseline.json`](../db/postgresql-baseline.json) (the same
reference `deployment/local.compose.yaml` uses, held equal by a test). The separate qualification executables prevent a generic
integration pass from being reported as recovery, concurrency, or fresh-baseline evidence. Linux CI installs
PGDG-signed PostgreSQL 18.6 tools and checksum-pinned age 1.3.2 for the backup drills; it disables automatic
creation of a host PostgreSQL cluster. A local backup qualification needs the same PostgreSQL 18.6 tools in
`CLAIMCORE_PG_BIN` or `PATH` and age 1.3.2 in `PATH`.

`ClaimCore.WebTests` includes production-route `TestServer` requests for all thirty-three generated
Web-v3 endpoints, OIDC session cookies and antiforgery admission, retired-route 404 behavior, raw
import bounds, and typed host failures. Its direct-context tests still cover narrower decoder and
wire projection seams; those do not substitute for route execution. The HTTP fixtures share production
limiter registration and use a controlled fake facade for admission scheduling. The overload control
rejects an injected extra production permit; it does not prove PostgreSQL contention or the complete
Program pipeline. Published client suites exercise the actual delivered host.
Windows CI builds and exercises fail-closed private-file branches, but the current private-file
runtime contract supports macOS and Linux only; Windows is not a published first-run target.

The deterministic unit and fuzz profiles run 200 cases per property. The scheduled extended profile
runs 5,000 for both:

```sh
CLAIMCORE_PROPERTY_PROFILE=extended CLAIMCORE_PROPERTY_BASE_SEED=<unsigned-seed> \
  node eng/ci/suites/suite.mjs run unit fuzz --build --results-root artifacts/properties-results
```

The base seed must be a canonical unsigned integer. CI chooses and records the weekly seed through
`eng/ci/policy/property-seed.mjs` (a hash of the run's identity, or the seed a manual run supplies) so a failure
can be reproduced exactly.

### Architecture inspection

The architecture suite supplements the Release behavioral tests with non-optimised Debug
implementation inspection. It uses the existing Expecto/Microsoft Testing Platform driver, not a
second test framework, and runs on Linux in CI:

```sh
node eng/ci/suites/suite.mjs run architecture --build
```

The registry builds it with `-p:Optimize=false` and sets `CLAIMCORE_ARCHITECTURE_REPORT`; each run writes a
bounded observed type/edge report under its fresh ignored results directory, and CI shows a compact graph in the
job summary. An unset or blank path, missing parent, or preexisting report fails the suite rather than silently
omitting the observation. No coverage collector rewrites these inspection inputs. Required assembly/selector
preflight and positive/negative F# fixtures qualify the compiled inspection mechanism, and the inspection itself
fails when a required product assembly is omitted or the observed graph has no cross-product edge. Type counts are
observations, not fixed thresholds. Raw and evaluated project-reference checks reject forbidden unused edges and
stale permissions alike; selected ambient-effect and direct-call rules are deliberately narrower than full effect
or semantic proofs. See [Architecture](architecture.md#compiled-architecture-enforcement).

#### Changing the component graph

[`config/architecture.json`](../config/architecture.json) is the only place a component's tier, layer,
responsibility, direct project edges, NuGet packages, or `InternalsVisibleTo` grants are declared.
To add, split, or retire a component:

1. Edit `config/architecture.json` and the affected `.fsproj` files together. Every `.fsproj` under `src/`,
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
5. Register a new test project in `config/test-suites.json` and write its inventory.

Do not add a compatibility edge, a transitional package, or a temporary grant: removing one requires
no allowance, and the manifest records only what the reviewed architecture permits today.

### Frontend assurance

Install the exact locked graph once, generate the contracts, then run the frontend gates:

```text
npm --prefix web ci
npm --prefix web run contract:generate
npm --prefix web run format:check
npm --prefix web run typecheck
npm --prefix web run lint
npm --prefix web run lint:styles
npm --prefix web run dead-code
npm --prefix web run contract:check
node eng/ci/policy/npm-audit.mjs
npm --prefix web audit signatures
npm --prefix web run licenses:check
npm --prefix web run sbom
npm --prefix web run test:unit
npm --prefix web run test:mutation
npm --prefix web run build
```

`npm run build` is the sole frontend asset producer. The native TypeScript compiler uses composite
project references and the Vitest suite uses isolated, machine-scaled file workers. After Vitest,
`node eng/ci/suites/frontend.mjs vitest` compares its sanitized report with `tests/inventory/vitest.txt`; a stale
count or renamed leaf fails immediately. Browser reports are compared with `tests/inventory/browser.txt` the same
way (`node eng/ci/suites/frontend.mjs browser <engine>`), and `node eng/ci/suites/inventory.mjs --check --only
browser` compares that inventory with Playwright's own listing. Publication requires the resulting manifest to match
source, npm lock, generated semantic/CLI-v4/Web-v3 contract, Node/npm versions, notices, and asset bytes. See
[`web/README.md`](../web/README.md) for frontend structure and the current compiler-API compatibility arrangement.

The locked StrykerJS/Vitest mutation gate targets the four operation modules (metadata, initial state, request
freezing and the reducer) and presentation preference/parser, descriptor/identity and typed ICU message-rendering logic. It uses one test worker to limit contention during static-mutation suite imports. It requires at least 92% killed mutants across them, refuses ignored or incomplete mutant
results, and checks the exact sources, tool version and target set in an ignored local report. It does not exercise F#
or PostgreSQL and cannot replace the full tests, catalog checks or restored-data audit.

#### Generated contracts

Contract artifacts are generated, never tracked. The F# generator writes canonical schemas, pure codec corpora and
split DTO modules; the locked Node stage compiles the aggregate Web response graph to typed AJV standalone shared
host, discovery, core, and recovery validator groups (about 25 MB in all, written to the ignored
`web/src/generated/contracts/`). [`config/contracts.lock.json`](../config/contracts.lock.json) records the length and
SHA-256 of every artifact and is what a reviewer reads when a contract changes.

- `npm --prefix web run contract:generate` regenerates and requires the result to match the lock. CI runs it before
  the frontend jobs; the browser and acceptance jobs receive the frontend-product job's artifacts and verify them with
  `contract:verify`.
- `npm --prefix web run contract:lock` regenerates and writes a new lock. It is the explicit acceptance of a contract
  change; review the lock diff and the F# change together.
- `npm --prefix web run contract:check` verifies the generated directory against the lock without regenerating, and
  runs the React Aria and localization checks.

No test regenerates on its own. The F# unit tests derive the CLI raw-decoder corpus in process from the same
projection. The generated minified validator groups are the only source-analyzer exception for that output, are each
independently limited to 600 KiB, and are dynamically selected before response acceptance; their exact exclusions
remain registered in `config/lint-exceptions.json`.

Frontend corpus tests compare every generated CLI endpoint outcome kind and Web endpoint outcome tag
against the exact response schemas, in addition to validating positive, malformed, and cross-endpoint
samples. This is wire-conformance evidence, not a claim that every runtime branch was exercised.

### Repository quality

CI and local runs execute the source, dependency and infrastructure gates and the frontend gates through one
runner, `node eng/ci/run-stages.mjs quality` (or `frontend`, `frontend-product`), which reads the registered plan in
`eng/ci/stage-plans/`, runs independent stages concurrently (`--parallel N`, default the smaller of the core count and
4), serialises stages that share a resource group, and prints each stage's output as one group when it ends. Add
`--only id,id` to run some stages. A stage that needs a pinned tool installs it first, at the pinned version; a stage
whose other required tool is absent fails both locally and in CI. Unknown selections fail before execution.
The table shows each stage's arguments and additional inputs; run the plan to resolve source selectors and templates.

<!-- generated:begin quality-stages -->
| Stage | Command | Inputs and ordering |
| --- | --- | --- |
| lint-exceptions | node eng/lint/check-exceptions.mjs |  |
| suite-registry | node eng/ci/suites/check-registry.mjs |  |
| clean-source | node eng/ci/clean-source.mjs | requires dotnet; exclusive |
| eng-tests | npm --prefix eng test |  |
| eng-format | npm --prefix eng run format:check |  |
| eng-types | npm --prefix eng run typecheck |  |
| eng-lint | npm --prefix eng run lint |  |
| eng-npm-audit | npm --prefix eng audit --audit-level=low |  |
| eng-npm-signatures | npm --prefix eng audit signatures |  |
| python-format | uv run --frozen ruff format --check --no-cache | requires uv |
| python-lint | uv run --frozen ruff check --no-cache | requires uv |
| python-types | uv run --frozen mypy | requires uv |
| python-limits | uv run --frozen python -B eng/lint/check_python_limits.py | requires uv |
| python-audit | uv audit --frozen | requires uv |
| git-ignore-policy | node eng/ci/policy/ignore.mjs |  |
| source-secret-scan | node eng/ci/scan/main.mjs source |  |
| test-diagnostic-privacy | node eng/ci/policy/diagnostic-privacy.mjs |  |
| fantomas | bash eng/Check-Fantomas.sh |  |
| fsharplint | bash eng/Check-FSharpLint.sh |  |
| actionlint | actionlint -color | requires actionlint |
| workflow-security | uv run --frozen zizmor --persona pedantic --config .github/zizmor.yml --no-progress --format=plain .github | requires uv |
| workflow-policy | node eng/ci/check-workflows.mjs |  |
| shfmt | shfmt -d | requires shfmt; append .sh source under eng, db, deployment |
| shellcheck | shellcheck -x | requires shellcheck; append .sh source under eng, db, deployment |
| compose-config | CLAIMCORE_COMPOSE_PROJECT="claimcore-config-{runId}" CLAIMCORE_CONFIG_DIR="/tmp/claimcore-config-{runId}" CLAIMCORE_SERVICE_UID="1654" CLAIMCORE_SERVICE_GID="1654" CLAIMCORE_HOST_PORT="0" docker compose --file deployment/compose.yaml config --quiet | requires docker |
| docker-cleanup-assurance | bash eng/Test-LabeledTestContainerCleanup.sh | requires docker; resource docker |
<!-- generated:end quality-stages -->

Use Fantomas without `--check` to format changed F# files. The FSharpLint gate applies its configured
syntax-tree rules after the strict compiler has type-checked the solution. FSharpLint, oxlint (type-aware,
on the TypeScript 7 native toolchain), Stylelint, and the centralized physical-line policy enforce size and complexity
limits across product and test code; repair findings instead of weakening a rule.

Every workflow selects its toolchain through [`.github/actions/toolchain`](../.github/actions/toolchain/action.yml),
which sets up the SDK from `global.json`, the Node release from `.node-version`, and then proves the
runner is actually using both, including the npm release that no setup input pins. Two independent layers keep
workflows safe: actionlint and zizmor (pedantic persona, with GitHub-backed audits in CI) apply the industry
rules, and `node eng/ci/check-workflows.mjs` applies the repository's own: toolchain selection only through the
composite action, no persisted checkout credentials, full-commit action pins with a reviewed version comment,
read-only default permissions with publication confined to the protected `release` environment on `main`, pinned
runner images, the mandatory graph (every family reachable from `Gate`), and scan-before-upload. Its negative
controls run in `npm --prefix eng test`. Dependabot scans composite actions alongside workflows.
[CI governance](ci-governance.md) owns workflow integrity and the repository-settings procedure.

The Git-ignore gate checks the private/generated probes and public release inputs listed in
[`config/git-ignore-policy.json`](../config/git-ignore-policy.json) through an isolated temporary Git database; it
never initializes the working tree. The source-secret gate snapshots exactly the tracked plus nonignored-untracked
source inventory. It uses the same Git ignore semantics before and after repository initialization, includes an
ignored file if it was force-tracked, rejects links and path collisions, and scans with redaction and no repository
allowlist or inline `gitleaks:allow` bypass. Ignored private or generated state is deliberately outside this source
gate. Every GitHub Actions artifact family is independently scanned after production and before upload
(`node eng/ci/scan/main.mjs artifacts <path>...`); a missing path, scanner failure, or detected secret prevents its
upload. The scanner is the checksum-verified binary from `config/tools.json`; a safe failure-stage label
distinguishes unavailable acquisition from scan execution without disclosing artifact paths or content, and neither
condition permits upload. An artifact scan does not replace the source inventory or the browser harness's
known-secret output checks (`eng/ci/policy/sensitive-output.mjs`).

Every language runs its linter in its strictest useful mode, and each mode is pinned so it cannot be
weakened in passing:

| Language                                 | Gate                                                       | Strict mode                                                                                                                      |
| ---------------------------------------- | ---------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| F#                                       | compiler, Fantomas (no roll-forward), FSharpLint           | warnings as errors; size and complexity ceilings pinned by the exception engine                                                  |
| TypeScript and JavaScript (`web`, `eng`) | TypeScript 7 `tsc`, oxlint with type-aware rules, Prettier | correctness, suspicious, pedantic, perf and style categories at `error`, denied warnings; typed rules scoped to TypeScript files |
| Python (`eng/backup`)                    | uv-locked ruff, mypy, function-length check, `uv audit`    | every ruff rule selected, mypy `strict`, 50-line functions, 300-line files, pylint argument and statement ceilings pinned        |
| Shell                                    | shellcheck, shfmt                                          | every optional check at `style` severity through `.shellcheckrc`; shfmt settings in `.editorconfig`                              |
| GitHub workflows                         | actionlint, zizmor, repository workflow policy             | zizmor pedantic persona; the only disabled audit is registered                                                                   |

Shared Oxlint declarations live in [`config/oxlint.json`](../config/oxlint.json); package configs
extend them and own their environments, plugins and scope-specific rules. Compiler settings remain inside each package root so isolated mutation sandboxes retain their
complete configuration. Both engineering and frontend JavaScript tooling are checked by the native TypeScript compiler. Browser globals belong
to browser source and tests, while orchestration uses Node globals. Retained Web asset identity
includes the full repository-local compiler inheritance graph, including the frontend base settings.

The exception engine (`node eng/lint/check-exceptions.mjs`) pins inherited and overridden oxlint categories and the Python
ceilings, so relaxing them fails the gate. `node eng/ci/suites/check-registry.mjs` holds the repository to the suite
registry: every test project is registered, every registered file exists, every inventory belongs to a suite, and
no workflow or script repeats a test count. Runner images are pinned to exact labels, and telemetry settings live in
the toolchain action.

Evaluated and not adopted: F# analyzers (G-Research and Ionide) report about 740 findings, of which about
560 ask for typed interpolation holes on every `$"..."`, and the "unsafe option unwrapping" findings mostly flag
`.Value` on validated wrapper types; adopting them would need hundreds of mechanical edits for little defect-finding
value, so FSharpLint and the strict compiler remain the F# gates. F# mutation testing is also not adopted; StrykerJS
covers the pure TypeScript operation-domain modules, and F# behavior is covered by the property, integration and
qualification suites. Splitting `ClaimCore.Postgres` into runtime and administration assemblies was prototyped and
rejected: the two halves share types and `internal` members across hundreds of files, so the split would move shared
types without adding a safety property; the boundary that matters, case-work hosts having no direct compilation access to schema
administration, is held by `ClaimCore.Hosting` and the architecture manifest.

[`config/lint-exceptions.json`](../config/lint-exceptions.json) is the sole registry of lint, type, format and
coverage exceptions for every language. Every entry has a stable `LX-nnnn` id, the tool, the exact rules (never a
blanket), one exact file, a kind, an exact occurrence count, a substantive reason, an owner and an ISO `reviewOn`
or `expiresOn` calendar-valid date; malformed entries, duplicate rules and unknown fields fail rather
than disappearing during loading. Generated-output exclusions are a separate reviewed list of recognized paths. An inline
suppression (`// oxlint-disable-next-line`, `# noqa`, `#nowarn`, `# shellcheck disable=`, `@ts-expect-error`,
`prettier-ignore`, coverage ignores and their equivalents) must carry `lint-exception: LX-nnnn` in its own comment or
the line above; no line numbers are recorded, so edits above a suppression never break it. Configuration-level
ignores (ignore patterns, `per-file-ignores`, mypy overrides, disabled rules, zizmor audit settings, knip ignores,
`.prettierignore`, `NoWarn`, `dotnet_diagnostic` severities) are matched by file, tool and target. An entry with no
remaining occurrence is stale and fails, as does any occurrence without an entry or a count that differs.
`node eng/lint/check-exceptions.mjs` runs the check; its tests (`npm --prefix eng test`) build isolated repository
trees for every scanner and policy. File-size, function-size, complexity, focused-test, skipped-test, test-filter,
retry, contract-drift and security-boundary rules are non-suppressible and enforced by the same command.

Endpoint and outcome completeness is proven where it is actually exercised: tests iterate the generated endpoint and
outcome catalogs (route-map dispatch, response-schema corpus coverage), so deleting an endpoint, branch or outcome tag
fails a test rather than a registry. Test counts alone are not evidence of coverage, and a codec corpus does not prove
a runtime branch was exercised.

### Documentation assurance

Build the solution first, then check every Markdown file Git lists (tracked, plus untracked and not ignored),
generated help block, exact-case local link and anchor, reachability of every `docs/` page from
`docs/README.md`, contract declaration, and the contract tokens in the test
inventories. The local gate also runs two byte-idle writes and proves neither changed any listed file relative to its
starting state, so unrelated working-tree edits are preserved:

```text
node eng/ci/policy/documentation.mjs
```

Maintainers use `ClaimCore.Docs write` only to refresh registered generated bodies. A source change
made by either write is a failed local preflight until reviewed and committed; CI additionally runs this on a clean
checkout. Contract IDs remain in their registered owner documents, and the tests that exercise a contract carry its
`[CC-…]` token in their names.

### Published acceptance

The CLI acceptance harness builds fresh CLI, Web, and Database publish trees with their license, .NET SBOM, third-party
notices, and a manifest each (`node eng/ci/publish/main.mjs build`). It then runs the exact registered CLI-v4 process
tests (`tests/inventory/ClaimCore.AcceptanceTests.txt`) against an isolated primary/witness pair and synthetic Keycloak
over the published HTTPS service, including confidential automation and public-client PKCE. It never gives the CLI a
database credential:

```text
bash eng/Run-PublishedCliAcceptance.sh
```

Web asset manifest format 2 binds source and generated-contract inventories with unambiguous JSON
path/hash records in ordinal order. Only the root `claimcore-assets.manifest.json` excludes itself;
nested files with that name remain inventoried. Regenerate retained assets with `npm --prefix web run
assets:produce` before publishing after this format change. Publish verification also refuses an empty
file inventory. These hashes establish identity against the recorded inputs, not producer authentication
or review quality.

A manifest lists every regular file of a published tree with its length and SHA-256, plus a digest over those
records. Every consumer verifies the tree it received before and after use (`node eng/ci/publish/main.mjs verify
<root> [cli|database|web]...`), so the bytes that were published once are the bytes that were exercised.

For a local three-engine Web lifecycle with the same measured Web-branch requirement as CI, first run the unit, web
and postgres suites through `suite.mjs`, then:

```sh
bash eng/Run-LocalBrowserCoverage.sh
```

The wrapper takes the .NET coverage reports from `artifacts/test-results`, locks and rebuilds the Web asset producer,
creates fresh Web and Database publish trees with SBOMs and manifests, verifies those manifests after each engine,
and runs Chromium, Firefox, and WebKit separately under Coverlet. Each engine checks its actual sanitized test
identities against `tests/inventory/browser.txt` and must measure `ClaimCore.Web` branches. It then merges the three
browser inputs with the .NET suites' inputs and enforces the same coverage floors as CI. The harness rejects a Vite
server, uses a disposable synthetic OIDC issuer and separate primary and witness PostgreSQL clusters, and keeps
private diagnostics out of retained sanitized results. Same-machine containers do not prove independent-host
survival; GitHub artifact transfer and other operating-system runners remain separate CI evidence.

### Coverage

Frontend unit tests enforce their configured coverage floors. The CI unit, web, integration, published CLI and browser
jobs emit independent .NET Cobertura inputs; the coverage job first resolves exactly the reports the registry and
the browser engines imply (one per measured process, no extras), then merges them with ReportGenerator and enforces
repository and Web-specific line and branch floors. The architecture manifest supplies the exact product
assembly set; tooling is excluded from aggregation, every product assembly must be measured, and the
same floors apply independently to the merged class-line/branch projection and the reported rates.
Empty or duplicate classes/lines and invalid counts refuse qualification. It cannot be replaced by rerunning one convenient test family
after the fact. The published CLI input must contain measured `ClaimCore.Cli` entry-process branches and is produced
while running the complete authenticated acceptance inventory; manifest verification runs before and
after instrumentation restores the original tree. Each published-browser input must contain measured `ClaimCore.Web` production branches; a successful
browser lifecycle with an empty instrumentation report is not coverage evidence.

```sh
node eng/ci/coverage-policy/merge.mjs artifacts/coverage-input artifacts/coverage-report/local-$(date -u +%Y%m%dT%H%M%SZ)
```

The output directory must not exist. `eng/ci/coverage-policy/policy.mjs` holds the unchanged merged line/branch floors of
60%/40% and each ClaimCore.Web package at 80%/70%; its negative controls cover exact pass boundaries, just-below
failures, missing or weak Web packages, nonfinite rates, and ignored DTD entity references.

Verification is job-local: each job verifies the reports it produced against the inventories before it uploads, and
the consumers of published bytes verify manifests. Use **Re-run all jobs**, not mixed-attempt partial reruns.
Generated reports belong under ignored `artifacts/` or current-attempt CI artifacts, not in source.

## Dependency updates

Dependency ownership is ecosystem-specific:

| Dependency class      | Version owner                          | Locked graph or immutable identity                    |
| --------------------- | -------------------------------------- | ----------------------------------------------------- |
| .NET SDK              | `global.json`                          | Exact SDK with roll-forward disabled.                 |
| NuGet packages        | `Directory.Packages.props`             | Per-project `packages.lock.json` files.               |
| .NET repository tools | `.config/dotnet-tools.json`            | The tool manifest itself.                             |
| Node.js and npm       | `.node-version` and the `package.json` engines | Exact engine and package-manager declarations. |
| Frontend packages     | `web/package.json`                     | `web/package-lock.json`.                              |
| Engineering packages  | `eng/package.json`                     | `eng/package-lock.json`.                              |
| Python tooling        | `pyproject.toml`                       | `uv.lock`.                                            |
| Downloaded tools      | `config/tools.json`                    | Version and per-platform SHA-256.                     |
| PostgreSQL container  | `db/postgresql-baseline.json`          | Official image tag and digest.                        |
| GitHub Actions        | Workflow `uses` entries                | Full action commit SHA with reviewed version comment. |

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

Security checking uses each ecosystem's own tool, all required: NuGet Audit runs on every restore and its
vulnerability warnings are errors, the frontend audit policy and `npm audit signatures` run for `web`,
`npm audit` and signatures run for `eng`, `uv audit` for the Python graph, and the frontend license
check for production dependencies. The frontend policy requires a clean production audit and
checks the full graph against the one remaining exact, development-only advisory with no patched version:
[`braces`](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm). Its expiry is owned by the
[frontend audit policy](../eng/ci/policy/npm-audit.mjs); a new advisory, production exposure, malformed audit, or stale exception fails.
This vulnerable build-tool dependency remains a reviewed risk, not a security fix. Dependabot opens one grouped, 7-day-cooldown
version-update pull request per ecosystem each week (npm for `web` and `eng`, NuGet, the .NET SDK, uv, GitHub Actions including the
composite action, and the Compose image). The version cooldown does not delay security updates. An available update does not block an unrelated PR, and a vulnerability
finding is never a reason for a blanket gate waiver.

See [Browser presentation](web.md#browser-presentation) for user-visible locale behavior and the
[frontend source](../web/README.md) for catalog commands. Qualify language changes against exact
value display, preserved in-progress state, accessibility, and the reviewed 256 KiB aggregate gzip
JavaScript budget.

## Reporting results

List commands actually executed with their outcomes. Separately identify conclusions drawn from
source or configuration inspection. If Docker, a browser engine, or another prerequisite was
unavailable, state that explicitly; never imply the corresponding gate ran.
