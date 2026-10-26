# Architecture

A browser or CLI sends authenticated HTTPS requests to Web. Application decides what the actor may do;
Postgres loads the authoritative view and persists the result. One database owns acceptance and current state.

<a id="cc-arch-001"></a>

### CC-ARCH-001 — Component graph and compiled boundaries

| Project | Owns | Direct project dependencies |
| --- | --- | --- |
| Contract | Public models, vocabulary, formats, codecs, registry, fields, operation catalog and messages | None |
| Domain | Validated case state, admission and transitions | Contract |
| RecordFormat | Versioned durable encodings | Domain, Contract |
| Application | Authorization, decisions, query selection and verification folds | Domain, RecordFormat, Contract |
| Postgres | Transactions, reads, projections and schema administration | Application, Domain, RecordFormat, Contract |
| HostSecurity | Managed private-file admission and TLS purposes | None |
| Web | Authentication, HTTP admission, dispatch and composition | Application, Postgres, Contract, HostSecurity |
| CLI | HTTPS client, command grammar and frames | Contract, HostSecurity |
| Database | Owner administration | Application, Postgres, Contract, HostSecurity |

The [compiled architecture tests](../tests/ClaimCore.Tests/Architecture/) check the evaluated project graph
and forbidden dependencies, including I/O, clocks and randomness in the four pure projects. Rejecting fixture
assemblies exercise those guards. The published CLI closure cannot contain server decision or persistence
assemblies. Pure code receives time, authority and state explicitly.

The [generator](../eng/ClaimCore.ContractGenerator/) serializes Contract's typed operation catalog and
produces OpenAPI, TypeScript admission and [message contracts](../contracts/messages.ts). It also writes the
[CLI reference](cli.md). CLI syntax and browser paths come from the same catalog tree; current core advice
limits offered work. Neither adapter owns another business policy.

#### Durable records

[RecordFormat](../src/ClaimCore.RecordFormat/) owns byte encodings and explicit format dispatch;
[Versions.fs](../src/ClaimCore.RecordFormat/Versions.fs) owns the supported format numbers.
[Contract Formats.fs](../src/ClaimCore.Contract/Formats.fs) owns permanent scalar syntax and byte bounds.
The independent [Python oracle](../tests/fixtures/oracle/generate_vectors.py) pins durable bytes.
Canonical JSON orders members by RFC 8785 UTF-16 order and preserves authored string values. Wire JSON uses
declaration order with the same string rules.

Ledger hashes cover metadata and salted payload commitments. Erasure removes payload and nonce while leaving
the chain verifiable. Projections are derived state committed with the ledger entry. Verification checks
permanent history invariants without reapplying today's admission or capability policy.

Only the shipped baseline and current formats are admitted. Incompatible installations are refused untouched.
Initialize a separate fresh installation; no in-place upgrade, conversion or compatibility reader is supplied.
Keep incompatible installations with matching software and follow the [restore procedure](operations.md#backups-and-restore)
before reopening access to any restored data.

<a id="cc-run-001"></a>

### CC-RUN-001 — Runtime admission and shutdown

Each admitted call holds a runtime lease. Shutdown closes admission and waits for leases within its bound.
If the bound expires, admitted work retains the source until it drains; its result is preserved. Ticket
operations share this lifetime, and the source closes once. Readiness checks installation/schema access,
without establishing freshness or operator approval.

<a id="cc-auth-001"></a>

### CC-AUTH-001 — Actor-bound authorization and disclosure

[AuthorizationPolicy](../src/ClaimCore.Application/AuthorizationPolicy.fs) owns roles and capabilities.
Grants default-deny at installation or case scope. IdP membership and database roles convey no case rights.
OWNER administers authority; case work needs separate grants. OWNER and DATA_STEWARD are human-only, and at
least one enabled human installation owner must remain.

Authorization precedes operation replay/conflict disclosure. Absent and inaccessible resources share a
refusal. Current snapshots require case read; superseded snapshots require full history. Receipts report
`DISCLOSED`, `WITHHELD` or `ERASED`, using the same locked/read view that produced the result. No later
post-commit read reshapes disclosure. Resolve has its own author-only receipt policy.

Advice describes current state and rights. Execution rechecks them. Correction advice fixes unauthorized
groups at KEEP. Actor summaries contain no grant inventory; bounded grant pages carry their target header.
Pagination positions are canonical unpadded base64url JSON, bound to endpoint/resource. They have no expiry
or authority. Each page authorizes before cursor decoding and reads a separate snapshot.

<a id="cc-app-001"></a>

### CC-APP-001 — Rejection changes no accepted state

A case or authority acceptance appends one entry and updates projections in one READ COMMITTED transaction.
The writer first locks the ledger head, then reads everything influencing its decision. A shared ledger
envelope sequences identity, replay, admission, proposal, projection and receipt construction; case and
authority policies supply their own decisions. It persists one resulting append. Rejection changes no accepted ledger or projection state.

The database instant is captured after acquiring the lock. Together with the installation zone it supplies
the business date. Lock, statement and idle-transaction timeouts bound serialization. Reads use REPEATABLE READ.
See [Transactions](../src/ClaimCore.Postgres/Ledger/Transactions.fs) and the [store port](../src/ClaimCore.Application/LedgerStorePort.fs).

<a id="cc-app-002"></a>

### CC-APP-002 — Exact replay and commit uncertainty

Identity combines a caller-chosen operation ID with canonical request content: amounts compare by decimal
value, references after ASCII case folding, and free text exactly. Authorized equivalent replay returns
original acceptance metadata with current snapshot disclosure. The ledger retains authored request bytes;
public history exposes receipts and canonical snapshots. Changed content under a used ID refuses. Erased
content cannot be replayed.

`OUTCOME_UNKNOWN` means confirmation was lost after commit could have started. Preserve the exact request,
ID and revision. A later ordinary refusal, fault or NOT_STARTED response cannot disprove the earlier commit.
Observation finds accepted metadata but cannot prove equality with submitted content.

`operation.resolve` takes the head lock and returns the author's accepted receipt or installs a permanent
actor/operation fence and returns `NOT_ACCEPTED`. Execute checks the fence after replay/conflict lookup and
refuses `OPERATION_RESOLVED`. A foreign ID reveals no acceptance and cannot fence its author. Fences contain
no payload and remain indefinitely. Preview is advisory and writes nothing.

Predispatch cancellation is definite failure. Lost commit confirmation remains unknown; confirmed server
errors keep their established abort/integrity/schema classification. Provider failures are availability
faults; programming failures are `CORE_INTERNAL_ERROR`. [Resolution](../src/ClaimCore.Application/Resolution.fs)
and [Core](../src/ClaimCore.Application/Core.fs) own these decisions.

<a id="cc-cat-001"></a>

### CC-CAT-001 — Consumer vocabulary and presentation ownership

Contract owns ordered vocabulary, field identities, codecs and message keys. Domain owns admission and
transitions; Application owns authorization. The three [source locale files](../src/ClaimCore.Contract/Localization/)
are the sole message source. CLI and browser render codes/parameters locally. Language and display formats
are separate preferences and never rewrite business strings.

The catalog carries labels, order, scalar hints and operation bodies. Its derivation walks each body to
produce request paths and tagged branches; browser prefill paths come from the field table's groups.
The generator admits source text with the single Contract Template grammar, validates key/argument parity
and producer references, and emits typed message arguments. Both clients preserve authored values. Contract's display-warning policy identifies suspicious Unicode
without changing raw values. [Development](development.md#generated-contracts-and-boundary-verification)
explains freshness and independent contract checks.
