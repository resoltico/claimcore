# Architecture

ClaimCore's case-work boundary is one authenticated HTTPS service over a primary PostgreSQL store and a separately credentialed PostgreSQL witness. The React browser and CLI call that service; neither carries a database credential or storage assembly. The local two-cluster qualification proves protocol behavior on one machine, not independent-host survival or production readiness. Repository engineering programs and tests qualify parts of the runtime; they are not additional case-work services.

## Modular-monolith boundary

`ClaimCore.Web` is the only ordinary case-work host. It opens the actor-bound runtime through `ClaimCore.Hosting`, authenticates browser OIDC sessions or CLI/automation bearer tokens, and obtains a fresh actor-scoped core for each request. `ClaimCore.Cli` uses the generated service contract over HTTPS; it never composes a local core or opens PostgreSQL. The browser loads no native Domain or storage code. `ClaimCore.Database` retains separate schema-owner authority and cannot serve case work. Actor, witness, disposition, and privacy metadata remain outside the thirteen `CaseFields`.

## The component contract

[`config/architecture.json`](../config/architecture.json) is this repository's single architecture contract. It
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
| `RecordFormat` | core | Byte-stable canonical command-record format 3, historical snapshots, and signed recovery-artifact format 3. | `Domain` |
| `Application` | core | Actor-bound typed service facades, request admission, operation identity, endpoint outcomes and recovery decisions; internal scoped-core composition seams. | `Domain`, `RecordFormat` |
| `Contracts` | contract | Pure semantic/CLI/Web schema projection, strict HTTP request codecs, wire rendering and generated browser DTOs; no host composition or storage effects. | `Application`, `Domain` |
| `HostSecurity` | infrastructure | Handle-first local private-file and directory admission. No claims or transport decision authority. | none |
| `Postgres` | infrastructure | Durable actor-bound storage, exact fresh-schema admission, witnessed authority and recovery transactions, lifecycle and managed-copy evidence, and schema-owner administration. | `Application`, `Domain`, `RecordFormat`, `Witness` |
| `Witness` | infrastructure | Independent PostgreSQL witness baseline and append-only authority journal; a separate service role can append through a narrow definer function and read back committed evidence. | none |
| `Hosting` | composition | The sole composition root. Owns actor-bound runtime opening, witness/key custody, data-source lifetime, and admission leases; the only component that binds stores to the core. | `Application`, `Domain`, `HostSecurity`, `Postgres`, `RecordFormat`, `Witness` |
| `CliProtocol` | transport | CLI framing, strict JSON decoding, discovery, and typed HTTPS/OIDC client seams. It cannot name a runtime factory or read a database credential. | `Application`, `Contracts`, `HostSecurity` |
| `Cli` | entry point | The CLI process entry point owns stdin/stdout delivery and fail-closed remote-service configuration; it never composes a database runtime. | `Application`, `CliProtocol`, `Contracts` |
| `Web` | entry point | HTTPS OIDC/OAuth admission, actor-bound Web transport, server-side browser sessions, and bearer service requests. Never reaches storage or schema administration. | `Application`, `Contracts`, `Domain`, `HostSecurity`, `Hosting` |
| `Database` | entry point | Owner-private baseline, witness, backup and restore, copy custody, erasure, and activation administration. Never composes case work. | `Application`, `Domain`, `HostSecurity`, `Postgres`, `Witness` |
<!-- generated:end architecture-components -->

Dependency direction points toward Domain and Application. CLI and Web transport code collect and render data but do not decide transitions, settle recovery attempts, or invent authorization rules. Application owns those decisions; PostgreSQL enforces durable structural integrity without becoming a second claims workflow.

`Hosting` alone binds stores and witness/key custody to the actor-bound core. Web links `Hosting` but cannot compile against PostgreSQL administration or its internal ports through undeclared transitive references. CLI links neither `Hosting` nor `Postgres`, and its published tree has no storage driver; only authenticated service requests can reach case work. `Database` links owner administration and never composes the case-work runtime.

### Direct compilation boundaries

