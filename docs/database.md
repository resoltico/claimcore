# PostgreSQL storage and administration

PostgreSQL owns durable transactions and structural integrity. Domain and Application remain the authority for claims decisions. `ClaimCore.Database` is the owner-only administration executable; `ClaimCore.Web` alone composes ordinary case work with separately credentialed primary and witness stores. The browser and CLI call its authenticated HTTPS API and receive no database credential.

## Database executable

The generated block below is synchronized with the compiled program. `help`, `version`, and
`describe diagnostics` are database-free. Initialization, verification and pruning require the
private file selected by `CLAIMCORE_ADMIN_CONNECTION_FILE`.

<!-- generated:begin database-help -->
```text
ClaimCore.Database 0.6.0 — schema and recovery-retention administration
  ClaimCore.Database initialize <canonical-IANA-ID>
  ClaimCore.Database initialize-real-data <canonical-IANA-ID>
  ClaimCore.Database publish-real-data-activation-plan <private-policy-file> <private-evidence-file> <private-review-output-file>
  ClaimCore.Database activate-real-data <private-policy-file> <original-evidence-file> <fresh-evidence-file> <plan-id> <first-approval-id> <second-approval-id>
  ClaimCore.Database reconcile-real-data-activation
  ClaimCore.Database initialize-witness
  ClaimCore.Database provision-initial-owner
  ClaimCore.Database register-copy-signer <purpose> <event-id> <key-id> <raw-public-key-file> <owner-approval-id> <holder-approval-id>
  ClaimCore.Database retire-copy-signer <purpose> <event-id> <key-id> <owner-approval-id> <holder-approval-id>
  ClaimCore.Database ingest-managed-copy <attestation-file> <signature-file>
  ClaimCore.Database transition-managed-copy <attestation-file> <signature-file>
  ClaimCore.Database verify-delete-managed-copy <attestation-file> <signature-file>
  ClaimCore.Database verify-managed-copy <attestation-file> <signature-file>
  ClaimCore.Database adopt-managed-copy <private-proposal-file>
  ClaimCore.Database publish-external-copy <private-proposal-file>
  ClaimCore.Database transition-adopted-copy <canonical-file> <signature-file>
  ClaimCore.Database verify-delete-adopted-copy <canonical-file> <signature-file>
  ClaimCore.Database certify-managed-payload-absence <private-proposal-file>
  ClaimCore.Database complete-suppression-horizon <private-proposal-file>
  ClaimCore.Database prepare-writer-handoff <canonical-file> <signature-file>
  ClaimCore.Database settle-writer-handoff <canonical-file> <signature-file>
  ClaimCore.Database activate-writer-handoff <report> <report-signature> <evidence-index> <fence> <fence-signature> <supplement> <supplement-signature>
  ClaimCore.Database abort-writer-handoff <candidate-file> <owner-one-signature-file> <owner-two-signature-file>
  ClaimCore.Database draft-writer-handoff-abort <handoff-id> <abort-key-one-id> <abort-key-two-id> <new-private-candidate-file>
  ClaimCore.Database draft-installation-loss-retirement <loss-key-one-id> <loss-key-two-id> <evidence-report-file|MISSING> <checkpoint-file|MISSING> <known-operations-file> <KNOWN_OPERATIONS|UNKNOWN_OPERATIONS> <new-private-candidate-file>
  ClaimCore.Database retire-installation-after-loss <candidate-file> <evidence-report-file|MISSING> <checkpoint-file|MISSING> <known-operations-file> <first-signature-file> <second-signature-file>
  ClaimCore.Database reconcile-installation-loss-retirement <candidate-file> <evidence-report-file|MISSING> <checkpoint-file|MISSING> <known-operations-file> <first-signature-file> <second-signature-file>
  ClaimCore.Database purge-live <private-proposal-file>
  ClaimCore.Database inspect-managed-copy <copy-id>
  ClaimCore.Database reconcile-lifecycle-event <event-id>
  ClaimCore.Database verify-restore-report <report> <signature> <evidence-index> <nonce>
  ClaimCore.Database verify-fenced-tail <report> <report-signature> <evidence-index> <fence> <fence-signature> <supplement> <supplement-signature> <nonce>
  ClaimCore.Database verify
  ClaimCore.Database verify-data
  ClaimCore.Database hold-backup-capture (private inherited control descriptor)
  ClaimCore.Database reconcile-backup-capture <lease-id>
  ClaimCore.Database issue-backup-health <private-policy-file> <private-independent-evidence-file> <private-certificate-output-file>
  ClaimCore.Database reconcile-backup-health <private-policy-file> <original-independent-evidence-file> <private-certificate-output-file>
  ClaimCore.Database prune [--dry-run] [--settled-retention-days <1-3650>]
                           [--abandoned-retention-days <1-3650>] [--limit <1-1000>]
  ClaimCore.Database describe diagnostics
  ClaimCore.Database help
  ClaimCore.Database version [--json]
Prune defaults: accepted 30 days; revoked 30 days; batch limit 100; deletion enabled.
Set CLAIMCORE_ADMIN_CONNECTION_FILE to an owner-private schema-owner connection file.
Witness commands also require separate owner-private witness connection and key-ring files.
Initial ownership requires a private HTTPS issuer and immutable subject file.
```
<!-- generated:end database-help -->

