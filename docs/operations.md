# Operations and trust

The one PostgreSQL database is the authority. A successful read proves only what that snapshot
contains. The identity provider, service host, database owner and backup operator are trusted.
Ledger hashes detect inconsistency against trusted retained evidence; they do not independently
prove freshness, defeat a malicious administrator who rewrites everything, or prove real-world facts.
Case fields and payment dates are operator assertions; ClaimCore transfers no money.

## Erasure limits

ERASE clears payload and payload_nonce on every ledger entry of the case,
removes business projections, and retains reference, revision and ledger metadata. It cannot
reach old row versions, WAL, replicas, logs, exports or backups. Retained references may still
identify a person. Operators own retention, physical disposal and privacy decisions outside
this live-database act. VOID preserves all history and is not erasure.

Authority history is never erased, including the principals’ issuer and subject and executor/target
attribution. Case erasure does not erase actor identities or grants.

The append-only guard permits only case payload clearing; it is an integrity guard, not protection
against a hostile trusted owner. A schema owner can clear payload without recording ERASE under
that exception; verification detects missing payload without an accepted erasure. Runtime erasure
requires an accepted ERASE and removes its case’s complete live payload history atomically.

## Access review and sessions

Use `authority.list` with installation OWNER or AUDITOR for current actor summaries, then
`authority.grants` for the selected actor's bounded grant pages. Follow continuations to completion;
actor summaries do not contain a hidden complete grant set, and disabled actors remain reviewable.
Each request checks current caller rights; pages do not constitute one frozen cross-request audit.
Registration
records both executor and target; for REGISTER_ACTOR they differ. Scope grants deliberately, keep
at least one enabled human installation owner, and revoke ClaimCore grants independently of IdP
membership. Service principals cannot receive OWNER or DATA_STEWARD. Bearer service identity comes
from the admitted client ID; the CLI’s PKCE principal uses the human subject.

Before registering a SERVICE principal, restrict its confidential IdP client to client credentials
and disable interactive/user grant paths. The human CLI client is public authorization-code/PKCE,
with service accounts disabled. Put the API audience in access tokens only, never ID tokens.
`azp`/`client_id` classification does not establish the grant type; operators must maintain these
flow restrictions. [Web admission](web.md#cc-web-001) owns token classification and technical checks.

The evaluation identity container has a 2 GiB memory limit; Keycloak sizes its heap within that
container budget. Docker also needs capacity for PostgreSQL, Web and image builds. This is an
evaluation budget, not production sizing.

The local [Compose configuration](../deployment/compose.yaml) keeps the Web root filesystem read-only
and supplies bounded ephemeral `/tmp` for framework staging. Only the protected key-ring volume
persists session material; temporary staging is outside database backups.

Browser session protection keys and encrypted server-side session tickets persist in the required
key-ring directory. Tickets live in its private `sessions` subdirectory and survive a Web restart
with their original idle and absolute deadlines. Logout deletes the ticket. A backward clock
step that predates the last persisted activity refuses that session. The cookie contains only a
protected random lookup key; identity and tokens are not stored in browser storage. Protect the
whole directory, including tickets, and do not share it between concurrently running Web
processes: multiple-instance session coordination is not qualified. Treat the key directory
as a secret outside database backups; Linux key XML is unencrypted. Preserve, back up and rotate
this directory separately from database backups, with the same care as the client secret.
Pagination follows the [API contract](api.md). The browser retains no pending request across reload. Preserve exact uncertain requests through the caller’s process; never replace their ID.

## Private configuration

Private inputs are absolute paths with valid UTF-8 and no NUL, supported on Linux and macOS.
Symbolic links are followed, including Kubernetes secret-volume links. Path mode is checked before
opening. The caller applies a two-second timeout to the read-only open worker; a timed-out open
refuses as not regular. The worker may finish later, in which case its handle is disposed.
The opened handle must be seekable and satisfy the same mode policy: no other-user access
and no group write or execute. Group read is allowed, including `0640` and `0440`. Its length must
fit the caller's bound, all bytes come from that admitted handle, and the caller clears buffers.
Directories, missing or oversized files and malformed paths refuse. There is no native code.

Ownership and ACLs are not inspected. Filesystem mode semantics, the file owner and the host
administrator remain trusted; admission does not freeze concurrent changes to contents or
permissions. Preserve all notices when distributing a published application;
[Development](development.md#first-checkout) owns qualified publication modes.

## Backups and restore

Back up PostgreSQL with ordinary platform tools and protect copies as sensitive data. Retain
trusted ledger sequence/hash checkpoints independently. After restoring, use Database verification
with the expected checkpoint before reopening actor access. A restore from before later erasure
can resurrect payload; `verify --expect` against a later trusted position refuses that older
state. Identify and address affected erasures/copies before reopening. A checkpoint can detect
rollback behind it, not prove that no newer acceptance occurred. ClaimCore does not orchestrate
backup, restore, replication, copy custody or multi-party approval, or decide when restored data
is ready for use.

## Before real data

The local Compose configuration is evaluation setup, not production qualification. An operator
must choose and review deployment, identity administration, least-privilege credentials, TLS,
backup/restore procedures, checkpoints, monitoring, physical retention and incident response.
Test restoration and erasure effects on copies. Verify the actual runtime's privileges and
configuration. Green CI or source review grants no owner approval and no production assurance.
Keep existing installations with their matching software; no conversion is supplied.

## Serialized writes and verification growth

Accepted writes intentionally serialize on one ledger head. Verification streams rows but retains
operation identities and folded case/actor/grant state; its memory grows with accepted history and
projection cardinality. A streaming reader does not imply constant-memory verification.
The [synthetic qualification](development.md#synthetic-growth-qualification) exercises bounded
workloads in isolated databases and retains per-workload reports for their measured environment.
Those reports do not establish a memory-scaling bound, asymptotic timing bound or production
capacity. Timeout and cancellation guards remain enforced. More concurrent writers do not remove
serialization or promise more throughput. Operators must qualify their own workload and storage.
