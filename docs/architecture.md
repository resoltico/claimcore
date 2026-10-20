# Architecture

ClaimCore is one HTTPS service over one PostgreSQL authority. Browser and CLI clients carry
no database credentials. Domain owns business and stewardship decisions; Application owns
identity, authorization and outcomes; the store executes pure decisions against locked state.

<a id="cc-arch-001"></a>

### CC-ARCH-001 — Component graph and compiled boundaries

[The architecture manifest](../config/architecture.json) owns direct project references in both
directions. Catalog, Domain, RecordFormat, Application and Wire call no clock, file,
environment, randomness or network APIs. Only Hosting and Database reference Postgres; only Web references
Hosting. These referencer rules concern other projects, not a component's own types.
TimeZoneInfo belongs only to Hosting, Database and Postgres. Compiled negative fixtures prove
each rule rejects a forbidden dependency or call. Evaluated
MSBuild reference items include imported edges. Published-client closure and facade signature
checks cover the client boundary; frontend resolved-import guards cover renderer/interaction/
transport dependencies. These controls reject specified violations, not every expressible semantic
copy. Internals grants name a permitted referencer
or an actual test project. Test projects reference only product or tooling projects.

RecordFormat owns durable bytes and supports only its versioned record readers and writers.
Catalog owns shared closed vocabulary, diagnostic identities, authored English templates and locale assets.
Wire references only Catalog; it carries no Domain/Application policy. Its typed codecs supply request/reply readers, writers and
schemas for all twelve endpoints. It also owns typed delivery, correlation and CLI frames.
The OpenAPI contract derives from those codecs; the [API contract](api.md) owns the wire profile,
fingerprint and tracked artifacts. ContractGenerator references Wire, Domain and Application to
emit that contract and presentation descriptors. Core input descriptors bind to codec members during generation. The browser assembles requests
from these checked bindings. Both clients consume generated delivery and correlation descriptions.