`DisableTransitiveProjectReferences` is enabled in the shared build configuration. A component may
compile against only the project references it declares. PostgreSQL's Npgsql dependency keeps its
runtime assets transitive but does not expose compile/build/analyzer assets to case-work hosts.
The evaluated-project rules reject a configuration or imported property that re-enables implicit
transitive project references. Compiler-reference and published-tree tests verify that CLI cannot see or deploy PostgreSQL administration/Npgsql, while Web transport code cannot compile a direct owner-administration call. This is source encapsulation, not a substitute for separate database roles, actor grants, or independent witness custody.

Internal validated commands retain parsed scalars for one decision invocation; the original
authored request remains the authority for canonical operation bytes. Corrections assemble typed
facts and payment progress from accepted state. Restoration and transitions share complete-state
validation, and newly asserted dates are checked before constructing opaque accepted Claim.

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
global access, and alternative networking APIs. oxlint is a development boundary, not a security
sandbox for dynamically constructed JavaScript.

### Composition roots

A composition root is the one place that knows how a case-work runtime is wired. `ClaimCore.Hosting` owns that wiring and its data-source lifetime; `ClaimCore.Web.Program` opens one runtime and supplies only an authenticated principal-to-`IActorClaimsCore` factory to HTTP routes. Route code receives no store, schema owner, retained preparation, or unbound recovery port. Compiled rules name `Program` positively as the sole Web opener so moving composition to another source type fails review.

`ClaimCore.CliProtocol` owns CLI-v4 framing, discovery, OIDC/PKCE and authenticated HTTPS delivery, while `ClaimCore.Cli` owns only the process entry point and standard streams. Neither project references `Hosting` or `Postgres`; a CLI frame cannot open a database runtime. `ClaimCore.Database` is a separate owner-only administration entry point with no case-work facade. These boundaries keep a service credential and an individual actor grant from being mistaken for schema-owner authority.

## Compiled architecture enforcement

<a id="cc-arch-001"></a>
### CC-ARCH-001 — Exact component graph and selected compiled boundaries

The dedicated `ClaimCore.ArchitectureTests` project uses ArchUnitNET only as a test dependency. Its
F# fixtures qualify selected type dependencies and direct method calls through functions, closures,
generic/nested types, tasks, async workflows, sequences, records, unions, and interfaces. Positive
counterparts prove that permitted code is not rejected indiscriminately. Missing assemblies, empty
selectors, and omitted reflected types fail rather than appearing to contain no violations.

The suite checks the manifest from five independent directions:

- **Classification.** Every `.fsproj` under `src/`, `eng/`, and `tests/` is classified exactly once,
  and the manifest never names a project that does not exist.
- **Declared, evaluated, and observed edges.** Raw project XML is compared for every tier, and
  MSBuild-evaluated Debug and Release project, package, and shared-framework items are compared for
  product and tooling components. Every comparison is exact in both directions, so a surplus edge
  and a stale permission each fail. For the product tier the compiled model's cross-assembly edges
  are corroborated by CLR assembly references, so a framework generic attributed to another product
  assembly is not mistaken for an actual dependency. A permitted runtime edge with no observed use
  fails as stale. The one explicit `compileOnlyDependsOn` edge from Database to Domain is instead
  checked against its direct project reference and the Domain types in Postgres owner outcomes that
  Database consumes; the compiler needs that type closure even though the emitted Database assembly
  has no Domain reference. An imported conditional forbidden reference and an unused literal
  reference fail their negative controls.
- **Compiled internals grants.** `internal` is this architecture's primary encapsulation mechanism,
  so every `InternalsVisibleToAttribute` on a product assembly must match the manifest exactly, must
  name a classified component, and must follow a declared project edge. A grant added in source and
  not in the manifest fails.
- **Published surface.** The composition root exports exactly one entry point, storage exports
  only its schema-owner administration surface, Application's storage ports never become public, and
  the CLI protocol exposes no runtime factory or store and publishes only client transport seams.
