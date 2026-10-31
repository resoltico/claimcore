# Contributing to the implementation

Use this guide for a source checkout, an implementation change or a verification report. The
[case-work guide](case-work.md) owns user-facing meaning; [operating](operating.md) owns deployment and
custody procedures. See the [glossary](glossary.md) for terminology.
Start with [verification](#verification) to choose checks for editing, PR readiness or optional experiments.

## First checkout

Install the SDK declared by [global.json](../global.json), Node from [.node-version](../.node-version), and
npm from [web/package.json](../web/package.json). Bash and Python 3 are required; Docker is needed for
integration, workflows and acceptance. Workflow checks also use ShellCheck. Unix sink tests use sh, mkfifo,
dd, stty, script, and Linux prlimit where available. The [CI matrix](../.github/workflows/ci.yml) declares
platform qualification.

From the repository root:

```sh
dotnet tool restore
dotnet restore ClaimCore.slnx --locked-mode
npm --prefix web ci
```

The transitive matcher and traversal pins in [package.json](../web/package.json) address the
[minimatch](https://github.com/advisories/GHSA-g3hv-75wg-m249) and
[glob](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4) advisories. The
[import-boundary plugin](../web/scripts/import-boundary-lint.plugin.mjs) loads the configured upstream
rule exports; the package index also imports an unsupported API from an unused rule. The audit and
configured boundary rules remain required.

Use the declared versions before restoring. The frontend can build without executable .NET or Python;
ordinary .NET builds run neither npm nor generation. Framework-dependent apphost publication is qualified;
retain the whole publish directory and reviewed [notices](../eng/licenses/third-party-notices.json).
Self-contained, trimmed and single-file publication need separate qualification.

## Project boundaries

| Project | Responsibility |
| --- | --- |
| Json | Strict JSON values, writers, codecs and schemas |
| Domain | JSON-free values and identifiers, typed acts, accepted state, outcomes, authority policy and decisions |
| Application.Contracts | Use-case requests, replies, advice, paging and faults over Domain |
| Protocol | Wire codecs, endpoints, catalog, renderer-neutral messages and presentation policy, shared command-line contracts |
| RecordFormat | Durable grammars, literal names, permanent byte limits and cursor encoding |
| Application | ServiceContext-based orchestration, named port inputs, replay, fencing and verification |
| Postgres | Transaction primitives, projections and installation administration |
| HostSecurity | Private files, HTTPS origins and TLS purposes |
| Web | Authentication, request gates, runtime leases and route serving |
| CLI | HTTPS, command grammar, typed replies and frames |
| Database | Owner administration |

Domain has no project dependencies. Protocol builds codecs over Domain and Application.Contracts using Json. RecordFormat uses Domain and Json; Application uses Domain, Application.Contracts and RecordFormat. Protocol owns combined act/scope vocabularies, endpoint names and catalog read declarations. Postgres implements Application ports using Domain,
RecordFormat and Json. Application and Postgres have no Protocol reference. Web and Database use the
Application Service and port surface; the published CLI contains Domain, Application.Contracts, Protocol, Json and HostSecurity,
with Application and Postgres excluded. The
[project-edge policy](../tests/ClaimCore.Tests/Architecture/ProjectReferences.fs) owns the exact direct edges.

[Compiled architecture controls](../tests/ClaimCore.Tests/Architecture/) run in Debug and cover Domain
isolation, durable vocabulary ownership, service-model independence, host usage and vocabulary naming.
[Domain ownership](../tests/ClaimCore.Tests/Architecture/DomainOwnershipTests.fs) checks actual product
consumers; declaration controls check unused declarations. Independently owned types may have identical shapes, and independent message keys may share translations. Their implementations own the
compiler and negative-control details. Explicit exceptions need a concrete consumer or ownership reason.
Product internals may be exposed to tests only.

A vocabulary uses the module named for its value type, compiled as `<Type>Module`. `CaseAct.slot` and `AuthorityAct.slot`
pair typed inject/project functions; wire and durable codecs select their own member names without
reconstructing a kind/payload pairing. [Domain outcome models](../src/ClaimCore.Domain/Outcomes/) own typed outcomes;
[ServiceContext](../src/ClaimCore.Application/ServiceContext.fs) receives product identity and dependencies
from host composition. Multi-argument transaction and store members take named records.

Change value rules in their [owning value module](#value-module), and record/operation structure in the
[typed declaration](../src/ClaimCore.Domain/CaseFields.fs). The case projection stores exact durable snapshot bytes.
Web binds endpoint codecs to the public [Service](../src/ClaimCore.Application/Service.fs) through
[Route.bind](../src/ClaimCore.Web/Routes.fs).
Closed code sets use the shared [Vocabulary](../src/ClaimCore.Domain/Vocabulary/Vocabulary.fs) mechanism.

## Source vocabulary

### Value module

[Values.fs](../src/ClaimCore.Domain/Values.fs) owns permanent parsing and formatting for calendar dates,
countries, party names, reasons and references. [MoneyValues.fs](../src/ClaimCore.Domain/MoneyValues.fs)
owns amount and currency values and assembles them as `Money` for named business rules. Each value module
owns its current admission; date and text admission apply current business-date and text rules.
[Identifiers.fs](../src/ClaimCore.Domain/Identifiers.fs) owns nonempty operation, actor and case IDs and
bounded revisions and sequences. Authored requests and reply correlation fields retain raw values;
admitted commands and stored identity/position fields use typed values.

### Declaration

[BusinessField.describe](../src/ClaimCore.Domain/CaseFields.fs) exhaustively supplies each field's kind,
group, searchability and amount/currency relationship. Field/group inventories and authored input targets
derive from that description.
`CaseAct.slot` and `AuthorityAct.slot` pair typed injection with projection. Protocol owns endpoint and
interaction declarations; request codecs own wire paths, alternatives and vocabulary identity. Semantic
annotations attach meaning by named definition/member. Durable codecs keep their own literal spellings.

Private Registration, Decision and Payment records hold typed scalar values and complete Money values.
Their boundary adapters check exact membership and scalar kinds. Registration construction also checks
incident/notification chronology. PaymentProgress retains the complete Payment with its Decision.
Field collections are transport and presentation projections; business accessors return their scalar types.

[StateValidation](../src/ClaimCore.Domain/StateValidation.fs) admits newly asserted values generically.
Named accessors retain date-order, payable and payment-acknowledgement rules. Adding a declared field
does not require parallel authored/admitted record members or per-field codec and admission branches.

[StateValidation](../src/ClaimCore.Domain/StateValidation.fs) owns admission of newly asserted dates and
names and progress invariants. Corrections retain proposed groups until acknowledgement and admission
finish, so incomplete combinations can still report independent input issues. `InputTarget.order` owns
issue order, applied after the transition checks.

### Applicable

[Applicable](../src/ClaimCore.Domain/Transitions.fs) carries both the permitted state and the selected
act's payload. Transition execution matches that evidence exhaustively; eligibility evaluates unit-payload
act probes. Actor authorization and admission of supplied values remain separate checks.

### Verdict

Application's `Verdict` is Proceed, Replay, Refuse or Fail. Only Proceed permits a transaction to commit;
the other verdicts discard staged changes. Public outcomes adapt the result to each operation's contract.
Domain refusals distinguish case, authority and access causes while codecs retain their flat public codes.

### Route

A Web-owned endpoint binding with input and output closures. Binding captures the handler with matching
request and response types, so dispatch can invoke the route without recovering erased values.

### Read and write transactions

[IReadTx](../src/ClaimCore.Application/LedgerStorePort.fs) supplies primitives over one consistent snapshot.
`IWriteTx` adds the head, nonce, append, projection and fence primitives. The PostgreSQL adapter constructs
it after acquiring the ledger head lock.

### Request gate

Web checks the connection, origin, credentials, media type, request size and antiforgery token before
Application handles a request. A rejected gate produces a transport failure.

### Runtime leases

The Web runtime grants a lease to each admitted operation, including session-storage calls. Shutdown
stops new leases and waits for current holders to finish provider cleanup before disposing storage.

## Transactions and durable formats

[RecordFormat](../src/ClaimCore.RecordFormat/) owns durable grammar and
[Versions.fs](../src/ClaimCore.RecordFormat/Versions.fs) owns format numbers. Permanent scalar grammars used
to decode retained records may only be relaxed under the same format number. Tightening requires a
separately authorized format change. Independent permanent-limit vectors in
[record-format-vectors.json](../tests/fixtures/record-format-vectors.json) protect retained-record decoding.
[DurableNames.fs](../src/ClaimCore.RecordFormat/DurableNames.fs) owns exhaustive literal spellings;
[RecordLimits.fs](../src/ClaimCore.RecordFormat/RecordLimits.fs) owns the 65,536-byte request-record ceiling.
Wire spelling and transport limits do not govern retained records. Oversized encoding is an internal fault.
[Generated inventory properties](../tests/ClaimCore.Tests/RecordFormat/DurableRoundTripProperties.fs) visit
every case kind, snapshot status, authority kind, role and scope on deterministic trials, with
varied permanent values and independently authored spellings; fixed byte vectors remain independent.

[IStore](../src/ClaimCore.Application/LedgerStorePort.fs) exposes read/write callbacks over primitives.
The PostgreSQL adapter creates a write transaction capability after taking the head lock. Reads use one
repeatable-read snapshot; writes read decision state under the lock and commit only on Proceed. Receipt
construction uses that same state, without a later read changing disclosure. Runtime leases remain held
through actual provider cleanup.

Permanent decoding does not reapply current admission. [baseline.sql](../db/baseline.sql) is the
checksum-bound schema source; its SHA-256 installation identity derives from the exact embedded bytes.
Baseline edits require a rebuild and a fresh installation. The runtime refuses unsupported installation
identities without modifying them.

The case projection contains identity, revision, lifecycle, a nullable snapshot, status, progress,
last-change time, normalized search text, notification date and exact claimed/payable amount/currency
pairs. Live snapshots store
exact [CaseRecord.encodeSnapshot](../src/ClaimCore.RecordFormat/CaseRecord.fs) bytes; erasure stores NULL.
Reads decode the permanent record and check its reference, revision and lifecycle against the columns and
its derived case ID. Decode or agreement failures are integrity faults. Verification folds the ledger,
compares the projection snapshot bytes exactly and checks status, progress, last-change time and search
text, notification date and every amount/currency column against the folded state. Case progress and business-value invariants belong to Domain
and permanent decoding; SQL retains durable token, identity, revision, erasure and byte-size checks.

## Generated contracts

After a declaration, codec or message change:

```sh
dotnet run --project eng/ClaimCore.ProtocolGenerator --configuration Release --no-restore -- --output contracts
dotnet run --project eng/ClaimCore.ProtocolGenerator --configuration Release --no-restore -- --check
```

The [generator](../eng/ClaimCore.ProtocolGenerator/Program.fs) owns its exact inventory under
[contracts](../contracts/), [the CLI reference](cli.md) and [browser chrome messages](../web/src/generated/chrome-messages.ts).
Check mode refuses missing, stale or extra outputs and does not repair them. Review generated diffs with their
producer. Custom output directories contain only contract artifacts.

Protocol publishes semantic operation/input metadata; CLI owns verbs, flags, operands, switches and
synopses. The generator renders the command reference through CLI grammar. The public catalog has no
`synopsis` field. Core owns domain wording and scalar guidance; renderer catalogs contain interaction text.
Request codecs own input structure. [WireMembers](../src/ClaimCore.Protocol/Wire/WireMembers.fs) supplies technical
member names; catalog annotations identify meaning by named schema definition/member. Finite codec
vocabularies supply the catalog's choice/group and constant vocabulary names; group annotations supply
correction advice identity. Generation rejects finite inputs without a vocabulary. Browser form state
keeps the selected catalog entry identity, and request constants derive from that entry. The schema walk
resolves references and nested discriminators, and validates complete, non-orphan annotations and tokens.

Qualified vocabulary keys are produced by `Vocabulary.messageKey`; the generated `Terms` map supports
`p.term(vocabulary, value)`. Registry checks allow equal tokens in different vocabularies and reserve one
composite `act` vocabulary. Identical renderer/core wording is refused in every locale.

Catalogs share one Template grammar. Structural checks cover language parity, producer arguments and every
template before publication. Handwritten
[template vectors](../tests/fixtures/message-template-vectors.json), wire examples, correction/transition
matrices and CLI frames provide independent expectations. Hand-authored durable cases in
[sources.json](../tests/fixtures/oracle/sources.json) define inputs and acceptance expectations; the
[Python oracle](../tests/fixtures/oracle/generate_vectors.py) derives their bytes and hashes without calling
the implementation. Keep those source cases independent of production codecs and validators.

## Browser boundaries and lint

[oxlint.config.ts](../web/oxlint.config.ts) composes the browser policy with
[typed presentation rules](../web/typed-presentation-lint.config.ts) and
[transport boundary rules](../web/browser-transport-lint.config.ts). Maintained import rules separate
presentation from request coordination and reject computed imports there. The strict composite
[protocol project](../web/tsconfig.protocol.json) uses ES2024, no ambient package types and a closed source
manifest. Its explicit JSON inputs include contract-owned locale data; portable encoding/clone
declarations support the byte boundary without enabling DOM or Node types. Pure DTOs live under protocol.
React, DOM, Node and outside source imports must fail that project. Browser and test projects still check
actual platform declarations. The app-only build references the strict protocol project, whose declarations
are emitted only into the ignored Node cache; whole-solution typecheck includes test and e2e sources.
Browser unit tests mirror `web/src` under `web/tests/unit`; cross-module scenarios live under
`web/tests/flows`, with fixtures and setup under `web/tests/support`. `npm run lint` rejects unit paths
without a matching source module. Categories, explicit rules and overrides together define the effective
policy; review the complete `oxlint --print-config` output when changing it.

Keep files and functions focused by responsibility. File length, parameter counts and change-locality
measurements are review guidance, not representation requirements. Correctness, dependency, compiler,
formatting and actionable lint controls remain enforced. Only a total exhaustive table may suppress
CyclomaticComplexity, with a concrete reason immediately before the suppression.

F# rules live in [eng/fsharplint.json](../eng/fsharplint.json); formatting policy
lives in [web/prettier.config.mjs](../web/prettier.config.mjs). The permitted total-table suppression needs
the concrete reason described above.

## Verification

Choose checks by the changed behavior and the claim being made:

| When | Scope |
| --- | --- |
| Editing | Focused tests, builds or named stages for affected behavior; use existing watch commands where helpful. |
| Broader checkpoint | `bash eng/verify.sh --fast` runs lint, native units and the full frontend stage. |
| Implementation PR ready for review | `bash eng/verify.sh` runs the ordinary full gate; CI qualifies the declared platform matrix. |
| Documentation-only change | Check affected links, commands and claims; run relevant documentation checks. Product or generated-output changes need their applicable checks. |
| Change probe or performance investigation | Select the experiment and checks that answer the concrete question; see [Change probes](#change-probes) and [Growth](#growth). |

There is no automatic full-gate requirement after each edit or commit. Explicit task
acceptance criteria still apply. Reuse results only while their relevant source, configuration, dependencies
and execution conditions remain applicable; rerun affected checks after later changes. Preserve failures
and their resolution rather than treating a later pass as evidence that the earlier run never failed.
Required clean or independent verification remains required. Local checks establish only their exercised
scope and platform; focused runs do not qualify an unrun complete stage or CI matrix.

After [first checkout](#first-checkout), examples for focused iteration are:

```sh
dotnet build tests/ClaimCore.Tests/ClaimCore.Tests.fsproj --configuration Debug --no-restore -p:Optimize=false
dotnet test --project tests/ClaimCore.Tests/ClaimCore.Tests.fsproj --configuration Debug --no-build --no-restore \
  --zero-tests-policy=strict --filter 'FullyQualifiedName~exact decimal totals' -- --settings=eng/expecto.runsettings
npm --prefix web run test:unit -- tests/unit/protocol/preparedRequest.test.ts
npm --prefix web run test:unit:watch -- tests/unit/protocol/preparedRequest.test.ts
```

Build current native inputs before using `--no-build`; choose the affected test suite or frontend file.
Frontend file selection runs every test in the selected files;
name filters can create skipped results that the required-test reporter rejects. A build alone proves
neither behavior nor integration. Use named stages when the whole affected suite is the useful unit.

For broader verification:

```sh
bash eng/verify.sh --fast
bash eng/verify.sh
```

The fast gate includes clean npm installation, audits, lint, types, tests and builds; it is a checkpoint,
not a required edit loop. The second command runs all default stages, excluding growth. Before the first
acceptance run, install NSS `certutil` (`libnss3-tools` on Ubuntu, `nss` on Homebrew). The configuration
acceptance checks inspect actual isolated CA trust. After changing the Playwright version, install the locked engines and system dependencies:

```sh
bash eng/verify.sh browsers
```

The `browsers` stage may require administrator access on Linux. Named stages share restore/build work,
execute in order and stop at failure:

| Stage | Scope |
| --- | --- |
| lint | Fantomas, FSharpLint and browser compiler boundaries |
| unit | Debug native properties, byte contracts and architecture controls |
| frontend | Audit, format, type-aware lint/types, browser units, Knip, licenses and build/notices |
| workflows | ShellCheck and pinned Actionlint |
| integration | PostgreSQL then Web suites |
| postgres / web | The corresponding isolated suite |
| browsers | Install the locked Playwright engines and required system dependencies |
| acceptance | Published service/CLI, deployment image and real browsers |
| growth | Separate synthetic contention and verifier qualification |
| growth-compare REF | Three alternating same-host pairs against a committed reference, with relative budgets |

Pure tests run in Debug without optimization for visible compiled negative controls; production is built
in Release. Shared store-contract scenarios run against both the memory double and PostgreSQL. Native
projects use automatic nonempty discovery and explicit stage assignment. Focused runs, smoke selections,
skipped or pending cases, and test retries do not replace the required stage scope. The gate defines the
PostgreSQL/growth split; CI assigns complete browser engines to separate jobs.

Default acceptance runs complete Chromium, Firefox and WebKit suites with zero retries. All three complete
engine jobs are required for the CI matrix; a selected engine alone does not qualify the full matrix. Smoke
mode explicitly reports incomplete qualification. Native report directories under
`artifacts/verification/` retain TRX and browser summaries; preserve relevant failures before report pruning.
No later passing check erases a failed run. Cleanup must preserve original failures and target owned resources.

Independent closed inventories live in [inventory.json](../tests/fixtures/inventory.json), authored by hand
rather than generated from product declarations. Input builders belong to
[Inputs.fs](../tests/ClaimCore.TestSupport/Inputs.fs) and
[inputs.ts](../web/tests/support/inputs.ts); expected outputs and rule witnesses remain independent.
The authored seed-corpus admission test identifies missing positive examples. Dependent properties still
fail when that corpus is invalid; do not turn those failures into skips.

## Test inputs and golden fixtures

Positive behavior examples are authored overrides on the inventory-driven input builders, including
wire seed groups. Keep the values under test literal. Invalid examples and permanent-format vectors
stay independently authored. The `goldens` list in
[inventory.json](../tests/fixtures/inventory.json) declares the wire request/response examples and
CLI frames. Review their diffs after an intentional local update:

```sh
CLAIMCORE_UPDATE_GOLDENS=1 bash eng/verify.sh unit
```

Update mode refuses when `CI` is set, including an empty value. It writes only declared fixture
basenames and always refuses the three permanent vector files. It supplies omitted positive fields
from the builders and prepares CLI frames from their authored arguments; it leaves invalid examples
unchanged. Run the ordinary gate afterwards. Updating goldens never resolves permanent-format failures.

## Change probes

Change probes are optional architecture experiments. Select a probe to answer a
concrete design question or meet explicit task acceptance criteria. Prefer evidence from requested product
changes when it answers the same question. Probes do not replace [product verification](#verification).

### Select the experiments

Choose only the representative change needed and state the question, scope and expected evidence before
implementing it. The observations below are diagnostic hypotheses, not architectural requirements.
Keep the change stable when comparing results, or disclose why the experiments are incomparable. A READ
reusing existing request/result types measures registration cost, not novel query design. Fewer edits do
not justify generic machinery, weaker types, shared production/oracle answers or removed safeguards.

| Probe | Change | Evidence question |
| --- | --- | --- |
| P-FIELD | Add a required registration text field. | Which typed-state and receiving-contract adaptations are necessary? A descriptor alone must not change existing durable members or bytes. |
| P-ACT | Add a payload-free case act. | Does current admission evolve explicitly while existing-format interpretation remains frozen? |
| P-ROLE | Add a role with an existing capability. | Are current permission decisions and permanent authority meaning kept separate? |
| P-READ | Add a read-only endpoint. | Do contracts, authorization, storage, clients and generated output form one exercised path? |
| P-MESSAGE | Add a refusal and its message. | Do the producer, diagnostic contract and supported locales agree without competing client business rules? |

Record authored and generated adaptations separately. Edit counts describe cost; they are not acceptance
limits and do not justify hidden property bags, weaker types or silently enlarged durable grammars.

### Run an experiment

Keep hypothetical features out of the delivered product. Use an isolated worktree when implementing a
probe, record its source revision and patch, and never push experimental branches. Exercise the element
through its relevant real path, including applicable refusal or boundary behavior. Regenerate affected
contracts; use real PostgreSQL when claiming store behavior. Compilation or a memory double cannot
establish database behavior.

Choose checks for the question. Run the complete fast gate when claiming full adaptation through its
scope, plus relevant integration checks for database claims. A focused proof needs no automatic full
fast-gate run, but cannot establish full adaptation. Investigate failures and fix in-scope product defects;
report other causes and unrun checks. Preserve useful evidence before cleaning up owned probe resources.

### Report outcomes and cost

Keep a short PR summary of the question, revision, patch, environment, commands, result and limits.
Distinguish locality, exercised behavior and remaining adaptation work. Include file counts or effort/time
measurements when relevant to the question; label estimates and unavailable measurements. Explain failures
by cause and distinguish necessary independent-oracle, composition or compatibility work from duplication.

### Reuse applicable evidence

A later revision alone does not require repeating an experiment. Reuse its original result when the
relevant producer/consumer paths, fixtures, generation, toolchain and execution conditions still support
the conclusion. Briefly explain applicability; retain the original revision and material failures without
relabeling old checks as new executions. Rerun affected proofs when their basis changes. Reuse cannot turn
an incomplete result into a complete pass or waive required product verification.

Existing Git, generation and verification commands suffice; no maintained script applies these probes.

## Growth

Growth measurements are separate from the ordinary verification gate. They are optional unless required
by task acceptance criteria or an agreed release plan; a release does not automatically require both
growth commands. Choose the smallest existing scope that answers the concrete performance question.
State the reference and workloads for a comparison. Preserve evidence from running work; a policy change
does not resolve an observed regression or invalidate an agreed acceptance criterion.

| Purpose | Command | Evidence |
| --- | --- | --- |
| Focused write observation | `CLAIMCORE_GROWTH_PROFILE=1000:1 bash eng/verify.sh growth` | One current-source write profile and three cardinality populations; no reads or reference comparison. |
| Focused comparison | `CLAIMCORE_GROWTH_PROFILE=10000:1 bash eng/verify.sh growth-compare REF` | Three alternating pairs for that write profile and cardinality controls; diagnostic only. |
| Full comparison | `bash eng/verify.sh growth-compare REF` | Three alternating pairs with relative budgets over shared workloads. |
| Full observation | `bash eng/verify.sh growth` | The complete current-source workload set; no reference ratios. |

Single-profile `growth` can exit zero while its report marks qualification incomplete: only the selected
observation passed. Diagnostic `growth-compare` returns 2 when its budgets pass, as described below.
Neither result qualifies unmeasured workloads. Choose a broader run when needed by the question or agreed
acceptance scope; retain workload identity and all comparison checks when claiming a comparison result.

Run `bash eng/verify.sh growth-compare REF` to compare against a commit. It captures the candidate's
HEAD and tracked working changes, retaining the source patch and its SHA-256. It refuses any nonignored
untracked files: stage intended new source files and move unrelated scratch files outside the checkout.
It refuses source changes during capture. Both source-stable inputs build in disposable
detached worktrees with locked restore. The comparison alternates
reference → candidate three times. Write and cardinality profiles use fresh PostgreSQL containers;
read profiles share the fresh database for their case population. The compared set
is writes `10000:1`, `100000:1`, `100000:16`, the 1,000-case cardinality comparison, and six read profiles
at 100k cases: search, filtered, notification order, summary, global authority history and actor-filtered
authority history. The [workload plan](../tests/ClaimCore.IntegrationTests/GrowthWorkloadPlan.fs) owns the
exact set. Both sides execute their selected workloads, including new candidate-only profiles; only shared
keys yield ratios, and other profiles are reported as not applicable.

For a targeted diagnostic or an injected-delay control, the existing `CLAIMCORE_GROWTH_PROFILE`
selector accepts `10000:1`, `100000:1` or `100000:16`. It still runs three alternating pairs and the
normal growth harness's three cardinality controls, without read workloads. The report states that
qualification is incomplete: exit 1 means a budget/check failed, and exit 2 means diagnostic budgets
passed but the required full set was not run. A diagnostic can never return qualification success.
Reduced runs do not establish a full comparison pass.

Comparison fails when a candidate median's write/read throughput is below 0.85×, or its p95, p99 or verification
time exceeds 1.30× the reference. Reports retain all six raw runs, medians, ratios and workload
fingerprints in JSON and Markdown under `artifacts/verification/growth-compare-*` (or beneath
`CLAIMCORE_TEST_REPORT_DIRECTORY`). These are same-host, same-session relative measurements, not an SLA.
Different workload fingerprints refuse comparison, naming the differing members.

References without workload fingerprints require a recorded manual review of act cycles, populations,
authored values and read queries. Set `CLAIMCORE_GROWTH_EQUIVALENCE_FILE` to a JSON review with exact
`reference` and `candidate` commit IDs, `candidatePatchSha256` when working changes are present,
a `workloads` array of reviewed keys (for example
`write/10000/1`, `write/100000/1`, `write/100000/16`, `cardinality/1000`, `read/search/100000`,
`read/filtered/100000`), and a `review` explaining the evidence. Missing or mismatched reviews refuse
before building. This is explicit manual equivalence evidence, not an inferred fingerprint match.
Single-profile diagnostics also require review of `cardinality/1` and `cardinality/100`.

Only the disposable test harnesses receive an ownership label; reference harnesses without comparison
selection also receive that selection. Every adaptation is retained as a patch in the report. Product code
stays at the recorded commit. The comparison removes its worktrees, build outputs and labelled
containers on success or failure, while preserving reports and failure output.

The full `bash eng/verify.sh growth` observation includes nine write-contention profiles,
three cardinality comparisons and search/filtered, notification-order and summary workloads at 1k,
10k and 100k cases. At the
100k case population, global and actor-filtered authority-history reads also run after 100k additional
grant/revoke events. Those events are seeded after the existing case-query measurements, preserving
their workload inputs and fingerprints. The authority workloads have their own declared fingerprints.
[GrowthQualificationTests](../tests/ClaimCore.IntegrationTests/GrowthQualificationTests.fs) owns sequencing
and completed-report checks; its `GrowthReadWorkloads` helper owns read populations and latency reports. Selecting one `CLAIMCORE_GROWTH_PROFILE` marks qualification
incomplete. Report actual counts, throughput, p50/p95/p99 latency and verifier memory/time. Measurements
establish neither an SLA nor constant total-system memory.

The gate bounds managed completed-report retention, not total caches, image or artifact use. Protect active
runs, measure resource use and reclaim only owned temporary resources. Locked NuGet/npm restore and audits
still run with caches; shared caches are not disposable task resources.

## Dependencies

[Directory.Packages.props](../Directory.Packages.props) pins direct .NET packages and selected production
transitives that track the runtime security train; reviewed lockfiles record the complete resolution. Node/npm and [package-lock.json](../web/package-lock.json) own the frontend graph.
Audit all dependencies, retain image digests and use locked restore/npm ci. Dependency changes must update
consumers, lockfiles, licensing evidence and qualification together.

Private-file qualification uses real objects on the declared native CI platforms, including Linux musl.
Native-language and assistive-technology review remain human work.

## Source releases

[Releases](https://github.com/resoltico/claimcore/releases) distribute source archives. The owner authorizes
release version/date/tag assignment and publication. A completed change or green gate does not grant that
authority. See [CHANGELOG.md](../CHANGELOG.md) and the [release workflow](../.github/workflows/release.yml).
The workflow requires main membership, the exact annotated tag/SHA, matching version and successful tag CI;
branch evidence does not substitute for those inputs.
