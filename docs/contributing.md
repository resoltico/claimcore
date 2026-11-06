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
Shared UI components import the styles their markup requires. Styles imported only by a lazy view are
not available on cold entry to another view. Check shared fact rendering through direct Recovery and
review routes as well as the case workspace; a prior visit can mask a missing stylesheet dependency.
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

[TimelineOverview](../src/ClaimCore.Application/TimelineOverview.fs) selects concise recorded events and
highlight references from validated historical effects after disclosure and conflict-range filtering.
Full differences remain authoritative. The generated `resolveTimelineHighlights` helper only resolves
those references; renderers must not choose other fields or reconstruct historical reminder actions.
Keep maximal retained-value replies within the existing 4 MiB cap, including 50-entry conflict pages.

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

Use three phases, and select the scope before starting a command:

1. **Edit and diagnose.** Run affected tests and the cheapest check that can reject the change. After a
   declaration change, regenerate/check contracts and compile the actual consumers and authored fixtures
   before starting containers. Inspect each command's exit status; a later successful command does not
   make an earlier failed command pass.
2. **Check a coherent checkpoint.** Use `bash eng/verify.sh --fast` when the source and consumers agree.
   For browser work, inspect one populated small-screen case and one RTL view with the full shared axe
   helper before expanding coverage. Batch stale selector/DTO/route/consent ports; do not discover each
   one by replaying a complete acceptance suite's already-passing prefix.
   After a qualification failure, stop full reruns. Diagnose the failed path, inspect related consumers
   for the same cause, and pass the affected focused checks before resuming qualification. Repeat a
   full run for diagnosis only when the failure requires its conditions; state that reason first.
3. **Qualify stable source.** Run the required full stages and platform matrix after relevant changes
   have stopped. Choose an execution owner: CI can supply the stages/platforms it actually exercises;
   use local full verification when required or when CI cannot supply that evidence. Avoid repeating the
   same qualification locally and in CI solely because both are available.

