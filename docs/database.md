# Database administration

Database is the separate owner executable; it serves no case work. Use its `help` command
for invocation syntax. Its commands are initialize, migrate, provision-owner, verify, head,
help and version. Discovery opens no database.

<a id="cc-db-002"></a>

### CC-DB-002 — Private owner inputs precede database access

The two settings are `CLAIMCORE_ADMIN_CONNECTION_FILE` and `CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE`.
Private inputs use managed HostSecurity admission on Linux and macOS. Paths must be absolute,
valid UTF-8 and contain no NUL. Symbolic links are followed, including secret-volume links.
Both the path mode before opening and the opened handle's mode must forbid all other-user
access and group write/execute; group read is allowed (for example `0600`, `0640` or `0440`).
The open runs on a worker with a two-second bound; a blocked open is refused as not regular.
The opened handle must be seekable, its length must fit the caller's bound, and the bytes are
read from that same handle. Callers clear the returned buffer after use.

There is no ownership or ACL inspection. Filesystem mode semantics and the host administrator
are trusted; admission does not freeze concurrent changes to file content or permissions.
Unsupported platforms, directories, missing files, malformed paths and oversized files refuse.
No native library or C compiler is required.

The principal file has exactly `operationId`, `issuer` and `subject`; `operationId` is a stable
nonzero canonical UUID. Provisioning validates one human principal through Application and is
exact-operation idempotent.
Keep that operation identity for a retry. No secret or connection string belongs in argv or output.
Remote database connections require verified TLS, a qualified PostgreSQL major and minimum minor listed in
[server support](../db/postgresql-baseline.json), and appropriate
owner privileges. Runtime credentials are separately admitted with no schema-owner authority.

<a id="cc-db-001"></a>

### CC-DB-001 — Atomic initialization and ordered migrations

[0001_baseline.sql](../db/migrations/0001_baseline.sql) is the current schema source.
Initialization atomically installs the baseline and immutable canonical IANA business zone.
Repeated initialization requires the same zone. Unsupported namespaces or migration histories
are refused untouched. There is no adoption, reset or conversion of existing installations.
Migrations are numbered forward-only scripts, subject to the [baseline compatibility policy](architecture.md#durable-records).
Opening requires history to be exactly the checksum-bound prefix of the shipped scripts.

Provision-owner bootstraps only an empty authority with its stable operation ID. Database outputs are
structured tags and positions. Exit 0 succeeds; 2 refuses invocation or input; 3 fails; 4 reports lost commit knowledge;
5 reports verification divergence.
A lost output after a durable operation remains uncertain. Inspection of the primary alone
cannot prove freshness against an independent checkpoint.

Verification reconstructs chain hashes, payload commitments, revisions and projections,
checks retained/erased payload shape, and validates permanent authority-history invariants.
Success prints `VERIFIED` with the ledger position. Divergence prints `DIVERGED` with
`sequence` (an integer or null) and a stable `finding`; database I/O failures remain `FAILED`. `verify --expect` checks
an externally retained sequence/hash checkpoint. It detects an older restore behind that
checkpoint; it does not prove that the checkpoint is itself the newest. See [Operations](operations.md).

The supported-major list and enforced minimum minor are owned by
[postgresql-baseline.json](../db/postgresql-baseline.json): currently PostgreSQL 18.6 or later
within major 18, tested with its pinned image. Server admission enforces the declared major and
minimum minor. Persisted text/date/money guards mirror format, never current admission policy. Dates/instants are finite
and in years 0001–9999. Reference identity uses ASCII folding with C-collation semantics.

Verification chooses its first divergence deterministically: scan ledger entries in sequence
order, validate metadata/link/hash/commitment/payload and permanent case/authority history, then
compare case and authority projections, head and expected checkpoint. The typed findings are
`LINK`, `ENTRY_HASH`, `COMMITMENT`, `PAYLOAD_FORMAT`, `REVISION_SEQUENCE`, `HISTORY_START`,
`DISPOSITION_SEQUENCE`, `ERASURE_INCOMPLETE`, `PAYLOAD_WITHOUT_ERASURE`, `AUTHORITY_FOLD`,
`ACTOR_NOT_ENABLED`, `HUMAN_REQUIRED`, `OWNER_REQUIRED`, `CASE_PROJECTION`, `AUTHORITY_PROJECTION`,
`HEAD` and `EXPECTED_POSITION`. The sequence is null for a global finding. I/O is a failure, never
reported as divergence. Verification preserves permanent bootstrap, enabled executor, human-only
target and last-owner invariants without reapplying the current role/capability matrix.

The ledger rejects inserts/updates/deletes outside its append and case-payload erasure paths.
The trusted owner payload-clear exception and verification limits are described in
[Operations](operations.md#erasure-limits). `pg_restore --data-only` into an initialized schema
is unsupported because append-only guards reject reconstruction; use ordinary full restore into
an empty database under the operator’s procedure, then verify independently retained evidence.
