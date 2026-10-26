# Database administration

Use `claimcore-database` for owner-only initialization, provisioning, verification and head inspection.
It serves no case work. Offline `help` lists commands and exit codes; `version` reports build identity.
Neither command opens a database.

| Invocation | Purpose |
| --- | --- |
| `claimcore-database initialize ZONE` | Initialize the supported schema with a canonical IANA business time zone. |
| `claimcore-database provision-owner` | Bootstrap one human owner from the private owner-principal input. |
| `claimcore-database head` | Read the admitted installation's current ledger position. |
| `claimcore-database verify` | Verify the installation's retained ledger and projections. |
| `claimcore-database verify --expect SEQUENCE:ENTRY_HASH` | Also compare an independently retained checkpoint. |

Checkpoint sequences use canonical nonnegative decimal notation within the protocol's safe-integer range;
entry hashes use 64 lowercase hexadecimal characters. Verification requires that sequence/hash position to
belong to the verified chain, including its empty genesis position; the ledger may extend beyond the checkpoint.

<a id="cc-db-002"></a>

### CC-DB-002 — Private owner inputs precede database access

CLAIMCORE_ADMIN_CONNECTION_FILE supplies the owner connection. Provisioning also uses
CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE, with exactly operationId, issuer and subject. Keep a stable nonzero
canonical operation ID: provisioning an owner is an idempotent accepted operation for one human principal.

[Private input admission](operations.md#private-configuration) completes before connection. Remote database
connections require verified TLS and appropriate owner privileges. Runtime credentials are separate and have
no schema-owner authority. Keep credentials out of argv, output and reports.

<a id="cc-db-001"></a>

### CC-DB-001 — Atomic initialization and exact baseline admission

[baseline.sql](../db/baseline.sql) is the only supported schema. Initialization locks before namespace
discovery and atomically installs its checksum-bound identity and immutable canonical IANA business zone.
Repetition requires the same complete installation identity and zone. Runtime admission, readiness,
provisioning, head and verification use the same baseline admission.

Unknown, partial or incompatible namespaces refuse untouched. Initialize a separate fresh installation and
preserve older installations with matching software. There is no in-place conversion. A source digest identifies
the shipped baseline; it cannot prove that a trusted owner left a live schema unchanged.

Supported PostgreSQL majors, minimum minors and qualified image pins live in
[postgresql-baseline.json](../db/postgresql-baseline.json). Server admission enforces that declaration. SQL
scalar guards protect permanent formats, independently of current admission policy. Reference identity uses
ASCII folding with C-collation semantics.

Provision-owner bootstraps only empty authority. Outputs are structured `kind` outcomes and shared positions (`sequence`, `entryHash`). `REJECTED` denotes refusal. Exit 0 succeeds;
2 refuses invocation/input; 3 fails; 4 reports lost commit knowledge; 5 reports verification divergence.
Lost output after durable work remains uncertain. The command table above gives the supported syntax.

## Verify a database or restored copy

`verify` reconstructs chain hashes, commitments, revisions and projections. It checks retained/erased payload
shape and permanent authority-history invariants without reapplying current capability policy. Success prints
VERIFIED with position. Divergence prints DIVERGED with a stable finding and sequence (or null for a global
finding). Provider/I/O failures remain FAILED.

`verify --expect` compares an independently retained sequence/hash checkpoint. This detects a restore behind
that checkpoint; it cannot prove the checkpoint is newest. Keep checkpoints outside the primary database and
follow the [restore procedure](operations.md#backups-and-restore) before reopening access.

Verification streams entries and folds case/actor/grant state. A same-snapshot SQL prepass finds the earliest
repeated operation ID. Row link/hash/commitment/payload findings precede duplicate/history findings; projection,
head and checkpoint checks follow. The prepass may sort/spill, and folded resource memory grows with cardinality.
The [verification implementation](../src/ClaimCore.Application/Verification/) owns finding semantics.

Append-only guards permit accepted append and case-payload clearing. They cannot protect against a hostile
trusted owner; [erasure limits](operations.md#erasure-limits) explain the exception. A data-only restore into an
initialized schema is unsupported because these guards reject reconstruction. Use a full restore into an empty
database under the operator's procedure, then verify independent evidence.

See the [glossary](glossary.md) for project terminology.
