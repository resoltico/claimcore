# Contributing to the implementation

Use this guide for a source checkout, an implementation change or a verification report. The
[case-work guide](case-work.md) owns user-facing meaning; [operating](operating.md) owns deployment and
custody procedures. See the [glossary](glossary.md) for terminology.

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

Use the declared versions before restoring. The frontend can build without executable .NET or Python;
ordinary .NET builds run neither npm nor generation. Framework-dependent apphost publication is qualified;
retain the whole publish directory and reviewed [notices](../eng/licenses/third-party-notices.json).
Self-contained, trimmed and single-file publication need separate qualification.

## Project boundaries

| Project | Responsibility |
| --- | --- |
| Json | Strict JSON values, writers, codecs and schemas |
| Domain | JSON-free values and identifiers, typed acts, accepted state, outcomes, authority policy and decisions |
| Protocol | Wire codecs, endpoints, catalog, renderer-neutral messages and presentation policy, shared command-line contracts |
| RecordFormat | Durable grammars, literal names, permanent byte limits and cursor encoding |
| Application | ServiceContext-based orchestration, named port inputs, replay, fencing and verification |
| Postgres | Transaction primitives, projections and installation administration |
| HostSecurity | Private files, HTTPS origins and TLS purposes |
| Web | Authentication, request gates, runtime leases and route serving |
| CLI | HTTPS, command grammar, typed replies and frames |
| Database | Owner administration |

Domain has no project dependencies. Protocol and RecordFormat build codecs over Domain structure using
Json; Application uses Domain and RecordFormat. Protocol owns combined act/scope vocabularies, endpoint names and catalog read declarations. Postgres implements Application ports using Domain,
RecordFormat and Json. Application and Postgres have no Protocol reference. Web and Database use the
Application Service and port surface; the published CLI contains Domain, Protocol, Json and HostSecurity,
with Application and Postgres excluded. The
[project-edge policy](../tests/ClaimCore.Tests/Architecture/ProjectReferences.fs) owns the exact direct edges.

[Compiled architecture controls](../tests/ClaimCore.Tests/Architecture/) run ArchUnitNET in Debug. Subjects
cover whole assemblies because F# task/async bodies and closures can live in StartupCode types. Nonempty
forbidden sets and compiled direct/task controls cover Domain isolation, durable vocabulary isolation,
service-model independence, host usage and vocabulary naming. Domain controls verify each forbidden
assembly separately, so duplicate fixtures cannot mask a missing boundary. Product internals may be exposed
to tests only.

[Domain ownership](../tests/ClaimCore.Tests/Architecture/DomainOwnershipTests.fs) follows compiler symbol
references from Application, RecordFormat and Postgres through called Domain helpers. The pinned SDK
supplies the compiler service. Compiled controls distinguish a used helper from an unrelated presentation
member in the same module, and preserve references in signatures and later body expressions.
Unused-declaration controls use actual product consumers and compiler-visible union-case uses, including
pattern matches. Structural checks compare record fields and union cases; generated containers are not
source declarations. Explicit exceptions require a concrete consumer or ownership reason.

A vocabulary uses the module named for its value type, compiled as `<Type>Module`. `CaseAct.slot` and `AuthorityAct.slot`
pair typed inject/project functions; wire and durable codecs select their own member names without
reconstructing a kind/payload pairing. [Domain outcome models](../src/ClaimCore.Domain/Outcomes/) own typed outcomes;
[ServiceContext](../src/ClaimCore.Application/ServiceContext.fs) receives product identity and dependencies
from host composition. Multi-argument transaction and store members take named records.

Change value rules in [Values.fs](../src/ClaimCore.Domain/Values.fs), and record/operation structure in the
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

Private registration, decision and payment wrappers hold complete `GroupValues<Authored>` or
`GroupValues<Admitted>` in [CaseFields.fs](../src/ClaimCore.Domain/CaseFields.fs). Their constructors check
exact membership and admitted value kinds against the declarations. Group formatting and parsing iterate
those fields through [FieldValue](../src/ClaimCore.Domain/FieldValues.fs), which dispatches to the owning
value module. [Validation](../src/ClaimCore.Domain/Validation.fs) maps field failures to input issues;
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
every case kind, snapshot status, authority kind, role and scope on each of 200 deterministic trials, with
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