`initialize` creates an immutable `SYNTHETIC_ONLY` installation. `initialize-real-data` exists only for an operator-reviewed build with a source-pinned independent publication root and backup-health policy; the generic checkout refuses it before creating either schema. A fresh `REAL_DATA` pair starts in `BOOTSTRAP_NO_CASES`, where claimant case work is unavailable. After a witnessed `publish-real-data-activation-plan` from owner-private signed health evidence, two distinct authenticated human installation owners must review and approve that exact stable plan. The owner-only `activate-real-data` command then requires fresh signed health evidence satisfying the published plan and consumes both approvals under the authority lock; primary and witness must agree on the settled activation before case work opens. If its result is uncertain, `reconcile-real-data-activation` repairs only the exact already-settled witness event, including after the short-lived health certificate expires; it cannot initiate another activation. The generic checkout has no reviewed root and cannot complete this real-data path. Keep policy/evidence files and the private plan-review receipt out of Web, CLI, logs and source control.

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
Witness initialization and ordinary owner operations that append authority also require
`CLAIMCORE_WRITER_CAPABILITY_FILE`: an independent owner-private regular file containing exactly
32 random nonzero raw bytes, mode `0600` on macOS/Linux. It is not the witness encryption key and
must not be copied into an argument, log, browser response, or case-work CLI configuration.
Terminal installation-loss retirement is the exception: its purpose-specific witness-owner W0/W1 procedure, separate auditor credential, key ring and two independent owner signatures remain available when the old writer capability or app connection file is lost. It never reopens that writer.
Primary runtime/schema-owner and witness writer/auditor/schema-owner connections refuse a non-loopback host unless its connection string selects
`SSL Mode=VerifyFull`. Remote admission disables GSS encryption fallback so TLS verifies the server
certificate and requested hostname. Loopback development connections may disable TLS; an omitted
SSL mode is not sufficient for a remote host. Remote TLS handshakes also require online
certificate-revocation checking; the server CA must publish reachable revocation evidence.
Unknown or unavailable revocation status can refuse opening, including owner administration.
After certificate compromise, close admission and restart affected processes after replacing
credentials and trust; an established connection is not retroactively revoked.

Restored-pair audit uses `CLAIMCORE_WITNESS_AUDIT_CONNECTION_FILE`, an owner-private connection file
for the separate `claimcore_witness_auditor` role. That role has audited SELECT access and no witness
append, handoff, or writer-capability authority. It is not the case-work witness writer credential;
the restored pair is audited before a replacement writer is activated. Set
`CLAIMCORE_RESTORE_ARCHIVE_ROOT` to the exact owner-private archive directory named by the signed
evidence index. Report verification reopens and hashes each listed encrypted BASE/WAL object under
that root through nofollow handles; a missing, linked, changed, or unregistered copy refuses.

Runtime connection admission checks the supported server, durability/session settings, confined
application identity, exact current baseline identity, required structural checks and least-privilege
ACLs. Development and CI use the digest-pinned image in
[`db/postgresql-baseline.json`](../db/postgresql-baseline.json). Unsupported old storage is classified
before queries assume the current relation layout.

