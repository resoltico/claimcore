# ClaimCore fresh-baseline package: pre-implementation design and QA

Reviewed baseline: main ad45a9c649d14f5cb592d772c56b043c1b093ba2.
Scope: replace installed-schema evolution and historical recovery compatibility with an explicit fresh installation boundary. No release, merge, data conversion or operator-data deletion.

## Design pass

1. One direct final-state SQL baseline and one embedded frozen identity/digest definition replace the ordered migration catalog, ledger, upgrades, ALTER/backfill sequence and legacy fixtures. The final DDL preserves all thirteen business columns, exact values, accepted-history invariants, attempt/settlement evidence, durable revocations, retained-request bounds, indexes and least-privilege ACLs.
2. `ClaimCore.Database initialize <canonical-IANA-ID>` validates the explicit zone before any database access. Under the existing transaction-scoped schema advisory lock, it classifies the namespace before DDL. A missing ClaimCore namespace can be created; any existing historical, partial, empty, newer or incompatible namespace is refused without adopting, altering or deleting it. An exact current baseline with the same zone is validated and returned unchanged. A different zone is refused. New schema, marker, lineage UUID and non-null immutable zone commit together. `verify` is owner-only, read-only validation for reconciliation, never repair. Old `migrate` and `set-business-zone` commands are not aliases.
3. Runtime connection admission requires the new identity before accessing current-only ACLs or data. It also retains structural, server, durability, role and ACL checks. A new marker cannot authorize an old ledger or legacy-only objects. Metadata is not an assertion that a hostile database owner cannot tamper with arbitrary DDL; the owner remains trusted infrastructure.
4. Canonical command-record format 3 uses an explicit canonicalCommandFormat field; recovery-envelope format 2 uses that identity. Old record/envelope versions and shapes are refused without conversion. SHA-256 fingerprint algorithm version 1 and snapshot version 2 remain current independent formats. Transport CLI-v3/Web-v2 framing stays unchanged; generated contracts and fingerprints change together where their payloads change.
5. Remove legacy provenance and dismissal reasons, pre-attempt uncertainty storage and its wire/UI flag. Keep real append-only attempts, independent settlements, acceptance observation, uncertain-completion states, exact-byte/idempotent retries and compact revocation tombstones. Raw current canonical imports have explicit CANONICAL_RECORD_V3 retention provenance; this describes current validation/retention, not a fabricated original producer. An exact replay preserves existing first-producer metadata.
6. Pruning remains explicit owner administration of terminal technical preparations, never an initialization step. Accepted receipts and independent revocations outlive optional preparation rows. Pending/unsettled evidence must not be silently lost: terminal pruning must also exclude preparations with unsettled identified attempts. No case, accepted history, lineage or revocation deletion is introduced.
7. Tests replace historical positive upgrades with fresh creation, exact repeated and concurrent initialization, refusal without mutation, corrupt/newer/partial marker cases, DDL rollback and immutable-zone coverage. Recovery tests prove strict old-format refusal, exact current transfer and preserved uncertainty/idempotency/revocation. Current UI, native, database and contract qualification remains mandatory. Immutable historical test-baseline entries are connected to stronger replacement evidence, not erased.

## Separate design QA pass

- Rejected simply concatenating migrations: final CREATE definitions must contain no legacy backfill, old provenance, nullable zone or transitional constraints.
- Rejected missing-ledger-as-empty logic: existence of the namespace is the guard. Empty or partially initialized ClaimCore schemas are unsupported and untouched.
- Rejected a post-initialization zone setter: it leaves a half-configured installation and permits a misleading success boundary. Zone and lineage are atomic.
- Corrected runtime ordering: unsupported installations must be classified before ACL queries assume new tables, with safe typed diagnostics and no provider payload leakage.
- Rejected marking unbound imports as semantic-core-produced: use explicit current import provenance. Existing exact replay is still independent of a later producer's metadata.
- Removed no uncertainty guarantee: old unidentified-start markers disappear only because old installations are refused. Unsettled current attempts stay unsettled even after a later accepted retry. Pruning must retain their evidence; acceptance and uncertainty are independent.
- Identified an unused physical DISMISSED lifecycle writer. Remove that historical persistence path, but preserve current in-memory dismissal projection from durable revocation, including pruned tombstones and races.
- Rejected treating SQL failure, cancellation or lost commit confirmation as proof of non-commit. Existing AdministrationExecution commit-boundary classification and bounded output-failure behavior remain authoritative.
- Races are serialized before inspecting or creating the schema. The same-zone repeat performs validation without rewriting marker timestamps, lineage, provenance, receipts or request bytes.
- No fabricated successful execution evidence: report local focused/full gates and remote full CI independently. No coverage, assertion, compatibility or security gate is weakened to obtain a green result.

Decision: proceed with implementation of this corrected design. This decision record preceded product implementation. Source checkout synchronization and tool extraction are preparation, not implementation.

## Implementation QA note: evidence producer identity

The immutable v0.1 assurance registry binds the existing `ClaimCore.MigrationQualificationTests`
producer name. That test-only identity is retained rather than weakening the registry or adding an
alias. Its implementation and required `fresh-baseline-qualification` stage now execute only direct
baseline creation/refusal tests; no historical migration engine, upgrade SQL or compatibility path is
retained. Individual historical assertions remain explicitly connected to their reviewed replacements.
