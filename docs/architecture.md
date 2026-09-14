# Architecture

ClaimCore is one local product, not a distributed system. An F#/.NET core, local HTTPS host,
React/TypeScript browser client, and PostgreSQL form the runtime. Repository engineering programs
and tests qualify that runtime; they are not product services.

## Modular-monolith boundary

The CLI and Web host are separate local process adapters over the same typed `IClaimsCore` facade.
Each composes its own core runtime; the React client uses only the local Web host. The CLI need not
start or call the Web host, and the browser does not load native domain or storage code. The Database
executable retains separate schema-owner authority and is not a case-work client. No remote service
or additional business field is introduced by this structure.

## Compiled architecture enforcement

The dedicated `ClaimCore.ArchitectureTests` project uses ArchUnitNET only as a test dependency. Its
F# fixtures establish that the selected rules detect module functions, generic/nested types,
closures, tasks, async workflows, sequences, records, unions, interfaces and selected platform calls.
Positive counterparts prove that permitted code is not rejected indiscriminately. Missing assembly
inputs and empty required selections fail rather than being interpreted as absence of violations.

The suite inspects Debug implementation assemblies with optimisation disabled, retaining generated
types. It classifies all discovered product projects and checks both declared project references and
compiled type dependencies against the component policy. Selected ambient API, endpoint/composition,
and direct persistence/decision-call checks cover specific high-risk edges; they are not a general
effect or authorization proof. An in-memory forbidden-reference fixture proves that an unused
`ProjectReference` is rejected.

These checks complement curated signatures, ordinary-consumer compile tests, protocol tests and
real PostgreSQL/browser qualifications. They do not prove transaction correctness, complete effect
freedom, runtime reflection behavior, or TypeScript dependencies. New rule selectors and expected
results require owner review; a candidate cannot authorize weaker policy merely by making its own
tests green. Commands and exact evidence registration are owned by
[Development](development.md#architecture-inspection).

## Runtime responsibilities

- **Domain** owns accepted values, field metadata, validation, available transitions, and case state.
- **RecordFormat** owns byte-stable canonical command-record format 2 and historical snapshots. It
  also owns recovery-envelope format 1.
- **Application** owns the typed `IClaimsCore` facade, request admission, operation identity,
  endpoint-specific outcomes, and the sole public recovery workflow.
- **Contracts** projects Application's static semantic description into canonical CLI-v3 and Web-v2
  wire contracts, exact request/response schemas, pure deterministic JSON codecs, generated
  TypeScript DTO modules, conformance corpora, and browser validators. Application does not depend on
  Contracts, JSON, ASP.NET, PostgreSQL, CLI, or Web.
- **Postgres** owns durable storage, ordered migrations, connection validation, transaction
  boundaries, and the private technical recovery store. One composed runtime owns one
  `NpgsqlDataSource`, shared by its private claim and recovery stores for its lifetime.
- **HostSecurity** owns handle-first local private-file and directory admission shared by CLI, Web,
  and Database. It has no claims or transport decision authority; unsupported Windows private-file
  operations fail closed.
- **CLI** owns strict CLI-v3 framing and process delivery, calls HostSecurity for private artifacts,
  and delegates JSON encoding to Contracts.
- **Web** owns loopback HTTPS admission, Web-v2 transport, session handling, and browser interaction.
  It calls HostSecurity for credential, certificate, state, and lock paths. Its thin HTTP results
  deliver Contracts-owned codec bytes and never receive a store, retained
  preparation, recovery port, or transition callback.
- **Database** is the schema-owner administration executable for ordered migrations and bounded
  technical-preparation pruning; its admin credential also passes HostSecurity. It does not process
  case commands.

Dependency direction points toward Domain and Application. CLI and Web may collect and render data,
but they do not decide transitions, settle recovery attempts, or write database rows. PostgreSQL
enforces durable structural integrity but does not become a second claims workflow.

## Public boundary

Normal native callers use the endpoint-typed facade:

```fsharp
type IClaimsCore =
    abstract Describe: unit -> CoreDescription
    abstract Prepare: CommandDraft * CancellationToken -> Task<PrepareOutcome>
    abstract Execute: CommandDraft * CancellationToken -> Task<SubmissionOutcome>
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
its lifetime after use. The store, data source, preparation record, recovery port, and transition
callback are implementation details. An editable view, a preview, or advertised command is never
commit authority; execution always revalidates authoritative state and expected revision.

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

## Transaction and recovery path

1. A CLI or Web adapter decodes an exact endpoint body into a `CommandDraft` or endpoint request.
2. Application validates and binds the draft, preserves its operation ID and authored values, and
   derives canonical format-2 request bytes and their SHA-256 identity.
3. `Prepare` checks accepted operation identity and stored request fingerprint first. An exact
   accepted request returns its receipt even if technical preparation was pruned; a same-ID conflict
   discloses no receipt. Otherwise it checks retained identity, obtains a Domain advisory review for
   fresh reviewable work, and durably retains technical recovery material before returning `Prepared`.
   A retained request that is no longer reviewable reports `RetainedForRecovery`; a technical write
   whose completion cannot be established returns an explicit unknown outcome.
4. `Execute` and `Recovery.Resolve` check accepted identity before technical recovery. For a new
   attempt, they serialize the exact operation, revalidate it under PostgreSQL transaction locks,
   apply the Domain transition, and retain the receipt atomically when accepted.
5. Recovery attempt admission and settlement are separate technical dimensions. A definite business
   result is not replaced by an unconfirmed settlement; unresolved and unknown outcomes remain
   recoverable.
6. CLI and Web render the endpoint-specific result without rebasing, inventing an operation ID, or
   inferring non-commit.

PostgreSQL detailed recovery reads project actual attempt IDs and their settlements plus the
independent pre-003 uncertainty marker. Application keeps these separate from producer provenance and
accepted claim history. Before a technical COMMIT begins, cancellation can prove a rollback and yield
a definite cancelled outcome. Once COMMIT starts, its result is not cancellable into a claim of
non-commit: lost confirmation remains explicitly unknown. An unconfirmed technical settlement never
erases a definite business receipt.

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

## Trust boundary

F# access control protects ordinary callers from accidental bypass; it is not authentication or a
sandbox. The runtime assumes one trusted local administrative boundary. The Web credential admits a
browser session but supplies no individual identity or per-user authorization. Anyone holding
database credentials may issue SQL outside the core.

Shared or remote use requires a separately designed authenticated host with explicit authorization
and disclosure controls. None is supplied here; see [Security and operations](operations.md).
