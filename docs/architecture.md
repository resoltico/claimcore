# Architecture

One HTTPS service fronts one PostgreSQL authority; CLI and browser hold no database credentials.

<a id="cc-arch-001"></a>

### CC-ARCH-001 — Component graph and compiled boundaries

| Project | Responsibility and direct project dependencies |
| --- | --- |
| Contract | Pure vocabulary/models, typed interaction bindings/codecs, registry, fields, refusals/messages; none |
| Domain | Private validated case state, admission and transitions; Contract |
| RecordFormat | Versioned durable records; Domain, Contract |
| Application | Pure authorization, operation decisions, queries and verification folds; Domain, RecordFormat, Contract |
| Postgres | Locked transactions, reads, projections and schema administration; Application, Domain, RecordFormat, Contract |
| HostSecurity | Managed private-file admission and TLS purposes; none |
| Web | HTTP authentication/admission and composition; Application, Postgres, Contract, HostSecurity |
| CLI | Authenticated HTTPS client, command-line grammar and frames; Contract, HostSecurity |
| Database | Owner administration; Application, Postgres, Contract, HostSecurity |

Evaluated project references define the graph. Compiled guards reject I/O, clocks, environment, randomness and resource
access in Contract/Domain/RecordFormat/Application; Postgres references outside Web/Database; server
decision/persistence assemblies in the published CLI closure; and `JsonSerializer` in Contract/RecordFormat. Negative
compiled fixtures exercise each guard. Web's `Composition/` dispatch binds registry endpoints to the actor core.
Adapters render decisions. The generator derives OpenAPI and client types from Contract and descriptors from
Domain/Application. Generated TypeScript admits drafts, requests and complete replies without runtime schema walking;
exact interaction paths and tagged branches drive forms and CLI operands. All byte readers retain strict JSON; wire and
canonical writers differ in member order. Domain retains private validated types; public request/reply types exist once.

#### Durable records

RecordFormat owns byte grammars, admissible value sets and explicit format dispatch.
[Versions](../src/ClaimCore.RecordFormat/Versions.fs) owns format numbers: case request 6, authority 3,
snapshot 4, entry 2, case identity 2. Only current formats are admitted. Canonical JSON uses RFC 8785 UTF-16
member ordering and integer spelling; authored string values are preserved without normalization. Wire JSON
keeps declaration order with the same string rules. Permanent free-text bounds are 1,024 UTF-8 bytes inside a
32,768-byte framed payload. The independent [Python oracle](../tests/fixtures/oracle/generate_vectors.py) pins
durable bytes. The ledger hash covers metadata and a salted payload commitment, never the payload itself or an
unsalted content digest. Erasure removes payload and nonce while preserving chain verification. The ledger is
the sole acceptance record; projections are derived views written in its transaction. Before 1.0.0 a release
may replace the baseline and refuse incompatible installations untouched. Only the shipped schema and formats
are supported; keep incompatible installations with matching software. No conversion is supplied. From 1.0.0
retain readers for every released format and ship forward migrations.

<a id="cc-run-001"></a>

### CC-RUN-001 — Runtime admission and shutdown

A runtime lease keeps admitted work alive. Shutdown closes admission, drains leases within its bound and closes the
source once. Expiry reports the bound without disposing resources still leased or relabeling/cancelling admitted work.
Ticket operations share that lease; the source closes once after all users drain. Readiness checks
installation/schema access, never freshness or operator approval.

<a id="cc-auth-001"></a>

### CC-AUTH-001 — Actor-bound authorization and disclosure