The case projection contains identity, revision, lifecycle and a nullable snapshot. Live snapshots store
exact [CaseRecord.encodeSnapshot](../src/ClaimCore.RecordFormat/CaseRecord.fs) bytes; erasure stores NULL.
Reads decode the permanent record and check its reference, revision and lifecycle against the columns and
its derived case ID. Decode or agreement failures are integrity faults. Verification folds the ledger and
compares the projection snapshot bytes exactly. Case progress and business-value invariants belong to Domain
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

Product and test F#/TypeScript files have a 500-line limit. Functions permit five parameters and F#
tuples four items; generated contracts are excluded by path. Split files/functions by responsibility.
Only a total table may suppress `CyclomaticComplexity`: its body is one exhaustive match from a closed
union to values, with no nested branching. Put a concrete reason comment immediately before
`// fsharplint:disable-next-line CyclomaticComplexity`. Rule bodies meet complexity 12 by splitting
responsibilities; this exception does not apply to them. Other lint suppressions remain prohibited.

F# rules live in [eng/fsharplint.json](../eng/fsharplint.json); formatting policy
lives in [web/prettier.config.mjs](../web/prettier.config.mjs). The permitted total-table suppression needs
the concrete reason described above.

## Verification

```sh
bash eng/verify.sh --fast
bash eng/verify.sh
```

The first command is an iteration scope. The second runs all default stages. Before the first acceptance
run, and after changing the Playwright version, install the locked engines and system dependencies:

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

## Change probes

