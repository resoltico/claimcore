# Contributing to the implementation

Use this guide for a source checkout, an implementation change or a verification report. The
[case-work guide](case-work.md) owns user-facing meaning; [operating](operating.md) owns deployment and
custody procedures. See the [glossary](glossary.md) for terminology.
Start with [verification](#verification) to choose checks for editing, PR readiness or growth measurements.

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

[package.json](../web/package.json) and its lockfile own the frontend dependency graph, including overrides.
The [import-boundary plugin](../web/scripts/import-boundary-lint.plugin.mjs) loads the configured rule exports.
Run dependency audits and import-boundary checks when changing that tooling.

Use the declared versions before restoring. The frontend can build without executable .NET or Python;
ordinary .NET builds run neither npm nor generation. Framework-dependent apphost publication is qualified;
retain the whole publish directory and reviewed [notices](../eng/licenses/third-party-notices.json).
Self-contained, trimmed and single-file publication need separate qualification.

## Project boundaries

| Project | Responsibility |
| --- | --- |
| Json | Strict JSON values, writers, codecs and schemas |
| Domain | JSON-free values and identifiers, typed acts, accepted state, business refusals, authority policy and decisions |
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

A vocabulary uses the module named for its value type, compiled as `<Type>Module`. `CaseAct.slot`
pairs typed inject/project functions; `AuthorityAct.map` and `AuthorityAct.traverse` preserve typed grant
payloads. Wire and durable codecs own their member names and vocabularies.
[Application outcomes](../src/ClaimCore.Application.Contracts/Outcome.fs) own typed use-case results;
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
group, searchability and amount/currency relationship for current interfaces. Authored group shapes and
permanent record inventories remain explicit at their owning boundaries.
`CaseAct.slot` pairs typed injection with projection; authority mapping/traversal preserves grant payloads.
Protocol owns endpoint and
interaction declarations; request codecs own wire paths, alternatives and vocabulary identity. Semantic
annotations attach meaning by named definition/member. Durable codecs keep their own literal spellings.

Private Registration, Decision and Payment records hold typed scalar values and complete Money values.
Their boundary adapters check exact membership and scalar kinds. Registration construction also checks
incident/notification chronology. PaymentProgress retains the complete Payment with its Decision.
Field collections are transport and presentation projections; business accessors return their scalar types.

A business-field change may require typed record, boundary-adapter and codec updates. Keep these
adaptations explicit: adding current metadata alone must not change an existing permanent grammar.

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
`IWriteTx` adds the head, fresh payload-nonce provider, append, projection and fence primitives. The PostgreSQL adapter constructs
it after acquiring the ledger head lock.
Accepted authority operations may contain up to 50 contiguous effects. Each effect has an independently
generated payload nonce; all effects and projections commit together. Operation lookup validates the
complete group, and receipts/checkpoints use its final effect position. Payload erasure preserves
accepted metadata but can make exact replay unavailable.

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
`IReadTx.NonAcceptance` supplies the retained control node, typed previous hash and fence instant from
one snapshot. Application validates node identity/hash and projection agreement before status or write guards
use it. Passive recovery uses `IStore.Read`; a derived fence alone cannot establish a permanent promise.

Permanent decoding does not reapply current admission. [baseline.sql](../db/baseline.sql) is the
checksum-bound schema source; its SHA-256 installation identity derives from the exact embedded bytes.
Baseline edits require a rebuild and a fresh installation. The runtime refuses unsupported installation
identities without modifying them.

The baseline also owns the pinned `pg_trgm` extension declaration. PostgreSQL must have that module
available for fresh initialization and full restore. Its GIN indexes support the existing literal
`LIKE` predicates without changing search normalization, query admission or runtime DDL privileges.

The case projection contains identity/revision/lifecycle, a bounded nullable fact snapshot,
status/progress, fact last-change time, normalized fact search, notification date and exact
claimed/payable amounts/currencies. Case work and reminder entities have independent bounded records
and revisions. Current responsibility, summary search and minimum Pending due date have transactional
read projections. Live snapshots store
exact [CaseRecord.encodeSnapshot](../src/ClaimCore.RecordFormat/CaseRecord.fs) bytes; erasure stores NULL.
Reads decode the permanent record and check identity, revision, lifecycle and indexed facts against the
returned columns and retained OPEN identity. CaseId is stored explicitly and equals the creation operation
UUID. Decode or agreement failures are integrity faults. Verification folds the ledger,
compares the projection snapshot bytes exactly and checks status, progress, last-change time and search
text, notification date and every amount/currency column against the folded facts. Cross-entity verification independently checks work roots, revisions, compound effects, reminder identities, responsibility and derived follow-up against causal history. Case progress and business-value invariants belong to Domain
and permanent decoding; SQL retains durable token, identity, revision, erasure and byte-size checks.

## Generated contracts

After a declaration, codec or message change:

```sh
dotnet run --project eng/ClaimCore.ProtocolGenerator --configuration Release --no-restore -- --output contracts
dotnet run --project eng/ClaimCore.ProtocolGenerator --configuration Release --no-restore -- --check
```

The [generator](../eng/ClaimCore.ProtocolGenerator/Program.fs) owns its exact inventory under
[contracts](../contracts/), [the CLI reference](cli.md) and [browser chrome metadata](../web/src/generated/chrome-messages.ts), its bootstrap text and the
explicit test catalog loaders in [web/src/generated](../web/src/generated/).
Check mode refuses missing, stale or extra outputs and does not repair them. Review generated diffs with their
producer. Custom output directories contain only contract artifacts. Core message metadata is locale-neutral;
production presentation loads and admits one complete core/chrome language pair before activation.
The generated eager catalog loaders are for tests and do not belong in the production import graph.
Dynamic JavaScript uses native imports; Vite retains initial HTML preloads and async CSS dependencies.
Published failure/recovery journeys exercise missing chunks, document replacement and offline draft
preservation in each supported browser engine. Build or schema checks alone do not cover these paths.

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
composite `act` vocabulary. Message ownership follows the producing layer; identical wording or
translations do not establish shared ownership.

Catalogs share one Template grammar. Structural checks cover language parity, producer arguments and every
template before publication. Handwritten
[template vectors](../tests/fixtures/message-template-vectors.json), wire examples, correction/transition
matrices and CLI frames provide independent expectations. Hand-authored durable cases in
[sources.json](../tests/fixtures/oracle/sources.json) define inputs and acceptance expectations; the
[Python oracle](../tests/fixtures/oracle/generate_vectors.py) derives their bytes and hashes without calling
the implementation. Keep those source cases independent of production codecs and validators.

## Core presentation and copy

[Descriptor](../src/ClaimCore.Protocol/Presentation/Descriptor.fs) owns typed business presentation:
case headlines, attention, recommendations, change summaries and actor names come from the core.
CLI and web adapt inputs and render these values; they must not rank actions or recreate business rules.
Case and reminder view predicates, rows and counts use the same authorized read snapshot. Exercise
both the memory contract and PostgreSQL implementation when changing a view.

Catalog field and input metadata distinguish labels, short GUI hints and precise rules. Rules retain
wire syntax for CLI help, documentation and information tips. GUI diagnostics use the generated GUI
message map rather than displaying CLI syntax. Consequence and completion identities are core metadata;
renderers must not infer them from effect names.

The machine-readable [glossary](../src/ClaimCore.Protocol/Messages/Localization/glossary.json) owns the
EN/LV/AR seed terms. [MessageRegisters](../eng/ClaimCore.ProtocolGenerator/MessageRegisters.fs) checks
registers, banned GUI tokens and glossary alignment during generation and `--check`. Add a negative
control when changing these checks. Latvian and Arabic linguistic changes need native-speaker review;
record outstanding review in the PR until a reviewer has supplied it.

## Theme and layout

[theme/tokens.json](../web/src/theme/tokens.json) owns the DTCG color tokens and OKLCH ramp configuration.
Run `npm --prefix web run theme` to regenerate [tokens.css](../web/src/theme/tokens.css), then
`npm --prefix web run theme -- --check` to check it. Keep color literals in the token source and its
generated stylesheet. Components use semantic tokens, not primitive palette references. Green and blue
each support light and dark modes, system selection, stronger contrast and forced colors.

`npm --prefix web run lint` includes [design lint](../web/scripts/design-lint.mjs): no stray color
literals, component primitive references, viewport queries outside `ui/layout.css`, or pixel font sizes.
Use the shared `ui/` controls and layout primitives; [breakpoints](../web/src/theme/breakpoints.ts) and
[layout.css](../web/src/ui/layout.css) own responsive boundaries. Keep CSP-compatible control behavior
qualified in Chromium, Firefox and WebKit.

The [contrast test](../web/tests/unit/theme/contrast.test.ts) checks all four accent/mode combinations
and includes a negative control against the retired palette. Run it with
`npm --prefix web run test:unit -- tests/unit/theme/contrast.test.ts`. Passing that test does not replace
keyboard, axe, forced-colors or browser qualification. Preferences belong only in SettingsPanel,
reachable through `/settings` and the sign-in footer dialog; keep bootstrap and hydrated theme choices
consistent without relaxing the strict CSP.

## Browser boundaries and lint

[oxlint.config.ts](../web/oxlint.config.ts) composes the browser policy with
[typed presentation rules](../web/typed-presentation-lint.config.ts) and
[transport boundary rules](../web/browser-transport-lint.config.ts). Maintained import rules separate
presentation from request coordination and reject computed imports there. The strict composite
[protocol project](../web/tsconfig.protocol.json) uses ES2024, no ambient package types and a closed source
manifest. Its explicit JSON inputs are the generated contract artifacts; full language catalogs load through
presentation. Portable encoding/clone declarations support the byte boundary without DOM or Node types. Pure DTOs live under protocol.
React, DOM, Node and outside source imports must fail that project. Browser and test projects still check
actual platform declarations. The app-only build references the strict protocol project, whose declarations
are emitted only into the ignored Node cache; whole-solution typecheck includes test and e2e sources.
Browser unit tests mirror `web/src` under `web/tests/unit`; cross-module scenarios live under
`web/tests/flows`, with fixtures and setup under `web/tests/support`. `npm run lint` rejects unit paths
without a matching source module. Categories, explicit rules and overrides together define the effective
policy; review the complete `oxlint --print-config` output when changing it.

[ReadAdmission](../web/src/api/readAdmission.ts) bounds complete read responses per document. Queued
obsolete reads never dispatch. Once handed off, [service](../web/src/api/service.ts) drains a read under
its own 20-second response deadline while the obsolete consumer discards its outcome. The caller has a
separate 20-second deadline that includes queue time; draining can continue after that caller deadline.
Cancellation does not establish that the server released capacity. Writes keep their cancellation and
uncertainty rules. This client admission state is local to one document.

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
| Performance investigation | Select the workload and evidence needed; see [Growth](#growth). |

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
Published-process tests also require current Release application outputs, even when their test assembly
is Debug. Build the affected application projects in Release before a direct focused invocation;
`eng/verify.sh` prepares these inputs for its stages. Publication runs before owned database startup,
so a missing Release input fails before that setup cost.
Published Web tests also need current frontend assets from `npm --prefix web ci` followed by
`npm --prefix web run build`; the acceptance stage prepares those assets.
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
| acceptance | Published service/CLI, deployment configuration and real browsers |
| growth | Separate synthetic contention and verifier qualification |
| growth-compare REF | Three alternating same-host pairs against a committed reference, with relative budgets |

Pure tests run in Debug without optimization for visible compiled negative controls; production is built
in Release. Shared store-contract scenarios run against both the memory double and PostgreSQL. Native
projects use automatic nonempty discovery and explicit stage assignment. Focused runs, smoke selections,
skipped or pending cases, and test retries do not replace the required stage scope. The gate defines the
PostgreSQL/growth split; CI assigns complete browser engines to separate jobs.

Acceptance uses [its own run settings](../eng/expecto.acceptance.runsettings) with one worker so
container startup and teardown cannot overlap browser authentication in another fixture. These
fixtures share host networking. Keep these settings when running focused acceptance cases; unit and
store tests retain their separate concurrency settings.

Default acceptance runs complete Chromium, Firefox and WebKit suites with zero retries. All three complete
engine jobs are required for the CI matrix; a selected engine alone does not qualify the full matrix. Smoke
mode explicitly reports incomplete qualification. Native report directories under
`artifacts/verification/` retain TRX and browser summaries. Automatic pruning retains the ten newest
explicitly successful completed native runs; failed, active and historical runs without a known outcome
are preserved. Review retained failures before manually removing their exact owned directories.
No later passing check erases a failed run. Cleanup must preserve original failures and target owned resources.

Failed publication retains bounded stdout/stderr in the printed private fixture directory. Generated
published binaries are removed separately; diagnostic files and other unique failure inputs remain.
These directories can contain credentials and are not public report attachments. Review them privately,
then remove only the owned fixture after its useful evidence is secured. Failure retention can grow disk
use; it does not authorize deleting active fixtures, shared caches or unrelated containers.

Fresh-initialization PostgreSQL controls must reset all objects owned by their disposable fixture.
The baseline installs database-wide `pg_trgm` as well as `claimcore`; dropping only the schema leaves
the extension behind. Use the existing `DatabaseScenarios.resetForInitialization` helper for owned
fresh-initialization scenarios. Schema-only refusal and restore controls have different prerequisites;
do not broaden their cleanup or weaken the initializer's strict duplicate-object refusal.

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
is writes `10000:1`, `100000:1`, `100000:16`, the 1,000-case cardinality comparison, and read profiles
at 100k cases. Six common reads cover search, filtered cases, notification order, summary, global
authority history and actor-filtered authority history. Reminder-feature measurements comprise seven
profiles: derived follow-up/summary, pending/completed queues, bounded current case, financial isolation
and global work overview. Plain-sight read profiles also measure `case.views`, `reminder.views` and the
bounded typed `case.timeline`, including high-child-fanout change decoding and actor enrichment. New
keys without a reference equivalent remain not applicable for relative ratios; their actual candidate
measurements are still retained. The [workload plan](../tests/ClaimCore.IntegrationTests/GrowthWorkloadPlan.fs) owns the
exact set. Both inputs execute their selected workloads. Only shared workload keys yield ratios; unmatched profiles
are reported as not applicable.

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

`comparison-progress.json` is an atomic observation of preparation and alternating child runs. It records
source and harness identities, completed side count and the active owned checkout/report paths; it never
reports a partial pass. `comparison.json` remains the final verdict, including cleanup failures. Current
harnesses publish `artifacts/verification/growth/*/read-progress.json` in the checkout with seed/measurement
phase, counts and the last completed read report. Each completed read report is published immediately;
`run.json` bookkeeping can lag until the population fixture has been disposed. Incomplete progress is not
qualification evidence and does not permit skipping outstanding workloads.

Read reports include measured latency samples in observation order, excluding warmups, with the percentile
index rule recorded beside them. Comparison reports add informational per-pair ratios, run spread and
available sample evidence for latency percentiles. Missing reference samples are reported rather than
reconstructed. These observations do not change sampling, budgets or the median comparison rule. Retain
the source patch, harness patches, build logs and workload fingerprints together when reproducing a
result; a source hash alone does not identify the assembly loaded by a separate diagnostic.

A reference without workload fingerprints requires a recorded manual review of act cycles, populations,
authored values and read queries. Set `CLAIMCORE_GROWTH_EQUIVALENCE_FILE` to a JSON review with exact
`reference` and `candidate` commit IDs, `candidatePatchSha256` when working changes are present,
a `workloads` array of reviewed keys (for example
`write/10000/1`, `write/100000/1`, `write/100000/16`, `cardinality/1000`, `read/search/100000`,
`read/filtered/100000`), and a `review` explaining the evidence. Missing or mismatched reviews refuse
before building. This is explicit manual equivalence evidence, not an inferred fingerprint match.
Single-profile diagnostics also require review of `cardinality/1` and `cardinality/100`.

The comparison labels its disposable test containers and, when needed, adapts reference harness selection.
Every adaptation is retained as a patch in the report. Product code stays at the recorded commit.
The comparison removes its worktrees, build outputs and labelled containers on success or failure,
while preserving reports and failure output.

The full `bash eng/verify.sh growth` observation includes nine write-contention profiles,
three cardinality comparisons and search/filtered, notification-order, summary and explicit-work
follow-up/summary workloads at 1k, 10k and 100k cases. At the
100k case population, global and actor-filtered authority-history reads also run after 100k additional
grant/revoke events. Those events are seeded after the existing case-query measurements, preserving
their workload inputs and fingerprints. The authority workloads have their own declared fingerprints.
After those common measurements, one open case per thousand receives explicit responsibility and a past
Pending reminder based on the database business date. Mine/Overdue/FOLLOW_UP pages and filtered summaries check
independently named cases, account identity, dates and counts before recording their separate latency
samples. High-fanout workloads then populate 100/1,000/10,000 Pending and completed reminders on one
case, independently checking queue continuations, bounded current views, unique financial totals and
global attention. The `reminder-derived-follow-up` and `reminder-derived-work-summary` profiles use the
independent Work/Reminder seed. Only profiles with matching workload keys and fingerprints yield ratios;
common read/write/cardinality comparisons retain their budgets.
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

[Releases](https://github.com/resoltico/claimcore/releases) distribute source archives. Version, date,
tag assignment and publication require owner authorization. Prepare the final version, dated changelog
section, dependencies and documentation before publishing that source commit.

1. Open a pull request with the finalized release changes. After its required `Gate` passes, use the
   authorized PR merge path to update protected `main`. Wait for the resulting main commit's complete
   push-triggered CI run to succeed.
2. Create an annotated `vVERSION` tag for that exact commit and push the tag. A tag push does not start
   another CI run.
3. Dispatch [Publish source release](../.github/workflows/release.yml) from `main`, supplying the tag and
   its full commit SHA.

The workflow verifies the annotated tag, expected SHA, membership in `main`, matching version and a
successful main-branch push CI run for that exact SHA. It publishes the tagged commit's dated
[CHANGELOG.md](../CHANGELOG.md) section. A branch result for another commit, a local gate or a manual CI
run does not replace the required push result. Inspect the workflow and resulting release before
reporting publication complete.
