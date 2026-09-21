# Architecture

ClaimCore is one local product, not a distributed system. An F#/.NET core, local HTTPS host,
React/TypeScript browser client, and PostgreSQL form the runtime. Repository engineering programs
and tests qualify that runtime; they are not product services.

## Modular-monolith boundary

The CLI and Web host are separate local process adapters over the same typed `IClaimsCore` facade.
Each asks the composition root for its own runtime; the React client uses only the local Web host.
The CLI need not start or call the Web host, and the browser does not load native domain or storage
code. The Database executable retains separate schema-owner authority and is not a case-work client.
No remote service or additional business field is introduced by this structure.

## The component contract

[`architecture.json`](../architecture.json) is this repository's single architecture contract. It
classifies every `.fsproj` the repository builds — product, tooling, and test — and records each
component's layer, responsibility, permitted direct dependencies, permitted NuGet packages, and
reviewed `InternalsVisibleTo` grants. Nothing restates it: the compiled architecture suite enforces
it, and the table below is generated from it.

Removing an edge, a package, or an internals grant from the manifest requires no compatibility
allowance. Adding one is a reviewed change to the architecture, not an implementation detail.

<!-- generated:begin architecture-components -->
| Component | Layer | Responsibility | Direct dependencies |
|---|---|---|---|
| `Domain` | core | Accepted values, field metadata, validation, available transitions, and case state. | none |
| `RecordFormat` | core | Byte-stable canonical command-record format 2, historical snapshots, and recovery-envelope format 1. | `Domain` |
| `Application` | core | The typed IClaimsCore facade, request admission, operation identity, endpoint outcomes, and the sole public recovery workflow. | `Domain`, `RecordFormat` |
| `Contracts` | contract | Pure projection of Application's semantic description into CLI-v3 and Web-v2 wire contracts, schemas, codecs, and generated browser DTOs. | `Application`, `Domain` |
| `HostSecurity` | infrastructure | Handle-first local private-file and directory admission. No claims or transport decision authority. | none |
| `Postgres` | infrastructure | Durable storage, ordered migrations, connection validation, transaction boundaries, the private technical recovery store, and schema-owner administration. | `Application`, `Domain`, `RecordFormat` |
| `Hosting` | composition | The sole composition root. Owns runtime opening, the data-source lifetime, and the admission lease; the only component that binds a store to the core. | `Application`, `Domain`, `Postgres` |
| `CliProtocol` | transport | CLI-v3 framing, strict JSON decoding, discovery, and endpoint dispatch over a supplied core. It declares the core-supplier abstraction and cannot name a runtime factory. | `Application`, `Contracts`, `Domain`, `HostSecurity` |
| `Cli` | entry point | The CLI composition root and process entry point. It owns the runtime lifetime and stdin/stdout delivery; every protocol decision belongs to CliProtocol. | `Application`, `CliProtocol`, `Contracts`, `Hosting` |
| `Web` | entry point | Loopback HTTPS admission, Web-v2 transport, session handling, and browser interaction over a composed runtime. Never reaches storage or schema administration. | `Application`, `Contracts`, `Domain`, `HostSecurity`, `Hosting` |
| `Database` | entry point | Schema-owner administration for ordered migrations, installation business time, and bounded technical-preparation pruning. Not a case-work client, so it never composes a runtime. | `HostSecurity`, `Postgres` |
<!-- generated:end architecture-components -->

Dependency direction points toward Domain and Application. CLI and Web may collect and render data,
but they do not decide transitions, settle recovery attempts, or write database rows. PostgreSQL
enforces durable structural integrity but does not become a second claims workflow.

`Hosting` is the only component that binds a store to the core, so it is the only one that sees
Application's internal ports. Because a case-work host links `Hosting` rather than `Postgres`, no
CLI or Web code can reach a connection, a row, a migration, or the bounded pruning entry point even
by mistake: those types are not in its compile closure at all. `Database` keeps the opposite
arrangement — it links `Postgres` for schema-owner administration and never composes a runtime.

### Direct compilation boundaries

`DisableTransitiveProjectReferences` is enabled in the shared build configuration. A component may
compile against only the project references it declares. PostgreSQL's Npgsql dependency keeps its
runtime assets transitive but does not expose compile/build/analyzer assets to case-work hosts.
The evaluated-project rules reject a configuration or imported property that re-enables implicit
transitive project references. Compiler-reference tests independently verify that CLI and Web can
see the public facade but not PostgreSQL administration or Npgsql, while the CLI runtime still
contains its storage dependencies. This is source encapsulation, not a substitute for database roles.

### Machine identity and presentation