Every checkout re-reads the live server settings, session role, role attributes, baseline marker and,
for the witness, installation identity, epoch and tip. What only the catalog can answer (the pinned
catalog projection, the constraint policy and every privilege result) is re-derived whenever a
one-statement catalog change token differs from the one it last verified, and in any case at least
once a minute. The token digests the row versions and identities of every catalog those checks read,
plus the server start time and database, so any committed catalog write, including a direct
superuser `UPDATE`, changes it (the schema's objects are located by object ids at or above PostgreSQL's first user id, 16384, so a row written straight into a catalog under a lower explicit id is the one change it cannot see, which the periodic verification bounds); a token that cannot be read, a different token or an elapsed
interval runs the complete verification and raises exactly what it always raised, and a refused
verification is never remembered. This proves the catalog unchanged, not correct: it does not
weaken any check and does not replace `verify` or `verify-data`.

## Administration results and delivery

`describe diagnostics` publishes the exact response schema and its fingerprint without configuration
or database access. Results distinguish `NOT_STARTED`, `NOT_COMMITTED`, `COMPLETION_UNKNOWN`,
`COMPLETED` and `COMPLETED_CLEANUP_FAILED`. An unconfirmed commit exits 4 and requires reconciliation,
not an inferred rollback or automatic mutation retry. Definite admission/action failures exit 3.
Use read-only `verify` after reconnecting for schema/role admission and `verify-data` for the full current primary-and-witness row/authority audit. Neither is a historical operation receipt, independently retained freshness checkpoint, or restored-pair certificate; neither can prove which caller created an installation. `verify-data` reports bounded safe counts (including witnessed terminal approvals/events, individual physical-copy verifications, copy-deletion approvals, and writer-handoff approvals, preparations, settlements, activations, and aborts), cutoff and hash, and distinguishes pending witness intents from a clean audit. A failed audit quarantines case-work admission until reconciled.

`verify-restore-report` is a separate read-only exact signed-report recheck. Its pre-handoff report
binds the truthful `unfenced-capture` source cutoff, a later complete quiescent audit cutoff, and a registered WAL prefix; the source label alone does not prove a common cross-cluster cutoff. It
explicitly leaves an open recovery tail and returns `realDataReady:false`. A separately signed W1
tail/fence supplement and independent publication root are required before cutover review. Local
two-container restore tests prove only synthetic mechanics, not separate-host custody or production
recoverability.

Keep the exact independently published Database verifier package and its pinned publication root with every retained historical writer-activation record. Historical W2 reconciliation uses that package's original binary/root binding; a newer binary or rotated root must refuse an old publication rather than reinterpret it. Preserve both packages until the older activation and its evidence are no longer needed. This is historical readback, not permission to activate a new writer with expired proof.

An orphaned writer-handoff PREPARE is not cleared by a timeout. The owner may run `draft-writer-handoff-abort <handoff-id> <abort-key-one-id> <abort-key-two-id> <new-private-candidate-file>` to create exact owner-private canonical bytes with a ten-minute database-clock expiry; it derives the pending W1 ticket, current owner-holder grants, and old writer capability hash under lock, and refuses a pre-existing or linked output file. Two distinct registered `WRITER_HANDOFF_ABORT` human owner key holders must sign those exact LF-terminated bytes independently. `abort-writer-handoff <candidate-file> <owner-one-signature-file> <owner-two-signature-file>` then performs witnessed A1, primary A2, and witness A3 release with exact reconciliation across interruptions. It keeps case work quarantined until the primary and witness abort tickets and full audit agree; consumed approvals cannot be reused. Both commands require separately private primary owner/application, witness owner/auditor, key-ring, suppression-key, and writer-capability files. Do not put private keys, actor IDs, claimant data, or signatures in shell arguments; local synthetic signatures do not prove independent human custody.