Use change probes to test claims about the cost of representative future changes, including work
outside the refactored code. They are experiments, not a general certification of extensibility.
They do not replace or relax [required product verification](#verification), CI or release checks.
Explicit owner requests and stricter task commitments still govern; this procedure does not waive them.

### Select the experiments

Before marking a foundation or refactoring PR ready, assess the five probes below against the changed
responsibilities and affected producers, consumers, generation and test support. Cover all five for broad
foundation changes, running experiments or reusing applicable evidence as described below. For narrower
changes, cover the relevant probes and briefly explain exclusions in the PR. Investigate uncertain
relevance; include a probe if uncertainty remains. Documentation or presentation edits do not automatically
require all five, but changes to rendering or generation can affect them.
Procedure-only documentation changes need no hypothetical feature implementation.

Keep the representative changes and locality targets below stable for comparisons. State the exact
experiment: a READ reusing existing request/result types measures registration cost, not a novel query
design. Disclose any changed experiment or target and its reason; compare like for like or mark the
results incomparable. A locality miss is diagnostic and alone does not block merging. Fewer edits do not
justify generic machinery, weaker types, shared production/oracle answers or removed safeguards.

| Probe | Change | Locality target |
| --- | --- | --- |
| P-FIELD | Add a required registration text field of `PartyName` kind. | No `db/`, PostgreSQL or renderer changes. In Domain, only the new `BusinessField` case and its `describe` entry in `CaseFields.fs` change; no record, parsing, formatting, admission or other Domain function changes. |
| P-ACT | Add a payload-free case act allowed on an open, active case, changing no field. | No renderer, SQL or Protocol codec changes; slots derive the variant. |
| P-ROLE | Add a role with one existing capability. | No renderer or `db/` changes; growing vocabularies are decoded by their permanent-name owner. |
| P-READ | Add a read-only endpoint over existing case data. | No renderer changes except the one exhaustive endpoint-to-handler line in `src/ClaimCore.Web/ApiRoutes.fs` `route`. CLI verb/catalog derive; one store-port method is implemented by PostgreSQL and memory. |
| P-MESSAGE | Add a case refusal and its message. | Only Domain refusal/producer, diagnostic mapping and three locale files change; no renderer changes. |

Here, renderer files mean `src/ClaimCore.Cli/`, `src/ClaimCore.Web/` and `web/src/` outside
`generated/`. Generated contracts and documentation are recorded separately from authored production
files. P-READ's target permits only that composition line in renderer source. Assess P-FIELD's Domain
target across field mapping, parsing and admission as well as metadata and projection. Consolidating metadata in
[CaseFields.fs](../src/ClaimCore.Domain/CaseFields.fs) alone does not establish that criterion.

### Run an experiment

Start newly run probes from one recorded candidate commit, each in its own isolated, source-stable
worktree on a task-specific local branch. Never push experimental branches. Apply the representative
change manually; a saved manually authored patch may be replayed after reviewing its applicability.
Build the solution, [regenerate and check contracts](#generated-contracts), and add a temporary test
exercising the new element through its real path, including relevant refusal or boundary behavior.
Use real PostgreSQL when claiming store behavior; compilation or a memory double alone cannot prove it.

Run `bash eng/verify.sh --fast` for each new or materially changed experimental implementation to expose
maintenance costs beyond the focused proof. Focused iteration checks do not establish a complete fast-gate
pass. A known, unchanged failure need not be repeatedly rediscovered when the evidence below still applies;
keep its failure status and name unrun stages. Fix established defects within the PR's scope and rerun
affected checks or probes; otherwise report the cause and proposed follow-up. Preserve independent semantic
expectations and existing safeguards. Retain useful failure evidence before removing only owned experimental
branches, worktrees and temporary resources; keep hypothetical features out of the delivered product.

### Report outcomes and cost

Keep a short summary in the PR with links to patches and check evidence. Record the candidate, exact
representative change, environment, commands and exercised scope. Report these conclusions separately:

- Locality: each target met or missed, with its cause.
- Behavior: the paths and conditions proved, failed or not exercised; do not infer unrun results.
- Adaptation: complete or incomplete, including applicable compatibility commitments, downstream consumers,
  generated output, tests and fixtures. Claim completion only with a passing complete fast gate and any
  relevant integration checks; targeted fixes after a failed gate alone cannot establish completion.

Count authored production, generated and test/fixture files and added/deleted lines separately. Group
failures by root cause and name affected test files; failure counts alone do not describe change cost.
Record observed implementation/adaptation effort, check elapsed time and reasons for reruns; label estimates
or unavailable measurements. Distinguish avoidable duplication from necessary independent-oracle, composition
and compatibility work. Assess any added abstraction's ongoing cost as well as the experimental diff size.

### Reuse applicable evidence

A later candidate alone does not invalidate an earlier experiment. Review the intervening changes against
the specific conclusion and check inputs, including affected producer/consumer paths, shared fixtures,
generation, build configuration, dependencies, toolchain and relevant execution conditions. Source equality
or unchanged filenames alone do not establish applicability. Keep this a short, evidence-backed rationale
per probe rather than a separate dependency inventory.

Retain the original candidate, patch, environment, check scopes, results and material failures. Record the
revision now assessed and why those conclusions still apply; never relabel an old execution as run on it.
Rerun affected proofs or checks when their basis changes or applicability remains uncertain. A renderer edit
can invalidate rendering evidence while leaving an unrelated Domain conclusion applicable. Preserve unproven
or incomplete outcomes when reusing evidence; a targeted pass cannot convert an earlier failed full gate
into a full pass. These reuse rules concern hypothetical probes only and do not waive required clean,
independent or final-product verification.

Future foundation proposals must cite applicable probe results, a concrete defect or a stated requirement.
Keep measurements and follow-up proposals in PR evidence, not repository progress files. This section is the
single authority for the procedure; existing Git, generation and verification commands suffice. Representative
changes and expected behavior remain manually authored; no maintained script applies the probes automatically.

## Growth

Run `bash eng/verify.sh growth` before a release and on demand. It retains nine write-contention profiles
and three cardinality comparisons, and adds search/filtered read workloads at 1k, 10k and 100k cases.
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