Semantic identity covers machine names, every scalar constraint, command input shapes, rule IDs
and categories, explicit rule-set revision, and operational/record-format limits. Its versioned
length-framed encoding uses invariant values and collection lengths. Human-facing labels and
explanations are not identity-bearing. Their wire schema remains bounded, but translating copy
alone changes neither semantic nor CLI/Web wire fingerprints. `DomainRules.version` must advance
when executable business behavior changes without a corresponding descriptor change. A digest of
descriptors cannot automatically prove that arbitrary implementations behave identically.

The browser networking policy covers all handwritten browser source except the validated API
adapter. Its positive and negative controls include root components, domain-view helpers, qualified
global access, and alternative networking APIs. ESLint is a development boundary, not a security
sandbox for dynamically constructed JavaScript.

### Composition roots

A composition root is the one place that knows how a runtime is wired. Nothing depends on it, it
depends on everything it wires, and it is small enough that "every type here may compose" is true by
construction. The CLI follows that shape exactly: `CliProtocol` holds all CLI-v3 framing, decoding,
discovery and dispatch and declares an `ICoreSupplier` seam, while `Cli` is the process entry point
that implements the seam by opening a runtime. `CliProtocol` does not reference `Hosting`, so a
decoder cannot open a second runtime — the compiler rejects it before any rule runs.

`Web` keeps a single assembly. Its ASP.NET composition is already eager and singular: `Program`
opens one runtime and hands the typed core to the route map, and every other Web type receives the
core rather than a factory. That confinement is held by a compiled rule rather than a project edge,
so the rule also asserts positively that `Program` *is* the type that opens the runtime; it cannot
pass merely because composition moved out of the selector's reach.

The two hosts differ deliberately. A compiled rule is acceptable when its selector names something
the author declared, as `ClaimCore.Web.Program` does. It is not acceptable when the only way to name
the composition point is through compiler-generated closure names, which encode the source file and
its line numbers: the CLI's composition lived in a class whose `task` members F# hoists into
`<StartupCode$…>`, so renaming a file or moving a member silently changed what the rule selected.
That is the debt the CLI split removed. Splitting `Web` as well would scatter its `InternalsVisibleTo`
grants across two assemblies for no structural gain, so it keeps one assembly and a stable rule.

## Compiled architecture enforcement

The dedicated `ClaimCore.ArchitectureTests` project uses ArchUnitNET only as a test dependency. Its
F# fixtures qualify selected type dependencies and direct method calls through functions, closures,
generic/nested types, tasks, async workflows, sequences, records, unions, and interfaces. Positive
counterparts prove that permitted code is not rejected indiscriminately. Missing assemblies, empty
selectors, and omitted reflected types fail rather than appearing to contain no violations.

The suite checks the manifest from four independent directions:

- **Classification.** Every `.fsproj` under `src/`, `eng/`, and `tests/` is classified exactly once,
  and the manifest never names a project that does not exist.
- **Declared, evaluated, and observed edges.** Raw project XML is compared for every tier, and
  MSBuild-evaluated Debug and Release project, package, and shared-framework items are compared for
  product and tooling components. Every comparison is exact in both directions, so a surplus edge
  and a stale permission each fail. For the product tier the compiled model is compared too, so a
  permitted edge that nothing actually uses fails as a stale permission rather than lingering until
  someone happens to notice. An imported conditional forbidden reference and an unused literal
  reference fail their negative controls.
- **Compiled internals grants.** `internal` is this architecture's primary encapsulation mechanism,
  so every `InternalsVisibleToAttribute` on a product assembly must match the manifest exactly, must
  name a classified component, and must follow a declared project edge. A grant added in source and
  not in the manifest fails.
- **Published surface.** The composition root exports exactly one entry point, storage exports
  only its schema-owner administration surface, Application's storage ports never become public, and
  the CLI protocol publishes its own core seam with no runtime factory type in reach.
- **Compiled type and call rules.** The suite inspects non-optimised Debug implementation
  assemblies, including F# generated types, and compares ArchUnitNET's loaded type set to reflection
  for every product assembly on that same run. Domain, RecordFormat, Application, and Contracts must
  avoid the selected ambient console, file-system, environment, randomness, thread, clock, and
  identity-minting APIs. Web runtime opening stays confined to `Program`, only the composition root
  constructs the typed core, and direct domain-decision calls are forbidden outside their owner.
  Each of these rules asserts its positive counterpart as well, so none can pass vacuously.

Each platform's passing suite emits a bounded, sorted report of the actual inspected assembly type
counts and cross-component edges. Counts are observations, not thresholds. CI scans the report,
requires it in the stage manifest, verifies the downloaded bytes and schema during final evidence —
against the manifest's own product inventory, not a second hard-coded list — and shows a concise
graph in its job summary. A rule violation fails its named test with an actionable source/target
diagnostic; a report alone is not proof of correct behavior.

