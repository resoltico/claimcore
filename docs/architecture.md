# Architecture

ClaimCore is one local product, not a distributed system. An F#/.NET core, local HTTPS host,
React/TypeScript browser client, and PostgreSQL form the runtime. Repository engineering programs
and tests qualify that runtime; they are not product services.

## Adopted foundation and migration boundary

The target is one modular service authority. Both the agent CLI and the React client will use the
same versioned service operations; neither client may run the native domain or open PostgreSQL.
This does not introduce microservices, remote exposure, additional business fields, or a payment
engine. The separate Database executable remains privileged operational plumbing, not another
case-work interface.

The current native CLI path described below remains the supported implementation until the service
migration is delivered. Its presence is not evidence of target-client isolation. The foundation
work is divided into five bounded packages:

| Package | Boundary and completion evidence |
|---|---|
| F01 | Qualify F# compiled inspection, classify every product root, and retain the existing compiler/behavioral checks. |
| F02 | Extract generated implementation-free .NET protocol bindings beside the TypeScript bindings; client graphs exclude server implementation. |
| F03 | Converge the actual CLI and React paths on the service; trusted admission and runtime composition stay server-owned. |
| F04 | Resolve accepted replay independently of retained-preparation housekeeping; verify pruning, conflict, concurrency and commit-loss scenarios. |
| F05 | Preserve historical encodings and enforce a separately controlled history boundary through a qualified recovery procedure. |

Contracts remains a server-side semantic projection and may depend on Application. It is not a
client-safe distribution contract. F02 supplies the separate generated Protocol surface described
below, without reversing that dependency or duplicating authored operation definitions. Durable
record formats, public wire contracts and private domain representations have different
compatibility obligations.

## Client-safe protocol library

`ClaimCore.Protocol` contains generated immutable F# data shapes, pure JSON codecs and all current
Web-v2 endpoint bindings. It references no other ClaimCore project, server runtime, database driver,
or host framework. The Contracts producer reads the same authored schema/metadata graph that
produces TypeScript; extraction neither opens PostgreSQL nor starts the service. Equivalent schema
shapes share a generated type, while stable decimal/revision strings remain text. Existing browser/schema artifacts,
semantic definitions and fingerprints remain byte-for-byte unchanged by this extraction.

`WebV2.caseGet.Request.Encode(budget, { CaseReference = "EXAMPLE" })`, for example, produces a bounded
request; `WebV2.caseGet.Response.Decode(budget, bytes)` returns a typed endpoint-specific result.
`WebV2.endpoints` describes the same operation IDs, paths, methods and raw-body requirements as the
browser catalog. Bindings contain no transport, credentials, automatic retries or native-core
fallback. Raw imports expose typed required headers and a raw-byte limit; recovery export's binary
success media type remains separate from its JSON outcomes. F03 must supply the real transport and
caller context. The existing CLI has not been converted by F02.

Public records are proposals/read values, not accepted domain state. Codecs reject malformed UTF-8,
malformed Unicode, original duplicate decoded JSON names, unsupported object members and tagged
alternatives, invalid scalar representations, missing required members and excess size/depth. A
missing optional property is different from an explicit JSON null. Exact decimal and revision text
is preserved rather than parsed into a JavaScript number or rounded; integer-valued JSON uses exact
comparison before conversion. Callers explicitly supply a positive byte budget. The encoder bounds
both emitted bytes and temporary escaping reservations. No schema default invents input values.

Shared metadata records are useful typed lists, not positional tuples of today's specific fields.
The exact supported definition is nevertheless checked against its generated semantic projection,
so a changed required definition is not silently accepted as equivalent. Unknown-field and metadata
compatibility behavior intentionally follows the current Web-v2 contract. Extending that policy is
a public-contract change, not permission to relax a decoder locally.

A successful codec result establishes only the supported wire shape. It does not authenticate a
service, correlate a receipt with a pending request/history, authorize disclosure or prove a commit.
Those responsibilities remain with the later qualified client transport and the authoritative
service. An ordinary separately published consumer and negative compiler probes establish that
using Protocol alone supplies no native-server execution surface.

Accepted receipts must remain recoverable independently of whether optional technical preparations
are retained. A restored installation identity does not by itself establish data-history continuity.
History fencing is a server-side mutation precondition, not a new business field or a browser session
counter. Adopted data and historical readers may not be discarded as part of this transformation.

### Compiled architecture enforcement

The dedicated `ClaimCore.ArchitectureTests` project uses ArchUnitNET only as a test dependency. Its
F# fixtures establish that the selected rules detect module functions, generic/nested types,
closures, tasks, async workflows, sequences, records, unions, interfaces and selected platform calls.
Positive counterparts prove that permitted code is not rejected indiscriminately. Missing assembly
inputs and empty required selections fail rather than being interpreted as absence of violations.

The suite inspects Debug implementation assemblies with optimisation disabled, retaining generated
types. It classifies all discovered product projects and enforces component dependency direction,
selected ambient-effect restrictions, endpoint/composition separation, and persistence/decision
ownership. The explicit native-CLI allowance describes the transitional graph; replace it with
client isolation as F02/F03 deliver the supported path. Do not freeze current accidental edges as the
permanent architecture or add skipped target tests and call them enforcement.

These checks complement curated signatures, ordinary-consumer compile tests, protocol tests and
real PostgreSQL/browser qualifications. They do not prove transaction correctness, authorization,
complete effect freedom, runtime reflection behavior, or TypeScript dependencies. New rule selectors
and expected results require owner review; a candidate cannot authorize weaker policy merely by
making its own tests green. Commands and exact evidence registration are owned by
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
3. `Prepare` checks exact retained identity, obtains a Domain advisory review for fresh reviewable
   work, and durably retains technical recovery material before returning `Prepared`. Exact retries
   report `ObservedAccepted` or `RetainedForRecovery`; a technical write whose completion cannot be
   established returns an explicit unknown outcome.
4. `Execute` or `Recovery.Resolve` serializes the exact operation, revalidates it under PostgreSQL
   transaction locks, applies the Domain transition, and retains the receipt atomically when accepted.
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
second revision. Different request content under the same operation ID is rejected as an identity
conflict. If commit completion cannot be confirmed, the typed outcome remains uncertain. No adapter
may silently rebase, generate a replacement operation ID, or infer non-commit.

## Trust boundary

F# access control protects ordinary callers from accidental bypass; it is not authentication or a
sandbox. The runtime assumes one trusted local administrative boundary. The Web credential admits a
browser session but supplies no individual identity or per-user authorization. Anyone holding
database credentials may issue SQL outside the core.

Shared or remote use requires a separately designed authenticated host with explicit authorization
and disclosure controls. None is supplied here; see [Security and operations](operations.md).