HostSecurity admits private files using managed APIs under the
[private-input policy](operations.md#private-configuration). Test process capture and teardown use
managed process APIs and are not a product dependency. Postgres owns transactions, scoped reads,
projection persistence, schema administration and verification reads. Application owns pure ledger,
authority, erasure, projection and checkpoint verification folds.

Hosting is the case-work composition root. Its public service accepts verified subject facts and a
Wire request, maps to Application, invokes the actor core and returns a typed Wire reply. Web owns
HTTP authentication, admission, encoding and safe audit logging. Web references Hosting, Wire, HostSecurity and inert Catalog vocabulary;
CLI references Wire, HostSecurity and Catalog and embeds generated discovery artifacts.
The published CLI includes inert Catalog vocabulary and excludes server decision and persistence assemblies. Hosting exposes no Domain/Application
objects through its public facade and needs no optimization-data suppression. Database is the
separate owner administration process; it references Application for shared identities and outcomes.

#### Durable records

RecordFormat writes RFC 8785 canonical request, snapshot and authority JSON with ordinal UTF-16
member ordering and invariant numeric spelling. Business amounts remain exact decimal strings.
The [canonical writer](../src/ClaimCore.RecordFormat/CanonicalJson.fs) can encode escaped NUL;
business-record admission and decoding separately reject it under the [Domain format](domain.md#scalars).
Canonical content gives replay one stable identity across equivalent JSON member order, whitespace
and escape spelling while retaining authored string values without Unicode normalization.

[LedgerEntry](../src/ClaimCore.RecordFormat/LedgerEntry.fs) frames the request and snapshot bytes.
Its entry hash covers a fixed-width metadata preimage containing a salted payload commitment.
The payload and its nonce can be erased while the retained metadata chain remains verifiable;
no unsalted business-content digest is retained alongside it. The same transaction appends the
ledger entry and updates its projections. The ledger is the only acceptance record; projections
are derived views, not independent acceptance evidence. Reads need no second acceptance authority
or request-preparation store. [Operations](operations.md) defines the limits of this trust model.

[RecordVersions](../src/ClaimCore.RecordFormat/Versions.fs) owns durable format identifiers.
A format number identifies one byte grammar and its admissible value set, and decoders dispatch explicitly by format.
Before 1.0.0, releases may explicitly replace the baseline and refuse older installations untouched. From 1.0.0, readers
must retain every format a release wrote and schema changes require forward migrations.
[Database admission](database.md#cc-db-001) refuses unsupported installations without conversion.

Case requests use format 5 and snapshots format 4. Free-text names and stewardship reasons
have a permanent bound of 1,024 UTF-8 bytes, inside a 32,768-byte framed payload bound.
Authority, ledger-entry and case-identity formats remain 2. The independent Python oracle
under [tests/fixtures/oracle](../tests/fixtures/oracle/generate_vectors.py) owns regression vectors.
The replaced baseline refuses installations created by software through 0.10.0 untouched;
keep those installations with their matching software.

<a id="cc-run-001"></a>

### CC-RUN-001 — Runtime admission and shutdown

A runtime lease keeps admitted work alive. Shutdown closes admission, drains admitted leases
within its bound and closes the source once. Expiry does not relabel an operation's outcome or
cancel an already admitted durable mutation. Readiness is bounded installation/schema access,
not a claim of ledger freshness or an operator approval.

<a id="cc-auth-001"></a>

### CC-AUTH-001 — Actor-bound authorization and disclosure

ClaimCore's seven roles and ten capabilities are defined once in
[Application’s capability table](../src/ClaimCore.Application/AuthorizationPolicy.fs). Grants are
installation-wide or case-scoped, default-deny, and checked under the write lock or in the
read snapshot. Identity-provider membership and a database role grant no case authority.
Only humans receive OWNER or DATA_STEWARD; at least one enabled human installation owner
must remain. OWNER is authority administration and does not by itself grant case work.
Absent, inaccessible and erased-beyond-view cases or operations have the same public refusal.
Cursors are unpadded canonical base64url JSON keysets owned by Application. They carry a
folded reference, revision, actor UUID or grant position, with endpoint and resource bindings.
They have no secret, MAC, issue time, expiry or authority binding and survive runtime restarts.
Every page authorizes in its own read snapshot before decoding the position. Sharing or
editing a valid position conveys no rights; page size and history detail may change after
independent authorization.

| Preset             | Capabilities                                                    |
| ------------------ | --------------------------------------------------------------- |
| `OWNER`            | ManageAuthority, ReadAuthority                                  |
| `CASE_READER`      | ReadCase, ReadHistory                                           |
| `CASE_REGISTRAR`   | ReadCase, ReadHistory, Register                                 |
| `CASE_HANDLER`     | ReadCase, ReadHistory, ReadHistoryFull, Register, Decide, Close |
| `PAYMENT_RECORDER` | ReadCase, ReadHistory, RecordPayment                            |
| `DATA_STEWARD`     | ReadCase, ReadHistory, Steward                                  |
| `AUDITOR`          | ReadCase, ReadHistory, ReadHistoryFull, ReadAuthority           |

Register covers OPEN/amend and registration correction; Decide covers decide/withdraw and decision
correction; RecordPayment covers payment/clear and payment correction; Close covers close/reopen.
Correction checks every requested REPLACE/CLEAR group, including unchanged reaffirmations.
Installation ReadAuthority is required for actor listing, selected-actor grant paging and authority
observation; case grants do not supply it. Public actor summaries exclude grants; selected grant
pages load a bounded target header and grant rows in one REPEATABLE READ snapshot. Caller authority loads installation grants and the case scopes that can influence that decision.
A case request loads its target and, if a reused operation ID belongs to another case, that
accepted entry’s scope: at most two cases. Identity authorization and receipt disclosure use
those scopes in the same locked transaction or read snapshot. Context checks assigned
visibility with EXISTS; list pages use a role-parameterized semi-join and never collect all
assigned case IDs or grants. Authority review loads caller installation grants independently
from the bounded selected-actor page, including when caller equals target. Human-only grants are enforced on their target as well as their executor.

A receipt contains attribution ActorId and metadata; its snapshot is DISCLOSED, WITHHELD or ERASED.
A current snapshot requires ReadCase; a superseded one requires ReadHistoryFull. Execute, replay,
preview-ALREADY_ACCEPTED, observation and history apply that rule in their locked/read context;
there is no post-commit authority fetch. Authorize before revealing identity replay or conflict.

Core-returned installation and case offers are advice from the same authority/read snapshot.
They carry operation definitions, target/revision bindings, permitted correction choices and scoped
history detail. Unauthorized correction groups remain required KEEP inputs. Preview evaluates
combinations; execution rechecks current locked authority and state. Case-list rows and installation offers come from the same actor snapshot; clients need no sibling
context request. Grant cursors bind target actor and role-column collation/case UUID ascending,
NULLS LAST order. Installation-null positions pass the final row within that role; case positions
include later UUIDs and that role's installation grant. Retained erased-case references remain visible.
No offer is an authorization token.

<a id="cc-app-001"></a>

### CC-APP-001 — Rejection changes no accepted state

A rejection appends nothing and changes no projection. Writers first lock the singleton ledger
head, then read authority, operation identity and case state in the same READ COMMITTED
transaction. The captured database instant and stored zone determine the business date.

The [lock helper](../db/migrations/0001_baseline.sql) acquires the row lock before reading
`clock_timestamp()` in a subsequent PL/pgSQL statement. Putting the clock in the locking SELECT's
target list would date a waiting writer before its lock wait. The
[transaction tests](../tests/ClaimCore.IntegrationTests/TransactionTests.fs) include that unsafe
ordering as a rejecting control. Lock, statement and idle-transaction timeouts bound serialization.

<a id="cc-app-002"></a>

### CC-APP-002 — Exact replay and commit uncertainty

Every accepted case operation and authority change appends exactly one ledger entry in the
transaction updating its projections. While accepted request content is retained, authorized
exact replay returns the original acceptance metadata with snapshot disclosure evaluated in the
current locked/read context. Different content under the same ID is refused. Identity privacy checks use the accepted
entry’s resource scope as well as permission on the current request’s target; these can be
different cases. OUTCOME_UNKNOWN
means a commit confirmation was lost. It does not prove rejection and never permits rebasing or
replacement identity. Erased request content cannot be replayed. Reads use one REPEATABLE READ snapshot. Public actor and grant pages are bounded at 50 rows;
Both history details use the [API page limit](api.md). Permanent field bounds and the independently computed worst-escaping page budget keep FULL pages within the response limit. A continuation follows the last included row; a short page is not necessarily terminal.

Verification folds permanent record and authority invariants, compares projections and checks
chain/commitments. It does not replay today’s capability matrix or admission rules against history.
[Database](database.md) owns findings and operator exit codes. Normal read/replay checks the retained
nonce and framed payload commitment before using stored identity or returning receipts.

Unexpected programming failures produce `CORE_INTERNAL_ERROR` with `STOP_AND_INVESTIGATE`.
Only an explicitly detected stored-record violation is classified as integrity failure;
provider and I/O failures remain availability failures. A lost commit confirmation remains
`OUTCOME_UNKNOWN`; the runtime does not infer failure or replace the operation identity.

<a id="cc-cat-001"></a>

### CC-CAT-001 — Consumer vocabulary and presentation ownership

[Catalog](../src/ClaimCore.Catalog/ClaimCore.Catalog.fsproj) owns stable shared tokens and safe message
identities. Domain retains admission and transitions; Application retains authorization policy.
Descriptors reference Catalog message identities. English templates and en/lv/ar source assets live
under Catalog/Localization; the generator publishes them for CLI/Web. Pure core modules open no
resources. Node validates and compiles ICU from tracked generated documents without invoking .NET.
Language and display-locale preferences remain independent.

The [consumer manifest](../contracts/contract-manifest.json) has format 1. It orders input names
ordinally and binds their exact bytes with SHA-256. Its canonical bytes have fixed member order,
compact JSON and one final LF. The digest identifies static consumer documents, not current actor
authority, server freshness, review quality or owner approval. Identity-bearing client/source
resources are emitted afterwards and excluded from the input set. Generation never reads a runtime
identity to compute its successor. Freshness compares every produced filename and byte.

Case and authority clients share a tagged mutation lifecycle. Prepared immutable requests own bytes,
identity and expected revision. Preview and confirmation content remain distinct; disclosure refusal
clears server views independently from unresolved delivery. Exact accepted content or content-exclusion
evidence can resolve uncertainty; later availability or permission failure cannot.