These checks complement curated signatures, ordinary-consumer compile tests, protocol tests and
real PostgreSQL/browser qualifications. They do not prove transaction correctness, complete effect
freedom, every possible async-lambda call, runtime reflection behavior, or TypeScript dependencies.
New rule selectors and expected results require owner review; a candidate cannot authorize weaker
policy merely by making its own tests green. Commands and exact evidence registration are owned by
[Development](development.md#architecture-inspection).

<a id="cc-run-001"></a>
### CC-RUN-001 — Runtime lifetime closes admission without relabeling admitted work

`OpenPostgres` observes caller cancellation through connection, role, ACL, schema, and lineage
admission; a cancelled opening returns a safe typed runtime fault and never hands out a live facade.
An unexpected opener exception maps to a safe fault and closes the source it created, without
disclosing provider detail.
Once opened, the lifetime owns the data source and every `IClaimsCore` and Recovery call enters
through an admission lease. Disposal closes new admission, including through previously retained
facade references. It drains admitted work for a bounded 30 seconds; if a call is still active, the
data source remains owned until its final lease ends, then closes exactly once. An admitted query or
mutation keeps its original typed result, including a definite receipt, rather than being relabeled
as cancellation or failure by concurrent disposal.

## Public boundary

Normal native callers use the endpoint-typed facade:

```fsharp
type IClaimsCore =
    abstract Describe: unit -> CoreDescription
    abstract Prepare: CommandRequest * CancellationToken -> Task<PrepareOutcome>
    abstract Execute: CommandRequest * CancellationToken -> Task<SubmissionOutcome>
    abstract Get: string * CancellationToken -> Task<QueryOutcome<Lookup<CurrentCase, string>>>
    abstract List: CaseListRequest * CancellationToken -> Task<QueryOutcome<CaseSummaryPage>>
    abstract History: HistoryRequest * CancellationToken -> Task<QueryOutcome<Lookup<HistoryResultPage, string>>>
    abstract ObserveOperation: Guid * CancellationToken -> Task<QueryOutcome<Lookup<OperationReceipt, Guid>>>
    abstract Recovery: IRecoveryWorkflow
```

`Describe` returns the connected runtime description and semantic fingerprint. A fresh `Prepare`
produces a durably retained technical preparation plus a Domain-derived advisory review. An exact
retry instead reports its authoritative accepted receipt or its still-retained but non-reviewable
recovery state; it never fabricates a current-state review. `Execute` is the one-call command
workflow. `Get`, `List`, `History`, and `ObserveOperation` have distinct query
outcomes. Recovery is available only through `IClaimsCore.Recovery`, whose list, inspect, resolve,
dismiss, export, preview-import, and retain-import methods have their own typed lifecycle refusals.

A caller obtains the composed facade through `ClaimCore.Hosting.Runtime.OpenPostgres` and disposes
its lifetime after use. `Runtime` is the only type the composition root exports, and `Postgres`
exports nothing but its schema-owner administration surface; the store, data source, preparation
record, recovery port, and transition callback are unreachable implementation details. An editable
view, a preview, or advertised command is never commit authority; execution always revalidates
authoritative state and expected revision.

## Transaction and recovery path

1. A CLI or Web adapter decodes an exact endpoint body into its form shape and uses the one pure
   Application binder to create the closed Domain `CommandRequest`. Native callers supply that
   closed request directly.
2. Application revalidates the request, preserves its operation ID and authored values, and derives
   canonical format-2 request bytes and their SHA-256 identity.
3. `Prepare` checks accepted operation identity and stored request fingerprint first. An exact
   accepted request returns its receipt even if technical preparation was pruned; a same-ID conflict
   discloses no receipt. Otherwise it checks retained identity, obtains a Domain advisory review for
   fresh reviewable work, and durably retains technical recovery material before returning `Prepared`.
   A retained request that is no longer reviewable reports `RetainedForRecovery`; a technical write
   whose completion cannot be established returns an explicit unknown outcome.
4. `Execute` and `Recovery.Resolve` check accepted identity before technical recovery. For a new
   attempt, PostgreSQL serializes operation authority before the case lock, rechecks accepted and
   revoked identity, applies the pure Domain transition, and co-commits a receipt with its definite
   accepted settlement. A durable revocation ends future execution authority even after an attempt
   was admitted; it never rewrites earlier uncertainty.
5. Recovery attempt admission and settlement are separate technical dimensions. A definite business
   result is not replaced by an unconfirmed settlement; unresolved and unknown outcomes remain
   recoverable. Pending recovery capacity is separate from retained terminal evidence, attempt
   inspection is keyset-paged, and pruned revocations remain payload-free tombstones.
6. CLI and Web render the endpoint-specific result without rebasing, inventing an operation ID, or
   inferring non-commit.

PostgreSQL detailed recovery reads project actual attempt IDs and their settlements through a bounded
operation-bound keyset page, plus the independent pre-003 uncertainty marker. Application keeps these
separate from producer provenance and accepted claim history. Before a technical COMMIT begins,
cancellation can prove a rollback and yield a definite cancelled outcome. Once COMMIT starts, its
result is not cancellable into a claim of non-commit: lost confirmation remains explicitly unknown.
An unconfirmed technical settlement never erases a definite business receipt. Runtime business dates
come from one stored installation IANA zone and one captured instant, not process-local calendar or
zone settings. The composition root is the single place that pairs the stored zone with an observed
instant, and the compiled purity rules keep every component beneath it from reading a host clock,
zone, or fresh identity of its own.

<a id="cc-app-001"></a>
### CC-APP-001 — Business rejection preserves durable business state

A typed `Rejection` result changes neither the current case nor accepted case history in PostgreSQL.
It is not a commit-uncertainty result. Bounded technical preparations, attempts, dismissals, and
settlements are recovery evidence, not claim state or accepted history.

<a id="cc-app-002"></a>
### CC-APP-002 — Exact operation replay is idempotent and content-bound

An exact operation ID and canonical request-content replay returns the retained receipt without a
second revision, independently of technical preparation retention. Different request content under
the same operation ID is rejected as an identity conflict without disclosing the accepted receipt or
another preparation's details.
If commit completion cannot be confirmed, the typed outcome remains uncertain. No adapter may
silently rebase, generate a replacement operation ID, or infer non-commit.

## Refusal and uncertainty

ClaimCore refuses early wherever refusing is safe, and refuses to guess wherever it is not. The two
halves are deliberate, and the second is the harder one.

**Refuse early.** Configuration, admission, and identity are checked before any work is admitted. The
Web host loads and validates its whole configuration - required private paths, one exact HTTPS
localhost origin, every bounded admission limit - before it opens a runtime, and the runtime opens
only after connection, role, ACL, schema, and lineage admission all succeed. Compiled assemblies must
match the product release, the migration manifest must match its checksums, and the installation must
have an explicit stored IANA business zone. None of these degrade into a default.

**Refuse to guess.** A lost commit confirmation is not a failure, and cancellation after `COMMIT`
begins is not a rollback. Those paths deliberately preserve typed uncertainty rather than failing
fast into a claim the system cannot support, which is why an arbitrary exception inside claim
execution maps to an unresolved outcome instead of propagating. Treating them as failures would be
faster to write and would silently destroy the register's central guarantee.

**Boundaries are total.** Every decoder that turns externally supplied bytes or an opaque token into
a typed value must refuse hostile input with a typed result. An escaping exception is not a safe
refusal: it degrades a diagnosable rejection into a CLI internal-failure exit code or an HTTP 500,
and loses the reason the input was rejected. `ClaimCore.FuzzQualificationTests` holds that property
for strict JSON, CLI invocation framing, canonical records, recovery envelopes, and cursors.

**The installation owns its calendar.** The stored IANA zone and one captured instant produce every
business date. Only the two components that own that calendar may touch the host time-zone database
at all: `Postgres` validates and stores the identifier, `Hosting` resolves it once and derives the
date. A compiled rule keeps every other component away from `TimeZoneInfo`, so no adapter can derive
a second calendar from the host. The identifier is bound exactly; the daylight-saving rules behind it
are the host's, which [Security and operations](operations.md#the-installation-calendar) records as
an operational dependency rather than a solved problem.

**Impossible states are refused, not defaulted.** Where a value cannot be absent, the code says so
rather than substituting a sentinel. Request content identity is derived once from the canonical
request bytes and never read back from a recovery view, whose digest is deliberately withheld in
some disclosures; a cursor's identity write is checked rather than discarded, so a misplaced offset
cannot silently encode a zero operation ID.

## Trust boundary

F# access control protects ordinary callers from accidental bypass; it is not authentication or a
sandbox. The runtime assumes one trusted local administrative boundary. The Web credential admits a
browser session but supplies no individual identity or per-user authorization. Anyone holding
database credentials may issue SQL outside the core.

Shared or remote use requires a separately designed authenticated host with explicit authorization
and disclosure controls. None is supplied here; see [Security and operations](operations.md).
