# Database administration

Database is the separate owner executable; it serves no case work. Use its `help` command for invocation
syntax. Its commands are initialize, provision-owner, verify, head, help and version. Discovery opens
no database.

<a id="cc-db-002"></a>

### CC-DB-002 — Private owner inputs precede database access

The two settings are `CLAIMCORE_ADMIN_CONNECTION_FILE` and `CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE`. Private
inputs use the managed [private-input policy](operations.md#private-configuration) on Linux and macOS.
Admission finishes before a connection is opened. Unsupported platforms and inadmissible files refuse; no
native library or C compiler is required.

The principal file has exactly `operationId`, `issuer` and `subject`; `operationId` is a stable nonzero
canonical UUID. Provisioning validates one human principal through Application and is exact-operation
idempotent. Keep that operation identity for a retry. No secret or connection string belongs in argv or
output. Remote database connections require verified TLS, a qualified PostgreSQL major and minimum minor
listed in [server support](../db/postgresql-baseline.json), and appropriate owner privileges. Runtime
credentials are separately admitted with no schema-owner authority.

<a id="cc-db-001"></a>

### CC-DB-001 — Atomic initialization and exact baseline admission

[baseline.sql](../db/baseline.sql) is the only supported schema source. Initialization atomically installs
its checksum-bound identity and immutable canonical IANA business zone. Initializers lock before namespace
discovery. Repeated initialization requires the same complete identity and installation tuple, including zone;
runtime admission, readiness, verification, head and owner provisioning share that admission. The digest
identifies source, not proof that a trusted owner left the live schema unchanged. Unknown, partial, historical
or incompatible namespaces refuse untouched. Keep existing installations with matching software and initialize
a separate fresh installation; there is no migrate command, script history, adoption, reset or conversion.

Provision-owner bootstraps only an empty authority with its stable operation ID. Database outputs are
structured tags and positions. Exit 0 succeeds; 2 refuses invocation or input; 3 fails; 4 reports lost commit
knowledge; 5 reports verification divergence. A lost output after a durable operation remains uncertain.
Inspection of the primary alone cannot prove freshness against an independent checkpoint.

Verification reconstructs chain hashes, payload commitments, revisions and projections, checks retained/erased
payload shape, and validates permanent authority-history invariants. Success prints `VERIFIED` with the ledger
position. Divergence prints `DIVERGED` with `sequence` (an integer or null) and a stable `finding`; database
I/O failures remain `FAILED`. `verify --expect` checks an externally retained sequence/hash checkpoint. It
detects an older restore behind that checkpoint; it does not prove that the checkpoint is itself the newest.
See [Operations](operations.md).

The supported-major list and enforced minimum minor are owned by
[postgresql-baseline.json](../db/postgresql-baseline.json): currently PostgreSQL 18.6 or later within major
18, tested with its pinned image. Server admission enforces the declared major and minimum minor. Persisted
text/date/money guards mirror format, never current admission policy. Dates/instants are finite and in years
0001–9999. Reference identity uses ASCII folding with C-collation semantics.

Verification computes the earliest second occurrence of an operation ID in the same REPEATABLE READ snapshot
as the scan, checking it after that row's link/hash/commitment/payload checks and before history. Earlier
corruption wins; projections, head and expected checkpoint follow. PostgreSQL may sort/spill for the prepass;
this does not establish constant total-system memory. The typed finding identifies the violated property;
global findings have sequence null. Permanent bootstrap, enabled-executor, human-target and last-owner
invariants are verified without reapplying current capability policy. Database help owns invocation and its
structured output carries findings. See [Operations](operations.md) for the trust and erasure limits.

The ledger rejects inserts/updates/deletes outside its append and case-payload erasure paths. The trusted
owner payload-clear exception and verification limits are described in
[Operations](operations.md#erasure-limits). `pg_restore --data-only` into an initialized schema is unsupported
because append-only guards reject reconstruction; use ordinary full restore into an empty database under the
operator’s procedure, then verify independently retained evidence.
