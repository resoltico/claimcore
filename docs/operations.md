# Security and operations

## Current trust model

ClaimCore has one installation-wide, case-sensitive reference namespace and one HTTPS case-work service. Browser login uses OIDC Authorization Code/S256 PKCE; interactive CLI uses its own public client, while automation uses distinct client credentials. ClaimCore—not the identity provider—owns default-deny actor and case grants. The browser and CLI receive neither PostgreSQL credentials nor witness/key-custody material. Owner administration is a separate executable and credential boundary.

The primary schema owner, witness superuser and host/storage administrators remain trusted authorities. Direct SQL by those actors can bypass application checks; internal F# types and a second witness cluster do not defeat a person who controls both authorities or all backups and keys. The Web service defaults to loopback and supports explicit HTTPS interface binding; this is not independent deployment or active-active qualification. Synthetic primary/witness containers on one Mac prove functionality, not physical or administrative independence. Do not place real or adopted data under this build until the separate-host, backup, restore, audit, and retention admission requirements below are fulfilled.

Each fresh installation records an immutable `SYNTHETIC_ONLY` or `REAL_DATA` scope in both primary and witness. The generic build has no reviewed publication root and creates only synthetic installations. An operator-reviewed real-data build begins in `BOOTSTRAP_NO_CASES`: only the bounded owner/authority setup needed to establish independently verified backup health is allowed, never claimant case work or recovery. Activation is a one-way witnessed decision tied to a published, human-reviewed plan, two distinct current human owner approvals, and a fresh signed health certificate; mismatched primary/witness phase or an uncertain activation stays quarantined. A later stale health certificate stops claimant mutations while owner/custodian repair remains available under its separate authority lane. Do not treat local synthetic activation tests as real-data qualification.

## Credentials and sensitive surfaces

- Application environment variables select private files rather than carrying raw connection strings
  or credentials. Local Compose initialization reads its two passwords from the ignored `.env` file;
  they remain secrets even though Compose passes them to the container environment.
- Keep primary and witness connections, OIDC secrets, certificates, key rings, Web state, recovery, download, and diagnostic files in ignored,
  owner-controlled locations.
- Keep the active writer-generation capability in its own exact 32-byte, owner-private raw file;
  a witness key ring or PostgreSQL password is not a substitute. Cutover must retire the old
  capability under a witnessed handoff before another writer may serve case work.
- Do not print or upload connection strings, passwords, request bodies, claimant data, cookies,
  clipboard contents, downloaded recovery artifacts, canonical requests, or private terminal
  captures. CLI delivery diagnostics may contain only bounded safe identity and recovery direction after mutation dispatch.
- The persistent local Compose deployment uses TLS with its explicit local CA.
  Primary runtime/schema-owner and witness writer/auditor/schema-owner connections require `SSL Mode=VerifyFull` for a
  non-loopback PostgreSQL host and disable GSS encryption fallback; the server certificate and host
  name must validate. Remote TLS checks certificate revocation on new handshakes, so its CA must
  publish reachable revocation evidence. Supply a trusted root certificate where needed and keep credentials private.
  HTTPS listener and publication are explicit operating choices; real-data operation still needs a separately qualified deployment.

Compromised transport or issuer credentials require an explicit closure: stop affected hosts and
CLI sessions, revoke actor grants where their authority may be exposed, replace issuer signing
keys, credentials and trust roots, restart hosts to discard cached issuer metadata and browser
sessions, then perform exact current-pair audit and independent evidence review before reopening.
TLS revocation acts at handshake, not on existing connections. Key-ring rotation must retain
historical decryptors for witnessed audit and exact recovery; deleting an old key or renaming its
ID cannot undo disclosure. A ring with reused or zero material refuses startup without changing
its private file or stored evidence. Preserve matching old software and evidence for owner repair
if an existing installation has such a ring; never reset or adopt it to evade the refusal.

