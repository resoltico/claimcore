# Database administration

Database is the separate owner executable; it serves no case work. Use its `help` command
for invocation syntax. Its commands are initialize, migrate, provision-owner, verify, head,
help and version. Discovery opens no database.

<a id="cc-db-002"></a>
### CC-DB-002 — Private owner inputs precede database access

The two settings are CLAIMCORE_ADMIN_CONNECTION_FILE and CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE.
Connections and owner identity are owner-private regular files. Unsupported platforms,
symlinks, foreign ownership, group/other permissions, oversized or malformed files are refused.
The principal file has exactly operationId, issuer and subject; operationId is a stable nonzero canonical
UUID. Provisioning validates one human principal through Application and is exact-operation idempotent.
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
Migrations are numbered forward-only scripts; applied bytes are immutable. Opening requires
history to be exactly the checksum-bound prefix of the shipped scripts.

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