- **Compiled type and call rules.** The suite inspects non-optimised Debug implementation
  assemblies, including F# generated types, and compares ArchUnitNET's loaded type set to reflection
  for every product assembly on that same run. Domain, RecordFormat, Application, and Contracts must
  avoid the selected ambient console, file-system, environment, randomness, thread, clock, and
  identity-minting APIs. Web runtime opening stays confined to `Program`, only the composition root
  constructs the typed core, and direct domain-decision calls are forbidden outside their owner.
  Web bindings cannot depend on JsonDocument for ClaimCore request decoding; Contracts and the
  explicit third-party OIDC metadata decoder provide positive counterparts. Architecture policy
  parsing rejects duplicate/unknown fields and escaped project paths before loading assemblies.
  Each of these rules asserts its positive counterpart as well, so none can pass vacuously.

Each platform's passing suite emits a bounded, sorted report of the actual inspected assembly type
counts and cross-component edges. Counts are observations, not thresholds. The inspection builds the
report from the manifest's own product inventory and fails when a required assembly is omitted or no
cross-product edge is observed; CI shows a concise graph in its job summary. A rule violation fails its
named test with an actionable source/target diagnostic; a report alone is not proof of correct behavior.

These checks complement curated signatures, ordinary-consumer compile tests, protocol tests and
real PostgreSQL/browser qualifications. They do not prove transaction correctness, complete effect
freedom, every possible async-lambda call, runtime reflection behavior, or TypeScript dependencies.
New rule selectors and expected results require owner review; a candidate cannot authorize weaker
policy merely by making its own tests green. Commands and exact evidence registration are owned by
[Development](development.md#architecture-inspection).

<a id="cc-run-001"></a>
### CC-RUN-001 — Runtime lifetime closes admission without relabeling admitted work

`OpenPostgres` observes caller cancellation through connection, role, ACL, schema, lineage, and full-data audit admission; a cancelled opening returns a safe typed runtime fault and never hands out a live facade.
An unexpected opener exception maps to a safe fault and closes the source it created, without
disclosing provider detail.
Once opened, the lifetime owns the ordinary primary source, separate bounded read-barrier and two-connection full-audit sources, and witness store with its bounded read-fence pool; every actor-bound `IActorClaimsCore` and Recovery call enters
through an admission lease. Disposal closes new admission, including through previously retained
facade references. It drains admitted work for a bounded 30 seconds; if a call is still active, the
sources remain owned until the final lease ends, then close exactly once. An admitted query or
mutation keeps its original typed result, including a definite receipt, rather than being relabeled
as cancellation or failure by concurrent disposal.

## Public boundary

Application's public service boundary is `IActorClaimsCore`, obtained through
`ClaimCore.Hosting.Runtime.ForActor` with a principal acquired from validated authentication.
Every endpoint rechecks current actor grants. Native Prepare/Execute accept Domain CommandRequest;
Web binds its transport draft exactly once through Drafts. Query, recovery, management, lifecycle,
tombstone and approval members retain their distinct typed outcomes.

The complete supported signature is [Core.fsi](../src/ClaimCore.Application/Core.fsi).
`IClaimsCore` and CoreApi are internal composition seams, unavailable to ordinary consumers.
Hosting owns the scoped stores, credentials and runtime lifetime; the facade exposes none of them.
Public views, previews and advertised commands remain advisory rather than commit authority.

Pure ClaimCore HTTP request decoding, transport DTOs, schemas and wire rendering belong to Contracts.
Web owns bounded HTTP stream acquisition, Kestrel failures, authentication, dispatch and delivery.
The compiled decoder rule excludes only OidcAuthority's distinct third-party discovery boundary.

## Core outcome meaning and presentation

Ordinary rejections, core faults and recovery lifecycle refusals are separate closed typed reasons.
Their classification and recommended action are derived, never independently writable. Stable
diagnostic identities and exact safe parameters are part of semantic discovery and both wire
contracts. Contracts renders default English outside Domain/Application. CLI setup and local-file
failures have their own transport outcome; they cannot manufacture core fault authority. These
changes preserve validation, cancellation, commit-uncertainty and recovery semantics.
[Core outcome diagnostics](diagnostics.md) owns the scope, schema, privacy rules and remaining
localization boundaries.

## Actor authority and disclosure

<a id="cc-auth-001"></a>
### CC-AUTH-001 — Actor-bound disclosure and list continuation

The OIDC principal maps to a ClaimCore actor whose grants are default-deny and scoped to the requested resource and action. Case and operation reads resolve a target and check current actor authority before returning claimant-bearing data; nonexistent, inaccessible, voided, and suppression-fenced identities use the same public refusal and guidance. Mutation and read stores recheck the authoritative grant revision under their data lock, so a revoked grant cannot authorize a later disclosure or commit through a previously admitted context. Disclosure leases take a primary shared authority lock before the independent witness read fence, matching the primary-first order of witness-writing mutations; separate bounded primary-barrier and witness-fence pools let nested case and evidence reads use their ordinary pools without self-exhaustion. Case-list SQL filters inaccessible or disposition-blocked rows before pagination; its continuation is encrypted and authenticated for the exact principal, actor, grant revision, page size, and a 15-minute lifetime. A malformed, foreign, expired, changed-grant, or post-restart token is refused as one invalid cursor and must not be treated as a raw case reference. The paired synthetic timing tests check only a broad denial class, not constant-time behavior or resistance to privileged database observation.

## Transaction and recovery path

1. The CLI validates the generated endpoint body and sends it over authenticated HTTPS. Web decodes the exact body into its form shape and uses the one pure Application binder to create the closed Domain `CommandRequest`; native callers supply that closed request directly.
2. Application revalidates the request, preserves its operation ID and authored values, and derives
   canonical format-3 request bytes and their SHA-256 identity.
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
5. One retained-attempt transaction path owns command writes. Claim-store observations have no Domain callback or fresh execution capability. Exact accepted observations reconcile committed witness candidates only in mutation-bound calls; read-only case, history and operation views verify independent settlement without appending. Current projections verify both accepted field history and the current lifecycle tip, since lifecycle events can advance revisions without changing the thirteen fields.
6. Recovery attempt admission and settlement are separate technical dimensions. A definite business
   result is not replaced by an unconfirmed settlement; unresolved and unknown outcomes remain
   recoverable. Pending recovery capacity is separate from retained terminal evidence, attempt
   inspection is keyset-paged, and pruned revocations remain payload-free tombstones.
7. CLI and Web render the endpoint-specific result without rebasing, inventing an operation ID, or
   inferring non-commit.

PostgreSQL detailed recovery reads project actual attempt IDs and their settlements through a bounded
operation-bound keyset page. Recovery detail/header/evidence reads share a repeatable primary snapshot so concurrent owner pruning cannot mix retained metadata with a different attempt set. Unsettled attempts are never converted to definite settlement by a
later acceptance or revocation and continue to block pruning of that preparation. A replay observed after attempt admission returns an observed receipt; it does not co-commit a new acceptance or settle a different historical attempt. A settled unaccepted attempt is refused before Domain execution or a witness intent; a fresh admitted attempt can retry the unchanged request. Application keeps these
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
It is not a commit-uncertainty result. Bounded technical preparations, attempts, and settlements
record recovery evidence rather than claim state or accepted history. A durable dismissal or
revocation also ends future authority for the exact unaccepted operation; it does not rewrite an
earlier uncertain attempt.

<a id="cc-app-002"></a>
### CC-APP-002 — Exact operation replay is idempotent and content-bound

For a currently authorized actor with read access to the case, an exact operation ID and canonical
request-content replay returns the retained receipt without a second revision, independently of
technical preparation retention. Different request content under
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
match the product release, the baseline identity must match its frozen checksum, and the installation must
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

F# access control protects ordinary callers from accidental bypass; it is not authentication or a sandbox. The HTTPS host authenticates browser sessions and CLI bearer principals through OIDC, then ClaimCore applies current actor grants to each requested action and resource. The browser and CLI receive no database credential. Host, primary schema-owner, witness schema-owner, and storage administrators remain trusted; direct SQL by those authorities can bypass application checks.

The current host is loopback-bound. A remote or real-data deployment additionally requires separate-host witness and key custody, signed backup/checkpoint publication, restored-pair qualification, and a reviewed single-writer handoff; local synthetic containers do not supply that evidence. See [Security and operations](operations.md).
