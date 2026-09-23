# PostgreSQL storage and administration

PostgreSQL owns durable transactions and structural integrity. Domain and Application remain the
authority for claims decisions. `ClaimCore.Database` is the schema-owner administration executable;
normal case work uses Web or CLI with a separate runtime credential.

## Database executable

The generated block below is synchronized with the compiled program. `help`, `version`, and
`describe diagnostics` are database-free. Initialization, verification and pruning require the
private file selected by `CLAIMCORE_ADMIN_CONNECTION_FILE`.

<!-- generated:begin database-help -->
```text
ClaimCore.Database 0.5.0 — schema and recovery-retention administration
  ClaimCore.Database initialize <canonical-IANA-ID>
  ClaimCore.Database verify
  ClaimCore.Database prune [--dry-run] [--settled-retention-days <1-3650>]
                           [--abandoned-retention-days <1-3650>] [--limit <1-1000>]
  ClaimCore.Database describe diagnostics
  ClaimCore.Database help
  ClaimCore.Database version [--json]
Prune defaults: accepted 30 days; revoked 30 days; batch limit 100; deletion enabled.
Set CLAIMCORE_ADMIN_CONNECTION_FILE to an owner-private schema-owner connection file.
```
<!-- generated:end database-help -->

<a id="cc-db-002"></a>
### CC-DB-002 — Schema-owner credential admission precedes database access

The admin connection file must have an absolute canonical path to an owner-private regular UTF-8 file
no larger than 8,192 bytes, with no linked leaf or ancestor. The shared private-file service verifies
its opened file handle and rejects unsafe mode, extended ACLs, links, invalid UTF-8, and oversize
before database access; diagnostics disclose neither the path nor file bytes. This runtime path
supports macOS and Linux and fails closed on Windows. Database-free discovery remains available on
every build host. The file contains an Npgsql connection string for the schema owner and must never
be passed to CLI or Web as their runtime credential. Startup-option overrides and application-role
credentials are refused by owner administration; no credential is elevated through `SET ROLE`.

Runtime connection admission checks the supported server, durability/session settings, confined
application identity, exact current baseline identity, required structural checks and least-privilege
ACLs. Development and CI use the digest-pinned image in
[`db/postgresql-baseline.json`](../db/postgresql-baseline.json). Unsupported old storage is classified
before queries assume the current relation layout.

## Administration results and delivery

`describe diagnostics` publishes the exact response schema and its fingerprint without configuration
or database access. Results distinguish `NOT_STARTED`, `NOT_COMMITTED`, `COMPLETION_UNKNOWN`,
`COMPLETED` and `COMPLETED_CLEANUP_FAILED`. An unconfirmed commit exits 4 and requires reconciliation,
not an inferred rollback or automatic mutation retry. Definite admission/action failures exit 3.
Use read-only `verify` after reconnecting to inspect installation readiness; verification is not a
historical operation receipt and cannot prove which caller created an installation.

Output failure after confirmed administration does not relabel the database action as uncommitted.
It emits one bounded stderr delivery diagnostic and returns a nonzero exit. Terminal preparation
counts and byte totals are exact decimal strings. Native callers handle `AdministrationOutcome`
rather than assuming an exception or returned unit expresses every completion state.

## Stored data

`cases` retains exactly thirteen business columns plus technical revision. `case_changes` retains
append-only accepted-operation receipts, request fingerprints and snapshots; exact accepted replay
does not depend on optional preparation retention. These accepted facts remain independent of
technical attempt evidence.

`request_preparations` stores exact format-3 canonical request bytes and digest, application version,
preparing fingerprint/kind and timestamp. `SEMANTIC_CORE_V1` denotes a semantic preparation;
`CANONICAL_RECORD_V3` denotes validation and retention of an unbound current canonical record, not an
invented original producer. An exact-byte replay preserves the first retained metadata. The
append-only lifecycle records submission start only. Actual identified attempts and their independent
definite settlements remain available through bounded operation-specific inspection.

`operation_revocations` contains durable operation ID, format, digest, timestamp and
`OPERATOR_DISMISSAL` reason. It deliberately has no foreign key to the optional preparation; deleting
a terminal preparation cannot resurrect execution authority. `installation_lineage` contains one
nonempty installation UUID and its mandatory business time zone. `schema_baseline` contains one
baseline identity/digest and installation audit metadata. `request_preparation_prunes` is owner-only
maintenance audit. No old migration ledger, unidentified-start table, legacy provenance or legacy
dismissal reason is part of this installation.

Scalar bounds, exact numeric precision, complete decision tuples, chronology, payment prerequisites,
keys and references remain enforced in the final CREATE definitions. These are defense in depth,
not a replacement for the Domain state machine. Runtime credentials permit some direct SQL and
therefore belong only to trusted infrastructure.

## Fresh installation boundary

<a id="cc-db-001"></a>
### CC-DB-001 — Atomic fresh baseline and non-destructive refusal

[`db/baseline.sql`](../db/baseline.sql) is one direct final-state schema definition, embedded with the
frozen identity and SHA-256 digest in [`db/schema-baseline.json`](../db/schema-baseline.json).
`initialize <canonical-IANA-ID>` validates an explicitly chosen calendar before connecting. Under a
transaction-scoped schema lock it inspects the namespace before executing DDL. Only an absent
`claimcore` namespace may be created. Schema, grants, baseline marker, new lineage UUID and non-null
calendar commit in one transaction. A pre-commit DDL failure rolls all of them back; loss of commit
confirmation remains explicitly unknown.

