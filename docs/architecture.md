# Architecture

ClaimCore is one HTTPS service over one PostgreSQL authority. Browser and CLI clients carry
no database credentials. Domain owns business and stewardship decisions; Application owns
identity, authorization and outcomes; the store executes pure decisions against locked state.

<a id="cc-arch-001"></a>

### CC-ARCH-001 — Component graph and compiled boundaries

[The architecture manifest](../config/architecture.json) owns direct project references in both
directions. Domain, RecordFormat, Application, ClientProtocol and Contracts call no clock, file,
environment, randomness or network APIs. Only Hosting and Database reference Postgres; only Web references
Hosting. These referencer rules concern other projects, not a component's own types.
TimeZoneInfo belongs only to Hosting, Database and Postgres. Compiled negative fixtures prove
each rule rejects a forbidden dependency or call. Evaluated
MSBuild reference items include imported edges. Published-client closure and facade signature
checks cover the client boundary; frontend resolved-import guards cover renderer/interaction/
transport dependencies. These controls reject specified violations, not every expressible semantic
copy. Internals grants name a permitted referencer
or an actual test project. Test projects reference only product or tooling projects.

RecordFormat owns durable byte encodings. Contracts owns the endpoint catalogue, typed bindings
and core-derived interaction definitions.
ClientProtocol owns generic strict byte/schema admission, correlation and finite delivery policy,
without core dependencies. ContractGenerator projects the same definitions into tracked native
catalogue values and executable browser schemas/builders; complete inventory and byte checks
reject missing, extra and stale outputs. Structural JSON Schema alone does not express the
fingerprinted byte profile (UTF-8, duplicate names, Unicode, numeric lexemes and bounds). HostSecurity
admits private files through its own original public-header C17 shim. Only scalar admission
statuses and the admitted descriptor cross that native boundary; platform layouts and constants
remain in C. The managed owner consumes that descriptor once. Shared native build plumbing also
builds the separate test-only process supervisor, without sharing either mechanism or adding a
product process-supervision dependency. Product native items belong only to HostSecurity;
test executables belong only to their test projects.
Postgres owns transactions, projection persistence, schema administration and verification.
Hosting owns case-work composition, typed protocol dispatch and runtime lifetime. Its public service
accepts verified subject facts and opaque admitted inputs and returns encoded replies with safe audit
metadata. The typed runtime and actor factory remain internal to Hosting. Web references Hosting,
ClientProtocol and HostSecurity; CLI references only
ClientProtocol and HostSecurity. The published CLI excludes server semantic assemblies. Web verifies
HTTP identity and transport admission; Hosting binds the principal and invokes the actor core.
Hosting omits F# cross-assembly optimization data so the host compiler cannot import typed
dispatch implementation through a protocol signature. CLI delivers generated-contract frames;
Database is the separate owner administration process.
Database references Application to validate principals and render shared ledger/authority values,
so it does not duplicate their definitions. ContractGenerator derives artifacts from Contracts.

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
A format number changes only when its bytes change, and decoders dispatch explicitly by format.
Before 1.0.0, releases may explicitly refuse older installations untouched. From 1.0.0, readers
must retain every format a release wrote and schema changes require forward migrations.
[Database admission](database.md#cc-db-001) refuses unsupported installations without conversion.

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
Cursors carry a bounded HMAC-authenticated envelope. One ephemeral Hosting runtime key is shared
by its actor facades; restart invalidates it. The envelope binds installation, principal, actor ID
and enabled state, all sorted/deduplicated grants, endpoint, normalized query/resource/detail/order
and continuation. Page size is excluded so callers may change limits. MAC verification precedes
position use; the same REPEATABLE READ snapshot supplies authority and database time. Validity is
issued ≤ database now < expiry, with a 15-minute lifetime. Tokens confer no rights.

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
pages load a bounded target header and grant rows in one REPEATABLE READ snapshot. Complete caller
grants remain internal authorization and cursor-binding inputs, including when caller equals target. Human-only grants are enforced on their target as well as their executor.

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
current locked/read context. Different content under the same ID is refused. OUTCOME_UNKNOWN
means a commit confirmation was lost. It does not prove rejection and never permits rebasing or
replacement identity. Erased request content cannot be replayed. Reads use one REPEATABLE READ snapshot. Public actor and grant pages are bounded at 50 rows;
FULL history uses an Application-owned capacity of 10 while SUMMARY remains 50. The
RecordFormat payload bound and independent worst-escaping codec controls establish the FULL page's
wire budget. A continuation follows the last included row; a short page is not necessarily terminal.

Verification folds permanent record and authority invariants, compares projections and checks
chain/commitments. It does not replay today’s capability matrix or admission rules against history.
[Database](database.md) owns findings and operator exit codes. Normal read/replay checks the retained
nonce and framed payload commitment before using stored identity or returning receipts.