- Handle-first private-file operations are supported on macOS and Linux only. Windows source builds,
  tests, and configuration-free discovery work, but operations that need private credential or artifact files fail closed until an independently verified Windows handle/ACL implementation exists.
- On macOS, use physical canonical paths; system aliases such as `/var` and `/tmp` have linked
  ancestors and are intentionally refused. An extended ACL on any ancestor also refuses admission,
  including a home directory with a stock deny-delete ACL. A repo-local `.local` directory does
  not avoid that ancestor. Use the [durable private operator path](service.md#persistent-local-evaluation)
  and retain its files under operator custody; do not remove a home ACL to admit the CLI.

Initialization and retention credentials own schema administration and must never be application
credentials. This build intentionally refuses every old installation without changing its data.
Retain old databases and artifacts under their compatible software; provision a separate fresh
installation using `ClaimCore.Database initialize <canonical-IANA-ID>`. There is no automatic
upgrade, reset, deletion, database-content import or artifact conversion. See the
[installation boundary](database.md#fresh-installation-boundary).
Test credentials and containers must point only to disposable synthetic databases. Browser downloads
and the operating-system clipboard leave ClaimCore's process boundary; see the
[Web reference](web.md#recovery-downloads-and-clipboard).

Runtime connections use 5-second establishment and 10-second ordinary command budgets; owner and
witness connections use 5/30 seconds. All use 2000 milliseconds for cancellation readback. Authority
lease acquisition inherits the finite command budget; scheduled audits retain an explicit two-hour
acquisition/whole-operation window. Expiry after dispatch does not prove non-commit. Async catalog
admission owns a short pre-work transaction with local settings and independent finite rollback;
unclean connectors are retired rather than reused.

## The installation calendar

The fresh initializer stores one mandatory canonical IANA zone atomically with installation
lineage. Every business date comes from that stored zone and one
captured instant, never from a host default. The identifier is validated when it is set and again on
every runtime opening: it must be enumerable on the host, must resolve, and must resolve back to
exactly itself, and must be an IANA identifier rather than a platform-native one, so an alias, a Windows identifier, an abbreviation, or an offset literal is refused on every host.
A host that cannot resolve the stored zone refuses to open the runtime rather than producing a date
from some other calendar.

That validation binds the identifier, not the rules behind it. **ClaimCore resolves the zone through
the host's IANA time-zone database, so the offsets and daylight-saving rules it uses are the host's.**
Two hosts running different tzdata releases can therefore derive different business dates for the
same instant, but only for an operation that falls inside a transition whose rules changed between
those releases. The same applies to one host across an operating-system update.

Accepted history retains its effective business date and UTC observation authenticated by the
independent witness. Audits replay that saved execution context; later zone-rule changes do not
recalculate it. Fresh commands use the installed current rules, so updates can affect future
acceptance. A quiet period cannot establish historical validity.

Treat the time-zone database as part of fresh date calculation:

- Keep the hosts that serve one installation on the same operating-system time-zone data, and update
  them together.
- `ClaimCore.Database initialize` refuses a calendar different from the installed one. Choosing a
  different calendar is a new installation decision, not an edit.

ClaimCore does not ship its own time-zone database and does not detect tzdata skew between hosts.
Nothing in the product weakens this by running with invariant globalization, which would remove IANA
zone resolution altogether.

Durable lifecycle/tombstone approvals and holds, signer approvals, managed-copy retention and list
windows use the primary database's current UTC observation under their locks. List validation and
continuation issuance share one page observation. The database clock is not PostgreSQL transaction
start time, and elapsed waiting cannot leave a new decision authorized by an earlier host instant.
Witness sequences, revisions and exact identities define ordering; UTC timestamps do not replace them.

Keep primary, witness, issuer and independent verifier UTC clocks synchronized. Database UTC is not
a monotonic guarantee across clock corrections or restarts; signed current evidence refuses future
observations and exact expiry, while historical readback does not restore fresh authority. Recovery
and history cursors are query positions, not signed grants or independent freshness certificates;
every read still requires current actor authority and disclosure checks.

## Independent witness authority

<a id="cc-wit-001"></a>
### CC-WIT-001 — Independent, ordered authority evidence

The separate PostgreSQL witness serializes one installation's authority events through a gapless committed sequence and hash-linked journal. Case acceptance, technical attempt authority, grant and lifecycle changes use exact event identities, intent and settlement phases; a primary commit without confirmed external settlement remains uncertain until reconciliation. Exact receipt replay requires the committed candidate and independent settlement; read-only disclosures verify without appending, while mutation-bound replay may reconcile the exact witnessed candidate. Command identity and revocation event identity are distinct so future authority can close without changing earlier attempt knowledge. Runtime opening and the full current-pair audit replay the committed witness journal and refuse a missing, altered, duplicated, or divergent sequence; individual calls check the authoritative tip and generation under their locks, not the entire journal anew. Application and Web roles cannot rewrite journal rows or prune ciphertext; owner credentials and independent key custody remain trusted authorities. A witness on the same Mac as the primary is functional qualification, not protection against simultaneous host loss or an administrator who controls both stores.

Opening a runtime performs a fenced full audit. While it remains open, a bounded scheduled full audit repeats six hours after each preceding completion by default; `CLAIMCORE_FULL_AUDIT_INTERVAL_SECONDS` accepts 60–86400. It first drains complete primary authority operations through post-COMMIT witness settlement using an exclusive cross-process session lease, then takes the primary authority lock and independent witness ticket fence before capturing one stable cutoff and snapshot. Long audits do not accumulate catch-up runs. Its separate two-connection audit pool cannot be exhausted by queued actor work. Owner `verify-data` uses that same witness fence during ordinary writer activity; pending handoff, activation or loss phases already fence ordinary witness appends, and its primary lock plus before/after tip check prevent a moving pending transition from being reported as a stable audit. A failed or overdue audit closes actor-bound case and authority access; the owner Database reconciliation path remains separate. Read-only `verify-data` reports the witnessed cutoff, safe counts, a digest of verified opaque case tips, and a safe failure category. Those observations are not a backup freshness certificate.

Runtime diagnostics use the `ClaimCore.Runtime` meter. `claimcore.witness.failures` counts fixed
`stage` values (`read`, `append`, `settlement`) and fixed `cause` values (`pending_evidence`,
`transport`, `schema`, `authority`, `integrity`, `unexpected`). One stderr notice per stage/cause
combination is emitted per process; repeated events remain counted. These causes do not determine
whether a dispatched operation committed. Preserve its identity and exact bytes and use the
operation's recovery workflow to reconcile evidence.

`claimcore.audit.quarantines` records `reason=failed` or `reason=overdue` once when a scheduled-audit
worker closes admission. Quarantine remains sticky until owner reconciliation and runtime
reopening. Use owner `verify-data` and the relevant recovery procedure before reopening; a
counter or notice does not replace their evidence. Instruments have no case, operation, principal,
path or provider-message labels. Delivery is best effort: a failing listener or stderr sink cannot
change operation outcomes or reopen quarantined access. No metrics HTTP endpoint or general
framework logging provider is enabled by these signals, and source-preview readiness remains
unqualified.

## Data and recovery

History preserves prior facts after corrections. `VOID_DATA_ENTRY_ERROR` is an audited business disposition, not deletion; authorized reinstatement creates another witnessed event. A privacy erasure request is a separate fence on ordinary reads and work. Live payload purge and managed-copy deletion require distinct steward approvals, holds and uncertain-attempt checks, inventory, and independent verification; a pending phase must not be called erased. Minimal keyed suppression evidence is pseudonymous data with its own retention purpose. There is no claim of physical-media sanitization or deletion of an undiscovered human-held copy.

The owner-only `ClaimCore.Database prune` command removes bounded terminal technical preparations; it never erases accepted authority or resolves an unknown attempt, and active case/erasure holds and unexpired exports exclude their preparations from deletion. Recovery artifacts contain claimant data and do not prove that a command committed. Submission attempts and definite technical settlements remain separate from accepted history; a witnessed accepted receipt proves acceptance inside the stated recovery horizon. Schema owners retain broad administrative power outside the product command surface.

Detailed recovery inspection pages actual identified attempts and definite settlements. An unsettled
attempt stays unsettled even after a later definite attempt and excludes its preparation from
pruning. Never treat a bounded list or a momentarily absent receipt as proof of non-commit. Durable
revocation prevents future unaccepted execution without rewriting earlier uncertainty. Historical
unidentified-start and dismissal compatibility is absent because old installations are refused;
there is no inferred or retroactively fabricated authority transfer.

For an uncertain mutation, preserve and replay only the exact operation identity and retained
format-3 request or signed format-3 recovery-artifact bytes described in
[CLI and protocol](cli.md#canonical-request-identity-and-recovery). Do not infer failure from missing
output, a delivery loss, or a momentarily absent receipt. Lineage and a matching marker do not prove freshness: an older primary backup can omit later accepted work, revoked authority, grants, holds, or erasure fences. The product's current-pair full audit does not replace a separately retained signed checkpoint and a complete test restore. The managed backup tool and local two-cluster drill are synthetic qualification components, not a production backup, cutover, or independent-host certificate. A restore remains quarantined until exact witness, catalog, row/authority, WAL, checkpoint, and newer-fence reconciliation has passed under a fenced writer handoff.

The payment command records an operator assertion; ClaimCore does not transfer funds or contact a
provider. Future external effects require durable intent, provider idempotency, acknowledgements, and
reconciliation.

## Privacy erasure and suppression

<a id="cc-erase-001"></a>
### CC-ERASE-001 — Fenced live purge and witnessed payload pruning

An erasure request fences ordinary reads and writes before any deletion. Owner-only live purge requires two distinct witnessed data-steward approvals, no active hold, complete suppression-denial coverage and a full current-pair audit; it removes live claimant rows but leaves a keyed, pseudonymous tombstone that blocks stale operations, imports, and reference reuse. A separate owner-only witness-payload prune requires a fresh pair of steward approvals bound to the exact post-purge witness target set, cutoff, authority tip and expiry. Its owner transaction settles the prune event and removes only sealed CASE ciphertext while leaving immutable journal metadata; an interrupted settlement remains uncertain until exact reconciliation. The post-prune audit verifies the recorded target set and absence without reconstructing erased claimant bytes. A signed target-set marker preserves whether a pruned operation published an external copy, so its immutable publication receipt remains mandatory in full audit after ciphertext pruning. Neither step deletes backups, WAL, snapshots, keys, exports or undiscovered copies. Known liabilities and active holds keep the privacy state pending; no clock expiry or individual copy deletion silently certifies final erasure.

Supply the live-purge proposal's `validUntil` at UTC microsecond precision (seven fractional digits ending in `0`); an unroundtrippable deadline is refused before an owner witness intent, not silently truncated after the two approvals.

Owner certification of `PAYLOAD_ERASED_SUPPRESSION_RETAINED` requires a fresh signed complete installation inventory and independent signed `ABSENT` inspection, with every current managed copy in witnessed `VERIFIED_DELETED` state through a consumed deletion approval. A known unmanaged or unadopted external copy, active hold, early retention, unsettled witness work, changed writer generation, or copy appearing after the signed inventory keeps certification pending. The issuer conservatively treats every current global backup, WAL, snapshot, replica and key copy as potentially case-bearing because this baseline does not encode proof that a particular copy cannot contain the case. A later copy can be included in a newer signed inventory after its verified deletion; source time alone does not clear it. This intermediate phase retains pseudonymous suppression evidence and is not total erasure. `ERASURE_FINAL` additionally requires a separately signed recovery-fence issuer proving the suppression horizon and old-writer isolation.

## Managed-copy custody and restore

<a id="cc-backup-001"></a>
### CC-BACKUP-001 — Signed copy inventory, verified deletion, and restore evidence

Managed primary, witness, WAL, snapshot, replica, key and product-export copies are tracked by opaque identity and separately purposed signers. A copy's `UNKNOWN` or `DELETE_REQUEST` state is not deletion. Owner-only `VERIFIED_DELETED` requires elapsed retention, no active hold, an exact current all-known-location registry and fresh signed ABSENT inspection by a distinct registered verifier, and a current individual approval bound to the report, copy revision and witness cutoff. The primary transaction co-commits the copy event and one-use approval consumption against a precommit witness intent; uncertain external settlement remains uncertain until reconciled. The full audit replays both the signed transition and approval use; changing the recorded verification time or dropping evidence is a refusal. One verified copy does not certify a case: known unmanaged exports, missing copy attestations, unavailable archives, and undiscovered human-held copies remain explicit liabilities. The local encrypted two-cluster backup and restored-pair drill is synthetic qualification only; a separate-host deployment needs independently retained signed freshness, WAL, complete restore and recovered-data checks before quarantine can be lifted.

An owner-held, signed dual-BASE capture initially yields only `CAPTURED_UNVERIFIED`: its exact encrypted bytes and cutoff still need separate witnessed copy registration and physical `VERIFY→RETAINED`, WAL coverage, independent checkpoint custody, and a signed full restored-pair audit with W1 tail and writer fence. Neither the capture receipt nor the generic build's absent publication root establishes real-data readiness.

Independent signing roles require distinct raw Ed25519 keys as well as the existing human,
machine, storage and host-key evidence. Different PEM encodings of one key cannot satisfy role
separation; exact file digests still bind the signed topology's pinned bytes. Keep capture lease
IDs, original signed candidates and matching verifier packages for the
[interrupted owner procedures](database.md#interrupted-owner-procedures). A checkpoint or local
capture receipt is historical evidence, not renewed health or permission to lift quarantine.

Intermediate restore reports always carry `realDataReady: false`, including `scope: full`; scope
identifies the evidence profile, not permission to process real data. The owner fenced-tail recheck
reports readiness only after fresh independent-host and local evidence agree. Writer activation
separately rechecks its exact signed candidate and current authority. Historical verification
reconstructs the original signed candidate without granting a fresh W2 ticket or health lease.

Restored-pair verification uses a separate SELECT-only witness auditor from the owner-private
`CLAIMCORE_WITNESS_AUDIT_CONNECTION_FILE`, not the old writer credential or capability. The
owner-private `CLAIMCORE_RESTORE_ARCHIVE_ROOT` must equal the signed index root, and encrypted
BASE/WAL files are rehashed at verification time. A pre-W1 audited report leaves its recovery tail
unsealed and is not a recovery certificate; the independently signed W1 supplement must close that
gap and prove permanent writer fencing before any cutover-ready conclusion. Same-Mac synthetic
evidence never establishes independent host, storage, key, or administrator custody. Owner and
verifier hosts need synchronized UTC clocks: a future-dated or expired report refuses rather than
receiving a freshness allowance from the verifier.

If a signed W1 writer handoff cannot complete, the schema owner may use the explicit two-owner abort workflow in [Database administration](database.md#administration-results-and-delivery). A1 records the exact abort while keeping the witness fence, A2 consumes the two distinct owner-held signatures in primary, and A3 releases only the reconciled pair; an interrupted step stays quarantined and is retried by exact bytes. Expiry does not silently roll back W1, and an already-open old runtime must not resume merely because the witness pending flag clears. The signatures used in local synthetic tests are not proof that two independent humans or hosts held the keys.

If acknowledged history cannot be reconstructed, the owner-only [installation-loss retirement](database.md#administration-results-and-delivery) records a terminal decision signed by two distinct current human owners with purpose-specific keys. Its first witness ticket immediately closes the old runtime; primary denial commitments for known operation IDs and a second witness ticket settle the exact decision. An unknown affected-ID set permanently fences the whole old installation instead of guessing which retry might have committed. An incomplete ticket remains pending and is reconciled only with the original signed bytes. Retirement never converts missing cases into present cases, labels an uncertain commit failed, or enables a same-lineage degraded successor. Keep surviving clusters, backups, checkpoints and private decision evidence under custody; a new installation is a different identity, not a restore of the retired one. If the witness or owner-authority evidence is missing, the product cannot claim a witnessed retirement and the old installation stays quarantined; external dual-owner incident governance is required without promoting the old data. No two-container test proves independent human key custody or complete discovery of lost operations.

## Before real data

Before processing personal or operational data, require a successful exact-revision CI and deployment gate, a separately hosted/administered witness, independently custodied encrypted primary and witness backups/WAL and signed checkpoints, a completed full restored-pair audit, a fenced single-writer handoff, live OIDC/actor-grant qualification, and pre-registered loss-retirement keys held by two distinct human owners. Establish an explicit business calendar, backup interval and recovery horizon, retention/hold/deletion policy, private export custody, monitoring, and an external incident decision process for loss beyond surviving product evidence. These are installation choices; ClaimCore does not invent a jurisdiction's legal period or require a permanent AWS subscription.

A reviewed real-data build must pin both the independent publication public key and the exact backup-health policy digest in source. A fresh `REAL_DATA` installation begins in witnessed `BOOTSTRAP_NO_CASES`: only typed actor, grant, signer and activation-approval setup is admitted; claimant casework, recovery and export remain closed. Two distinct HUMAN OWNER approvals bind a published stable physical activation plan, while activation separately requires fresh health evidence. The one-way witnessed `ACTIVE` transition cannot be inferred from an empty case table. The owner-only `issue-backup-health` path accepts only a current CHECKPOINT-holder-signed certificate bound to three distinct policy-pinned archive, checkpoint and test-restore signatures, actual witnessed `RETAINED` BASE/WAL rows, nofollow object hashes, contiguous WAL and a full current-pair audit. Its certificate is valid for at most 90 seconds; the runtime rechecks its signature, database-clock age, witness ancestors and current copy inventory before each new mutation. The generic source-preview build has no reviewed deployment root, cannot initialize `REAL_DATA`, cannot issue full health and never represents local synthetic activity as real-data readiness.

After a lost `issue-backup-health` response, use owner-only `reconcile-backup-health <private-policy-file> <original-independent-evidence-file> <private-certificate-output-file>` for readback. It authenticates the original signed bytes and witnessed historical CHECKPOINT signer even after certificate expiry, then reports exact observed `COMPLETE`, `PARTIAL_CERTIFICATE`, `PARTIAL_SIGNATURE`, `MISSING` or `UNKNOWN`. It never writes an output file or makes stale bytes ready. Preserve partial/unknown files; issue a fresh certificate at a new private output path when recovery requires current health, and quarantine changed or unreadable bytes for owner review.

Each owner first uses the authenticated `authority.reviewRealDataActivation` route to inspect typed nonclaimant plan facts, the exact canonical plan text and its SHA-256; an absent or inaccessible plan has the same refusal. `authority.approveRealDataActivation` then witnesses that plan ID/digest and the current approval-chain tip, with actor identity supplied only by the authenticated session. The two approvals require different enabled HUMAN installation OWNERs; each expires no later than 24 hours after its database-clock approval time. A lost response is retried with the same approval ID and request bytes, never a new candidate. Plan approval is not a health lease: the owner activation still checks current grants, the published plan and a newly issued short-lived health certificate against the current primary/witness pair.

A local synthetic run cannot establish independence from loss or administration of the same machine. No design can guarantee recovery after coordinated rollback or simultaneous loss of primary, witness, independent checkpoints, backups, and keys; infer that an unacknowledged COMMIT failed; or destroy unknown unmanaged copies. Passing tests does not establish legal compliance or security certification. Keep the installation quarantined when required evidence is missing or divergent, rather than weakening a gate or relabeling uncertainty.
