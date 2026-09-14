# PostgreSQL storage and administration

PostgreSQL owns durable transactions and structural integrity. Domain and Application remain the
authority for claims decisions. `ClaimCore.Database` is the schema-owner administration executable;
normal case work uses Web or CLI with a separate runtime credential.

## Database executable

The generated block below is synchronized with the compiled program. `help` and `version` do not
open PostgreSQL; mutating administration commands require the private file selected by
`CLAIMCORE_ADMIN_CONNECTION_FILE`.

<!-- generated:begin database-help -->
```text
ClaimCore.Database 0.1.0 — schema and recovery-retention administration
  ClaimCore.Database migrate
  ClaimCore.Database prune [--dry-run] [--settled-retention-days <1-3650>]
                           [--abandoned-retention-days <1-3650>] [--limit <1-1000>]
  ClaimCore.Database help
  ClaimCore.Database version [--json]
Prune defaults: settled 30 days; dismissed 30 days; batch limit 100; deletion enabled.
Set CLAIMCORE_ADMIN_CONNECTION_FILE to an owner-private schema-owner connection file.
```
<!-- generated:end database-help -->

<a id="cc-db-002"></a>
### CC-DB-002 — Schema-owner credential admission precedes database access

The admin connection file must have an absolute canonical path to an owner-private regular UTF-8 file
no larger than 8,192 bytes, with no linked leaf or ancestor. The shared private-file service verifies
its opened file handle and rejects unsafe mode, extended ACLs, links, invalid UTF-8, and oversize
before a database connection is attempted; failure diagnostics disclose neither the path nor file
bytes. This runtime path supports macOS and Linux and fails closed on Windows. Database-free `help`
and `version` remain available on every build host. The file contains an Npgsql connection string for
the schema owner and must never be passed to CLI or Web as their runtime credential.

The runtime validates the supported server range, required durability and session settings, runtime
role, and exact installed migration manifest when opening a connection. Development and CI use the
digest-pinned image selected by [`db/postgresql-baseline.json`](../db/postgresql-baseline.json);
executable configuration and tests own exact compatibility policy.

## Stored data

- `claimcore.cases` contains exactly the thirteen business columns plus technical revision.
- `claimcore.case_changes` contains immutable accepted-operation receipts, request fingerprints, and
  historical snapshots. Accepted replay does not depend on retained technical preparations.
- `claimcore.request_preparations`, its append-only lifecycle, and `installation_lineage` retain
  bounded technical material for exact-request recovery; they are not claim state or history.
- Each preparation stores canonical command-record format, exact request digest and bytes, application
  version, and generalized preparation provenance. Provenance is audit context, not operation
  identity; an exact canonical-byte replay preserves the first producer's version, fingerprint,
  provenance kind, and preparation timestamp.
- Submission-attempt and settlement tables retain append-only technical evidence for each recovery
  execution attempt. Detailed inspection reads the actual attempt IDs, start times, definite
  settlements, and an independent marker preserving uncertainty inherited from the pre-003 schema.
  A bounded list omits this detail and authored request values.
- `claimcore.schema_migrations` records each ordered migration's version, name, and SHA-256 digest.
- `claimcore.request_preparation_prunes` records every preparation-pruning attempt and its bounded
  outcome.

Revision is storage metadata, not part of `CaseFields`. Constraints enforce representable scalar
bounds, decision-tuple completeness, chronology, payment prerequisites, keys, and references. They
are defense in depth and do not replace the domain state machine.

The runtime role has the minimum data privileges needed by the application. Schema ownership and
administration use a separate credential. Runtime credentials still permit some direct SQL and
therefore belong only to trusted infrastructure.

## Migration policy

<a id="cc-db-001"></a>
### CC-DB-001 — Ordered migration integrity and supported upgrade

Migration files under `db/` are ordered, append-only, embedded in the PostgreSQL assembly, and bound
to the frozen checksums in `db/migration-manifest.json`. `ClaimCore.Database` bootstraps the ledger,
applies pending files in order in a transaction under a database lock, and refuses source or recorded
drift.

The currently supported paths apply a fresh database through 005 and upgrade an installed 001, 002,
003, or 004 schema sequentially to 005. They preserve adopted cases, accepted history, canonical
request bytes, preparations, attempts, settlements, legacy uncertainty, installation lineage, and
the exact 001–004 migration bytes. A failed migration rolls back atomically and recorded digests remain
exact.

Migration 002 added bounded request preparations. Migration 003 added per-submission attempts and
definite technical settlements while conservatively retaining uncertainty from starts without attempt
identities. Migration 004 is a hard provenance generalization: it renames `protocol_version` to
`canonical_request_format`, renames `web_contract_fingerprint` to
`preparing_contract_fingerprint`, and adds non-null `preparing_contract_kind`. Existing fingerprint
bytes are preserved exactly and marked `LEGACY_UNCLASSIFIED`; new semantic preparations use
`SEMANTIC_CORE_V1`. The change does not rewrite canonical request bytes, receipts, lineage, attempts,
or settlements.

Migration 005 grants the confined runtime `SELECT` on the pre-003 uncertainty marker. It changes no
row or canonical byte. Current recovery inspection reads that marker separately from producer
provenance and reads real append-only attempt and settlement rows; a later definite attempt does not
erase uncertainty inherited from an earlier unidentified start.

The upgrade through 005 requires planned downtime. Stop Web and CLI sessions, back up the
installation, apply the ordered migrations with the schema-owner Database executable, then start
current applications. There is no downgrade, compatibility view, or migration that converts legacy
provenance into semantic provenance.

- Never edit, reorder, or replace an applied migration.
- Add a new migration for every schema change.
- Update readers, writers, constraints, generated transport contracts, tests, and documentation together.
- Test both a fresh database and an upgrade from the previous supported schema state.
- Never delete or recreate adopted data merely to make a migration pass.

`migrate` verifies already-installed entries and applies each pending PostgreSQL schema migration in
order. It has no automatic downgrade, source-drift repair, or data-import path.

## Preparation retention

`prune` removes only old technical request preparations in one of three proved states: the operation
has an accepted receipt; the unsubmitted preparation was explicitly dismissed; or a post-003
submission has at least one identified attempt and every attempt has a definite technical settlement.
A start inherited from the pre-attempt schema and a legacy provenance marker remain conservative
recovery evidence; neither authorizes inference that an operation did not commit. The command never
deletes a case, accepted case history, canonical request bytes needed by a retained preparation, or an
unsettled submission.

- `--dry-run` records candidate count without deleting candidates.
- `--settled-retention-days` and `--abandoned-retention-days` accept 1–3650 days and default to 30.
- `--limit` accepts 1–1000 candidates and defaults to 100.

The command requires a current schema and schema-owner identity, serializes pruning with a database
lock, and records its parameters, candidate count, and deleted count in the owner-only prune journal.
Run a dry pass and review operational retention requirements before deletion.
Pruning an accepted operation's technical preparation does not remove its accepted `case_changes`
receipt. Exact accepted replay uses that receipt and its stored request fingerprint, not preparation
retention.

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

Integration tests create isolated Testcontainers instances, apply migrations themselves, and never
reuse the persistent developer database or repository connection files.

## Operational limits

An installed checksum proves which migration source was recorded; it does not attest every live DDL
object against administrator tampering. ClaimCore has no automatic repair, downgrade, backup, or
restore implementation. The composed application runtime owns one `NpgsqlDataSource` shared by its
private claim and recovery stores; opening a second product process creates a separate runtime and
connection pool. See [Security and operations](operations.md) before considering real data.