| Change or question | First useful check | Expand when |
| --- | --- | --- |
| Native behavior | Affected native test list with current compiled inputs | Interactions or a broader checkpoint justify the full stage |
| Wire/catalog declaration | Generator/check, compiler/type checks, authored corpus | Producers and consumers agree, before published integration |
| Browser behavior or stale test | Affected unit file or focused published diagnostic | Relevant behavior passes on the current source |
| Layout or copy | One representative populated frame; full axe and relevant bounds | A final visual set or another engine is needed for the claim |
| Documentation only | Links, command/behavior claims, existing documentation checks | Product or generated inputs also changed |
| Performance | An existing targeted workload; see [Growth](#growth) | Evidence or explicit acceptance requires the full comparison |

There is no automatic full-gate requirement after each edit or commit. Reuse passing evidence while its
relevant source, configuration, dependencies, platform and purpose still apply. After a change, rerun
what it can invalidate, then complete the final required scope once. Keep security, CSP, admission,
frozen-byte/ownership and hard resource limits intact. A failed check needs diagnosis; changing the
method or scope does not turn its previous failure into a pass.

Explicit task acceptance and required clean/independent checks still apply. Track additional requirements
(such as a full growth comparison or field retest) separately from the ordinary `Gate`. Focused runs,
smoke selections and partial matrices establish only their reported scope, never unrun qualification.

CI enforces the order: published browsers wait for frontend checks, and a full comparison waits for a
successful ordinary `Gate`. The workflow stage rejects regressions in those dependencies and partial
browser qualification before starting tools or containers. The matrix plan also checks its required
tuples and counts during cheap frontend tests and spec collection, before creating its dataset.
Direct local commands do not enforce prior check completion; apply the same sequencing when invoking
them. CI dependency checks prevent those workflow regressions, not every possible manual misuse.

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
Reuse installed dependencies while the lockfile/toolchain inputs are unchanged. A test-selector-only
edit needs its test checks, not another product asset build or dependency installation.
Frontend file selection runs every test in the selected files;
name filters can create skipped results that the required-test reporter rejects. A build alone proves
neither behavior nor integration. Use named stages when the whole affected suite is the useful unit.

Complete published-browser qualification has a 40-minute Playwright execution safety cap for the
expanded workflow and design-matrix inventory. `BrowserTests.fs` owns scope limits (complete 40,
diagnostic 20, smoke 2 minutes), passes them explicitly to Playwright, and gives its native process
one further minute for reporting and cleanup. The acceptance project has a 60-minute MTP bound for one
explicit engine, with 70 minutes including CI setup. The default local invocation sequences all three
engines once under a 150-minute project bound. These capacity bounds do not change per-test
limits, one worker, zero retries, strict completion or product performance thresholds; reaching a cap
is a failure, not a performance pass. Ad-hoc Playwright keeps its 20-minute configuration default.
Secondary-principal specs acquire real OIDC sessions in their own `beforeAll` setup under the existing
login-admission wait. Keep those cookies within the spec that acquired them so authentication
deadlines remain meaningful during long runs.

For concrete browser failures, select existing specs and one engine. With current Release
applications/test assembly and frontend assets already built:

```sh
CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE=chromium \
CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE=diagnostic \
CLAIMCORE_ACCEPTANCE_BROWSER_SPEC=design-matrix.spec.ts \
dotnet test --project tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj \
  --configuration Release --no-build --no-restore --zero-tests-policy=strict \
  --filter 'FullyQualifiedName~published browser diagnostic' \
  -- --settings=eng/expecto.acceptance.runsettings
```

This starts the same isolated fixture and keeps the existing hard limits, but runs only the selected
files. `CLAIMCORE_ACCEPTANCE_BROWSER_SPEC` also accepts a comma-separated list of unique exact
existing basenames (no spaces or paths). Diagnostics collect all selected failures with
`--max-failures=0`, under the unchanged 20-minute, one-worker, zero-retry limits. Selection is
validated before fixture startup; every selected test must pass without skips or retries, and reports
explicitly state incomplete qualification. Selected specs share mutable fixture state, so later failures
may be cascades: inspect chronology and current state before treating them as independent defects.
To run one existing test within one spec, also set `CLAIMCORE_ACCEPTANCE_BROWSER_CASE` to its exact
literal title (at most 256 Unicode scalars, unpadded and without controls). The runner escapes it as an
exact suffix and validates exactly one existing file/title/engine identity with Playwright `--list`
before fixture startup. Completion must match that identity, pass without skips or retries, and reports
include `selectedCase` with `fullQualification=false`; it does not establish whole-file coverage.
Existing selected-file artifact requirements remain; select the case that produces them.
The ordinary acceptance gate refuses diagnostic selection, including case selectors. Use this path to diagnose the affected
spec instead of restarting complete suites and replaying unrelated passing tests.

For a Linux-only browser failure, dispatch the existing CI workflow with `browser_spec` set to that
basename and optional `browser_engine=chromium|firefox|webkit` (default Chromium), for example
`gh workflow run ci.yml --ref BRANCH -f browser_spec=presentation-foundations.spec.ts -f browser_engine=webkit`.
This runs only the explicitly partial one-file diagnostic; qualification, growth and `Gate` jobs are
skipped. Invalid or ambiguous spec names and unknown engines fail before setup. A nondefault engine
without `browser_spec` fails configuration and blocks qualification jobs; ordinary dispatches keep full
qualification. Chromium is also installed for CLI authentication bootstrap when the selected test
engine differs. Add `-f browser_case='en 640px green light at 200% zoom'` with
`-f browser_spec=design-matrix.spec.ts` to diagnose only that existing planned dimension. It still
requires all 29 scene loads and 30 checks, with the expected captures, and remains partial.
Case-only input is rejected before qualification setup. The existing diagnostic execution and outer
time limits are unchanged.

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
Published Web services drain logs for their fixture lifetime, retaining at most the final 8 MiB of
stdout and 1 MiB of stderr. Their private `*.capture.json` records total and retained byte counts,
truncation, EOF and cleanup completion; verification reports retain only this counts-and-flags metadata.
A retained tail is not a complete log. Fixture teardown bounds
process termination and pipe draining. Command output limits and execution deadlines remain strict.
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
unchanged. Rerun the affected byte/contract tests without update mode, then complete the final required
qualification. Updating goldens never resolves permanent-format failures.

## Growth

Performance measurements are optional unless a task or release plan requires them. They are separate
from the ordinary `Gate`. State the concrete question, affected workload and reference before starting;
use the smallest existing diagnostic that answers it. A correction to UI copy, selectors or formatting
does not by itself call for a new growth run.

| Need | Existing command | Claim |
| --- | --- | --- |
| One write profile | `CLAIMCORE_GROWTH_PROFILE=1000:1 bash eng/verify.sh growth` | Current-source observation, not full qualification |
| Investigate a relative regression | `CLAIMCORE_GROWTH_PROFILE=10000:1 bash eng/verify.sh growth-compare REF` | Targeted diagnostic; successful budgets still exit 2 |
| Required full comparison | `bash eng/verify.sh growth-compare REF` | Three same-host pairs, six complete runs |

A full comparison repeatedly seeds large populations and may take hours. Start it after relevant
product code and workload fingerprints are stable and ordinary correctness qualification has passed
for those inputs. CI enforces this with the ordinary `Gate` dependency; for a local comparison, first
confirm applicable passing correctness evidence. Do not run a
full observation and a full comparison merely for duplicate coverage. Reuse existing results only when
their inputs, platform and question still apply; changed relevant code requires new evidence. Explicit
acceptance criteria remain binding, and a diagnostic or incomplete run never becomes a full pass.

Keep the existing throughput, latency, verification and resource limits. Inspect failures and current
progress before repeating a run; fix a bad prerequisite or method rather than weakening a check.
For workload definitions, source capture, reference equivalence, reports and cleanup, see
[Performance measurements](performance.md). The [workload plan](../tests/ClaimCore.IntegrationTests/GrowthWorkloadPlan.fs)
owns the exact populations and keys.

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