If acknowledged history cannot be reconstructed, `draft-installation-loss-retirement` prepares a terminal, data-minimal decision from the live primary authority and independent witness tip. Two distinct, currently granted human owners must already hold registered `INSTALLATION_LOSS_RETIREMENT` keys; real-data activation refuses without them. Supply an owner-private evidence report and independent checkpoint file, or the literal `MISSING` for evidence genuinely unavailable. The known-operation file contains sorted canonical operation IDs, one per line, or is an empty private file in `UNKNOWN_OPERATIONS` mode. The draft binds those exact bytes, the known-ID commitment digest and count, owner/key identities and revisions, and a ten-minute database-clock expiry. The owners sign the exact newly created candidate file independently, then run `retire-installation-after-loss` with the same source files and both signature files. Witness W0 immediately fences the old installation; a confirmed primary receipt and witness W1 make retirement definite. An interrupted result remains uncertain and `reconcile-installation-loss-retirement` accepts only the original candidate and source bytes. Retirement permanently refuses this installation's ordinary reads, writes, recovery and imports, retains its surviving evidence and keyed known-ID denials, and never calls missing cases restored. It does not promote a same-lineage degraded successor; any new installation is unrelated and must not inherit old receipts or identity. When witness or owner-authority evidence is unavailable, no witnessed completion can be claimed: retain the private dual-owner incident record and surviving backups/checkpoints, keep the old installation quarantined, and do not use this command to fabricate an outcome. A signed report records the owners' account of loss; its mere hash is not independent proof that every lost copy or operation was discovered.

`verify-delete-managed-copy <attestation-file> <signature-file>` is an owner-only, exact-copy action after a witnessed individual verifier approval. The private signed transition must match the fresh all-known-location ABSENT inspection, current copy revision, elapsed retention and holds, and registered verifier key; a missing, linked, malformed or divergent private file is refused. Completion means only that one managed copy reached audited `VERIFIED_DELETED`, not that a case or an installation has finished erasure. Never pass a raw case reference, claimant content or database credential as an argument.

`verify-managed-copy <attestation-file> <signature-file>` is an owner-only BASE/WAL verification, not restored-pair qualification. First use the fixed `eng/backup/Verify-ManagedCopy.sh` with a separate custodian's registered `RESTORE_COPY_VERIFIER` key to produce a short-lived, detached-signed physical-check proof for the exact encrypted copy and prospective VERIFY event. Set `CLAIMCORE_COPY_PHYSICAL_INPUT_FILE` to an owner-private `0600` JSON file with format `claimcore-managed-copy-physical-input-1` and exactly `copyId`, `objectPath`, `maximumObjectBytes`, `proofFile`, `signatureFile`, and `commitmentKeyFile`; keep paths and key material out of argv, logs, Web and case-work CLI. The owner command reopens the encrypted object through a nofollow private handle, rehashes its bytes, recomputes the separate HMAC location commitment, checks current distinct human signer authority and DB-clock expiry, then co-commits one witnessed `VERIFY→RETAINED` event and immutable signed report receipt. Missing, changed, linked, wrong-copy or wrong-location proof refuses without claiming verification. `RETAINED` means that one managed copy passed its physical/custody check; it remains an erasure liability and does not establish WAL freshness, a complete restored pair, old-writer isolation, independent off-host custody, or `realDataReady`.

`hold-backup-capture` accepts no command-line paths or credentials. The fixed `eng/backup/Capture-FencedBackup.py` sends canonical BEGIN/FINISH/OBSERVE frames through an inherited private duplex descriptor. Before the two encrypted BASE streams, the owner holds a witness authority read fence, completes a full primary/witness audit and issues a bounded lease with the exact installation, writer generation and witness cutoff. It reopens the resulting ciphertext, checkpoint, and signed cycle files through nofollow private handles, checks active distinct COPY_ATTESTOR and CHECKPOINT human holders and exact file/lease bindings, and keeps the fence through sealing. `CAPTURED_UNVERIFIED` is only a captured-byte receipt; separate witnessed REGISTER/VERIFY→RETAINED events, WAL coverage, independent checkpoint custody and an isolated full restored-pair audit remain required. A failed or interrupted FINISH is uncertain and the exact private files must be retained for owner reconciliation. Configure the owner process with private primary/application and witness owner/writer/auditor connection files, witness key ring, writer capability and suppression key, plus `CLAIMCORE_BACKUP_ARCHIVE_ROOT` and `CLAIMCORE_BACKUP_CHECKPOINT_ROOT` owner-private directories. The generic checkout does not certify independent off-host custody or real-data readiness.

If the pipe response is lost after FINISH, `reconcile-backup-capture <lease-id>` reads only the exact private cycle named by that opaque lease. It rehashes the ciphertext, checkpoint, manifest and signatures, checks historical COPY_ATTESTOR/CHECKPOINT registrations and the original witness cutoff, then compares the persisted receipt byte-for-byte. A missing or changed receipt stays unknown; neither this readback nor a sealed capture marks a copy `RETAINED` or a restore admissible. Do not rerun capture with a new identity to guess whether the first one completed.