[AuthorizationPolicy](../src/ClaimCore.Application/AuthorizationPolicy.fs) owns role/capability and
act/capability tables. Grants default-deny at installation or case scope and are evaluated under the write
lock or in the read snapshot. IdP membership and database roles confer no case authority. OWNER administers
authority without granting case work. OWNER and DATA_STEWARD are human-only; at least one enabled human
installation owner remains. Inaccessible and absent resources share a public refusal. Authorization precedes
operation-identity replay/conflict disclosure. Current snapshots require ReadCase; superseded snapshots
require ReadHistoryFull in the same locked/read context, including execute, preview replay, observation and
history. Receipts disclose `DISCLOSED`, `WITHHELD` or `ERASED`; no post-commit authority read decides
disclosure. Context, case advice and installation offers describe current rights and state, never commit
authority. Correction advice fixes unauthorized groups at KEEP. Execution rechecks every requested group.
Actor summaries exclude grants; selected-actor grant pages include their current target header. Caller
installation authority is loaded independently from bounded target grants. Pagination uses canonical unpadded
base64url JSON keysets bound to endpoint/resource, without a secret, identity/authority binding or expiry.
Every page authorizes before decoding its cursor. Editing or sharing a cursor conveys no rights; successive
pages are separate snapshots.

<a id="cc-app-001"></a>

### CC-APP-001 — Rejection changes no accepted state

Every accepted case/authority operation appends exactly one ledger entry and updates projections in one READ
COMMITTED transaction. Rejection changes no accepted ledger or projection state. Writers first call `claimcore.lock_ledger_head()`, then read
authority, operation ID and case; nothing influencing the decision is read before that lock. The store loads,
invokes one pure decision and persists it. The lock helper reads the database instant after locking in a
separate statement, avoiding an instant captured before a waiting writer acquires the lock. That instant and
the immutable installation IANA zone determine the business date; pure code reads no clock or host defaults.
Lock, statement and idle-transaction timeouts bound serialization. Reads use REPEATABLE READ.

<a id="cc-app-002"></a>

### CC-APP-002 — Exact replay and commit uncertainty

An operation is its caller-chosen ID plus canonical request content. While content is retained, authorized
exact replay returns original acceptance metadata with current snapshot disclosure; different content under a
used ID refuses. Erased content cannot be replayed. `OUTCOME_UNKNOWN` means commit confirmation was lost.
Preserve ID, expected revision and exact prepared bytes; never infer failed commit, mint a replacement ID or
rebase an uncertain request. Correlated exact acceptance/replay confirms acceptance; later
rejection, failure or NOT_STARTED cannot disprove an earlier commit. Observation identifies accepted metadata
but cannot prove equality with the submitted content. `operation.resolve` is a separate operational write: under the ledger head lock it returns the author’s receipt or inserts a permanent actor/operation fence and returns `NOT_ACCEPTED`. Execute checks the fence after replay/conflict lookup and refuses `OPERATION_RESOLVED`. Resolving a foreign ID reveals nothing and cannot fence its author. Fences hold no payload and remain indefinitely. Preview is advisory and appends nothing. Cancellation before
commit maps to `FAILED CORE_STORE_UNAVAILABLE`; uncertain transport or cancellation after commit starts maps
to `OUTCOME_UNKNOWN`. Confirmed PostgreSQL errors retain their known abort/integrity/schema/programmer
classification regardless of the commit marker. Verification checks permanent history invariants, commitments,
chain and projections without reapplying today's admission/capability rules. Detected stored violations are
integrity faults; provider/I/O failures are availability faults and programming failures are
`CORE_INTERNAL_ERROR`.

<a id="cc-cat-001"></a>

### CC-CAT-001 — Consumer vocabulary and presentation ownership

Contract owns tokens, public models, codecs, field identities and message keys. Domain owns
admission/transitions; Application owns authorization. Authored en/lv/ar JSON under
`src/ClaimCore.Contract/Localization/` is the sole message source. Hosts embed/import it; pure modules open no
resources. CLI and browser render codes/parameters locally, never server prose. [API](api.md) owns wire shape,
identity and artifacts. Structural descriptors retain message keys, scalar policy hints, field ordering and
correction/authority metadata without duplicating transport paths. Forms and CLI operands consume checked exact bindings. Language
and display-format preferences are independent. Contract also owns the bounded Unicode display-warning
policy; generated descriptors carry its category and count limits to the browser.
