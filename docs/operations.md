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
flow restrictions as described in [Web admission](web.md#cc-web-001).

Browser sessions and list cursors are ephemeral. A runtime restart invalidates cursor signatures;
expired or changed-grant cursors must be restarted. The browser retains no pending request across
reload. Preserve exact uncertain requests through the caller’s process; never replace their ID.

## Private configuration

Use the [owner-private input policy](database.md#cc-db-002) on supported local filesystem
semantics. Linux POSIX ACL masks and macOS descriptor ACLs are admitted explicitly; unrelated
network/userspace permission models are outside that assurance. The file owner and host
administrator remain trusted: descriptor admission does not freeze a concurrent owner rewriting
content or permissions. Preserve matching native libraries and all notices when distributing a
published application; [Development](development.md#first-checkout) owns supported build targets.

## Backups and restore

Back up PostgreSQL with ordinary platform tools and protect copies as sensitive data. Retain
trusted ledger sequence/hash checkpoints independently. After restoring, use Database verification
with the expected checkpoint before reopening actor access. A restore from before later erasure
can resurrect payload; `verify --expect` against a later trusted position refuses that older
state. Identify and address affected erasures/copies before reopening. A checkpoint can detect
rollback behind it, not prove that no newer acceptance occurred. No product backup, restore,
replication, custody, multi-party approval or deployment-readiness engine exists.

## Before real data

The local Compose configuration is evaluation setup, not production qualification. An operator
must choose and review deployment, identity administration, least-privilege credentials, TLS,
backup/restore procedures, checkpoints, monitoring, physical retention and incident response.
Test restoration and erasure effects on copies. Verify the actual runtime's privileges and
configuration. Green CI or source review grants no owner approval and no production assurance.
Keep existing installations with their matching software; no conversion is supplied.