`adopt-managed-copy <private-proposal-file>` is an owner-only adoption after an actor-bound approval. The private file contains exact custody, registry, and PRESENT inspection documents with detached signatures, not paths; the owner process independently opens and hashes the mapped ciphertext and checks keyed location and custodian commitments before recording the witnessed ADOPT event. Supply owner-private `CLAIMCORE_COPY_LOCATION_MAPPING_FILE` (one exact copy/case/custodian/location mapping) and `CLAIMCORE_COPY_COMMITMENT_KEY_FILE` (the installation's raw 32-byte commitment key). Missing, changed, inaccessible, or mismatched bytes leave the copy unresolved, not retained. Keep the proposal, mapping, connection files, key, and ciphertext out of CLI/Web requests and command output.

`publish-external-copy <private-proposal-file>` records an externally held copy before a case's erasure fence. The owner-private proposal carries signed registry and independent PRESENT-inspection documents, while `CLAIMCORE_COPY_LOCATION_MAPPING_FILE` and `CLAIMCORE_COPY_COMMITMENT_KEY_FILE` let the owner recheck the actual private ciphertext and keyed custody commitments. The witnessed publication is evidence that this exact copy was known before the request; it is not an adoption, a retained backup, or proof of deletion. A later external-copy adoption must refer to its exact pre-fence publication and obtain fresh custody evidence. A copy lacking that historical receipt remains an unresolved liability.

Signed copy custody and inspection instants must use UTC microsecond precision (seven fractional digits ending in `0`); finer timestamps are refused before publication or adoption because PostgreSQL cannot retain their exact signed value.

`transition-adopted-copy <canonical-file> <signature-file>` records an exact signed `UNKNOWN` or `DELETE_REQUEST` transition after verified adoption, preserving the product-export or external-publication origin. `verify-delete-adopted-copy <canonical-file> <signature-file>` completes `VERIFIED_DELETED` only after current signed all-known-location ABSENT evidence, the required deletion approval, retention and hold checks, and witnessed chain verification. Both are owner-private copy actions, not a case-erasure certificate. A copy that is merely unknown, pending deletion, or absent from one location remains an erasure liability.

`certify-managed-payload-absence <private-proposal-file>` is an owner-only case transition after live and witness payload pruning. It rechecks a fresh, signed complete copy-location registry and independently signed all-location ABSENT inspection against every current managed-copy row, its witnessed deletion and consumed approval, current signer authority, holds, retention and writer generation. The private proposal and configured registry, inspection, commitment and suppression keys never enter a case-work client. Confirmed completion reports `PAYLOAD_ERASED_SUPPRESSION_RETAINED`, not `ERASURE_FINAL`; missing or divergent copy evidence remains pending, and an uncertain owner commit requires exact reconciliation.

`complete-suppression-horizon <private-proposal-file>` is a distinct owner-only final transition after the explicit suppression horizon. It requires a fresh all-copy absence certificate, two action-specific steward approvals, no hold or live claimant payload, and a previously witnessed W2 writer activation whose independent six-host evidence is reverified from the exact retained verifier package. Set `CLAIMCORE_TERMINAL_FENCE_EVIDENCE_DIR` to an owner-private directory containing `report.json`, `report.sig`, `index.json`, `fence.json`, `fence.sig`, `supplement.json`, and `supplement.sig`; the independent deployment evidence directory is still required. All case recovery exports must have expired and lost their retained payload before W2's settled sequence. The generic checkout has no reviewed publication root and refuses this final action. Keep historical signed W2 evidence separately from managed payload copies; an absent or divergent proof leaves the case suppression-retained, not final. This certifies known managed-location absence and old-operation denial, not forensic media erasure or undiscovered human-held copies.

Output failure after confirmed administration does not relabel the database action as uncommitted.
It emits one bounded stderr delivery diagnostic and returns a nonzero exit. Terminal preparation
counts and byte totals are exact decimal strings. Native callers handle `AdministrationOutcome`
rather than assuming an exception or returned unit expresses every completion state.

## Stored data

Accepted command rows record the executable Domain rule revision. Readers and writers use the
Domain-owned revision; the fresh baseline admits only that reviewed revision. Business case and
accepted-history revisions are positive and below `Int64.MaxValue`. Updating executable rules and
their baseline is a fresh-installation boundary, without converting retained old installations.


`cases` holds the current projection of exactly thirteen business fields plus technical revision, disposition, and privacy state. `case_changes` retains ordered accepted-operation evidence with canonical requests, witnessed tickets, actor attribution, and snapshots; accepted replay does not depend on optional preparation retention. A row is a projection, not an independent authority: full audit replays accepted decisions and lifecycle changes against witnessed evidence before comparing the current row. Voiding a data-entry-error case preserves its business history; a privacy erasure request instead fences ordinary access and requires a separate, evidence-bound purge lifecycle.

`request_preparations` stores exact format-3 canonical request bytes and digest with the first preparer's actor/grant provenance and timestamp. An exact-byte replay preserves that first retained metadata. Raw canonical-record import is not an entry point; signed current recovery-artifact import is rechecked against case privacy, current grant, stored export/copy identity, and settled witness evidence. Identified attempts and their independent definite settlements remain available through bounded operation-specific inspection while technical material is retained.

`operation_revocations` retains durable witnessed authority even if a terminal preparation is pruned. Its distinct deterministic witness event ID is bound separately from the authored command ID; revocation can close future unaccepted authority while preserving an earlier orphan acceptance intent and its uncertainty. Actor/grant history, lifecycle events and approvals, legal holds, erasure fences, managed-copy inventory, and keyed operation/reference suppression live outside `CaseFields`. `installation_lineage` binds the installation UUID, witness epoch, suppression-key check, and mandatory business time zone; `schema_baseline` binds this fresh schema's identity/digest. `request_preparation_prunes` is owner-only maintenance audit. No old migration ledger or compatibility view is part of this installation.

Scalar bounds, exact numeric precision, complete decision tuples, chronology, payment prerequisites,
keys and references remain enforced in the final CREATE definitions. These are defense in depth,
not a replacement for the Domain state machine. Runtime credentials permit some direct SQL and therefore belong only to trusted infrastructure. The witness has a separate PostgreSQL role, catalog, append protocol, and key custody. Its journal proves the recorded authority sequence within the stated retention horizon; a principal controlling both clusters and all independent checkpoints remains trusted rather than cryptographically defeated.

## Current-pair data audit

<a id="cc-audit-001"></a>
### CC-AUDIT-001 — Accepted, authority, and suppression evidence agree with the witness

`verify-data` first takes an exclusive cross-process session lease that drains authority operations through primary COMMIT and witness settlement, then holds the primary authority lock and, while the ordinary writer is active, an independent witness read fence while it traverses the current primary's accepted history, lifecycle, actor/grant authority, activation plans and approvals, revocations, managed and adopted/external-copy events, erasure tombstones, and witness journal under one stable cutoff. During a pending handoff, activation or terminal-loss decision, ordinary witness appends are already fenced; the primary lock serializes the allowed owner transitions, and an exact before/after witness-tip check refuses movement during the audit. It replays recorded transitions and compares current projections, exact event identity, sequence, hashes, and available ciphertext evidence; missing or divergent rows quarantine the runtime rather than being counted as an empty or successful audit. Pending witness intents are reported separately, not treated as accepted work or runtime admission. Its safe result includes exact counts, the independent witness-tip hash, and `verifiedCaseTipsSha256`, a digest of verified opaque case IDs, revisions, and witnessed lifecycle tips—not a digest of claimant payload or every historical row. A failed audit reports a bounded category (`EVIDENCE_DIVERGENCE`, `AUDIT_UNAVAILABLE`, `AUDIT_FAULT`, or `TOPOLOGY_REFUSED`) without claimant data. Runtime opening performs the same fenced full audit; subsequent audits run six hours after the preceding completion by default, with `CLAIMCORE_FULL_AUDIT_INTERVAL_SECONDS` limited to 60–86400. A failed or overdue audit closes actor case-work admission until owner reconciliation and runtime reopening. A clean audit of one current primary/witness pair does not prove that an older independent backup contains every acknowledged operation or that unknown copies were deleted; restore promotion additionally needs externally retained freshness and copy evidence.

## Fresh installation boundary

<a id="cc-db-001"></a>
### CC-DB-001 — Atomic fresh baseline and non-destructive refusal

[`db/baseline.sql`](../db/baseline.sql) is one direct final-state schema definition, embedded with the
frozen identity and SHA-256 digest in [`db/schema-baseline.json`](../db/schema-baseline.json).
`initialize <canonical-IANA-ID>` validates an explicitly chosen calendar before connecting. Under a
transaction-scoped schema lock it inspects the namespace before executing DDL. Only an absent
`claimcore` namespace may be created. Schema, grants, baseline marker, new lineage UUID and non-null
calendar commit in one primary-schema transaction; separate witness initialization has its own admission and transaction. A pre-commit DDL failure rolls the primary installation back; loss of commit
confirmation remains explicitly unknown.

A repeated initialization of the exact current baseline with the identical calendar validates it
without rewriting any marker, lineage, timestamp, data or provenance. Concurrent initializers
serialize before classification. A different calendar is refused, not silently substituted.
`verify` runs current identity/structure/calendar checks in a read-only transaction; it cannot
initialize, repair or upgrade. Required runtime-role ACLs are additionally checked by the runtime.
Both runtime opening and read-only verification require the critical business-record and accepted-
history constraints to be present, validated and enforced, including primary, unique and reference
keys. A matching baseline marker does not admit a schema whose protections have been removed or
declared `NOT ENFORCED`. The current catalog comparison is not a full case/history row audit or proof that a restored database contains every prior acceptance; run `verify-data` and compare independent witness/checkpoint evidence before any restore is promoted. The schema owner remains a trusted authority.

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
identified attempt, active case/erasure hold or unexpired recovery export**. A later accepted retry does not erase an earlier attempt's uncertainty.
Definite rejection alone does not close authority: the request may become valid after later state
or business-date changes. Pending, rejected-only and unsettled evidence is never inferred away.

`--dry-run` audits candidate count without deleting candidates. `--settled-retention-days` and
`--abandoned-retention-days` accept 1–3650 days, defaulting to 30; `--limit` accepts 1–1000, defaulting
to 100. Pruning requires current-baseline admission and schema ownership, serializes maintenance,
and audits parameters/counts. It holds the primary shared authority lock before operation locks, serialising eligibility and deletion against new holds; a hold review date never releases protection. Review a dry run and the operator's retention requirements before
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

The Compose dependency is the official PostgreSQL image, pinned by tag and digest in
[`db/postgresql-baseline.json`](../db/postgresql-baseline.json) (and, equally, in `compose.yaml`; a test holds the
two together), not a packaged ClaimCore application. ClaimCore ships no database image. The image keeps its own
PostgreSQL license and package notices under `/usr/share/doc/`; weekly Dependabot pull requests propose newer images,
and one is adopted only through that reviewed change.

Compose is for local synthetic development only. Passwords initialize a new volume; changing `.env`
does not rotate roles in a retained database. The authenticated health check remains unhealthy when
the retained roles and new values disagree. Restore the matching private configuration or perform an
administrator-led credential rotation—do not delete an adopted volume to clear a health failure.

Integration tests create isolated Testcontainers instances, initialize fresh baselines themselves, and never
reuse the persistent developer database or repository connection files.

## Operational limits

An installed checksum identifies recorded baseline source; current catalog checks and full data audit add separate evidence but do not defeat an administrator controlling primary, witness, keys, and checkpoints together. ClaimCore has no automatic repair, downgrade, or active-active failover. The owner-only managed backup tool and isolated two-cluster restore drill are functional synthetic qualification, not off-host custody, live admission, or a production cutover certificate. The service runtime owns its primary data-source lifetime and separately credentialed witness protocol; a second case-work process is not an authorized concurrent writer merely because it can connect. See [Security and operations](operations.md) before considering real data.

### Backup control lifetime

The inherited private duplex socket keeps exact bounded frames. BEGIN and OBSERVE waits are bounded
by thirty seconds; a held FINISH/ABORT frame uses the remaining capture lease budget. One deadline
covers the complete frame, including trickled bytes, and the capture cancellation token interrupts
a blocked read. The owner awaits the read before releasing its stream/token scope. A missing reply
after FINISH remains an evidence-reconciliation problem, not proof that sealing failed.