A repeated initialization of the exact current baseline with the identical calendar validates it
without rewriting any marker, lineage, timestamp, data or provenance. Concurrent initializers
serialize before classification. A different calendar is refused, not silently substituted.
`verify` runs current identity/structure/calendar checks in a read-only transaction; it cannot
initialize, repair or upgrade. Required runtime-role ACLs are additionally checked by the runtime.

Every pre-existing unsupported `claimcore` namespace is refused untouched, including an empty
namespace, a partial installation, old migration-ledger schemas 001–006, mixed old/current metadata,
unknown baseline IDs, altered digests and malformed marker relations. A marker alone does not skip
current structural admission. Neither initialization nor verification deletes or adopts data.
Old `migrate` and `set-business-zone` invocations are unsupported, not aliases. There are no ordered
migration files, migration-prefix engine, compatibility views, backfills or automatic conversions.

For an old installation, stop attempting to open it with this build. Retain its database, volumes,
backups and recovery files under the compatible old software and the operator's retention policy.
Provision a **separate** database for this fresh baseline and explicitly choose its business calendar.
No supported import of old database contents or old recovery artifacts is provided. Do not copy an
old lineage, relabel a marker, rewrite artifact versions or create new operation IDs to get past
refusal; those actions do not transfer accepted/revoked authority and can duplicate effects. Any
future data transition requires a separate reviewed operator-led plan, not a shim in this package.

The baseline digest identifies a frozen installation contract, not the application release number.
Changing it must deliberately define another supported/refused installation boundary; it is not a
license to edit an existing database or silently advance its marker. Keep readers, writers,
constraints, generated contracts, tests and documentation coherent.

## Preparation retention

Pruning is an explicit owner operation, never an initialization side effect. Eligible preparations
must already be terminal through an accepted receipt or durable revocation **and have no unsettled
identified attempt**. A later accepted retry does not erase an earlier attempt's uncertainty.
Definite rejection alone does not close authority: the request may become valid after later state
or business-date changes. Pending, rejected-only and unsettled evidence is never inferred away.

`--dry-run` audits candidate count without deleting candidates. `--settled-retention-days` and
`--abandoned-retention-days` accept 1–3650 days, defaulting to 30; `--limit` accepts 1–1000, defaulting
to 100. Pruning requires current-baseline admission and schema ownership, serializes maintenance,
and audits parameters/counts. Review a dry run and the operator's retention requirements before
explicit deletion. It never deletes a case, accepted history, independent revocation or lineage.
An accepted receipt continues to support exact idempotent replay after technical pruning; a revoked
operation retains only its compact tombstone when its preparation is pruned, not exportable bytes.

## Installation business time zone

The initializer takes the mandatory canonical IANA identifier and stores it atomically with lineage.
There is no post-installation setter, implicit UTC default or host-local fallback. Every description,
preview and execution captures one UTC instant and derives its effective business date from this
stored zone. `Etc/UTC` is the explicit portable choice used by synthetic tests; operators choose the
calendar governing their installation. Runtime opening refuses a zone unavailable on its host.
Host tzdata remains part of the operational environment; see [operations](operations.md).

## Local development database

[Getting started](getting-started.md) owns the source-checkout Compose sequence and private connection
file setup. [`.env.example`](../.env.example) declares its required passwords and optional per-checkout
project and host-port settings. The selected `CLAIMCORE_POSTGRES_PORT` must match both private
connection files.

The Compose dependency is a separately published PostgreSQL image, not a packaged ClaimCore
application. Its [recipe](../db/Dockerfile.postgres-patched) pins the official PostgreSQL base and
exact package upgrades from the signed [Debian](https://snapshot.debian.org/archive/debian/20260913T022727Z/)
and [Debian security](https://snapshot.debian.org/archive/debian-security/20260913T022727Z/)
snapshots, where corresponding source packages can be obtained. The image retains the Docker-library
PostgreSQL [LICENSE](../db/postgres-upstream/LICENSE) and [AUTHORS](../db/postgres-upstream/AUTHORS)
at `/usr/share/doc/docker-library-postgres/`; PostgreSQL and Debian package copyright notices remain
under `/usr/share/doc/`. The image's per-architecture SBOMs and provenance are attested to its
published digests; use the exact digest in [`db/postgresql-baseline.json`](../db/postgresql-baseline.json).

Compose is for local synthetic development only. Passwords initialize a new volume; changing `.env`
does not rotate roles in a retained database. The authenticated health check remains unhealthy when
the retained roles and new values disagree. Restore the matching private configuration or perform an
administrator-led credential rotation—do not delete an adopted volume to clear a health failure.

Integration tests create isolated Testcontainers instances, initialize fresh baselines themselves, and never
reuse the persistent developer database or repository connection files.

## Operational limits

An installed checksum identifies the baseline source that was recorded; it does not attest every live DDL
object against administrator tampering. ClaimCore has no automatic repair, downgrade, backup, or
restore implementation. The composed application runtime owns one `NpgsqlDataSource` shared by its
private claim and recovery stores; opening a second product process creates a separate runtime and
connection pool. See [Security and operations](operations.md) before considering real data.
