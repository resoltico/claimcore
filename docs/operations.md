# Operations and trust

The one PostgreSQL database is the authority. A successful read proves only what that snapshot contains. The
identity provider, service host, database owner and backup operator are trusted. Ledger hashes detect
inconsistency against trusted retained evidence; they do not independently prove freshness, defeat a malicious
administrator who rewrites everything, or prove real-world facts. Case fields and payment dates are operator
assertions; ClaimCore transfers no money.

## Erasure limits

ERASE clears payload and payload_nonce on every ledger entry of the case, removes business projections and retains reference, revision and
ledger metadata. It cannot reach old row versions, WAL, replicas, logs, exports or backups. Retained references may
still identify a person. Operators own retention, physical disposal and privacy decisions outside this live-database
act. VOID preserves all history and is not erasure.

Authority history, issuer/subject, executor/target attribution, actor identities and grants are never erased.

The append-only guard permits only case payload clearing; it is an integrity guard, not protection against a
hostile trusted owner. A schema owner can clear payload without recording ERASE under that exception;
verification detects missing payload without an accepted erasure. Runtime erasure requires an accepted ERASE
and removes its case’s complete live payload history atomically.

## Access review and sessions

Use `authority.list` with installation OWNER or AUDITOR for current actor summaries, then `authority.grants`
for the selected actor's bounded grant pages. Follow continuations to completion; actor summaries do not
contain a hidden complete grant set, and disabled actors remain reviewable. Each request checks current caller
rights; pages do not constitute one frozen cross-request audit. Registration records both executor and target;
for REGISTER_ACTOR they differ. Scope grants deliberately, keep at least one enabled human installation owner,
and revoke ClaimCore grants independently of IdP membership. Service principals cannot receive OWNER or
DATA_STEWARD. Bearer service identity comes from the admitted client ID; the CLI’s PKCE principal uses the
human subject.

Before registering a SERVICE principal, restrict its confidential IdP client to client credentials and disable
interactive/user grant paths. The human CLI client is public authorization-code/PKCE, with service accounts
disabled. Put the API audience in access tokens only, never ID tokens. `azp`/`client_id` classification does
not establish the grant type; operators must maintain these flow restrictions. [Web
admission](web.md#cc-web-001) owns token classification and technical checks.

Protected authentication tickets now live in PostgreSQL; cookies contain only protected random lookup keys. Two Web
instances with the same admitted database and key ring can share sessions. Logout deletes a row; renewal only updates an
existing live row, so it cannot resurrect logout. Already admitted work may finish. DB UTC idle/absolute deadlines and
observed backward-step refusal replace process-monotonic elapsed time: arbitrary clock corrections have no real-elapsed
guarantee. Each row retains its original idle duration and absolute deadline across renewal/restart; changed deployment
settings affect new sessions only. Row lookups are asynchronous and independent of the ledger head and creation-cap
lock. This shared-clock qualification does not qualify arbitrary load balancing, key distribution or multi-host
deployments.

Protect the key-ring directory with owner-only directory access (0700), preserve admitted key files and retain keys for
every live ticket and protected backup. Local Linux key XML is unencrypted. Back up the database
and key ring together under controlled custody; neither substitutes for the other. Corrupt/unprotectable sessions
invalidate safely with `OIDC_SESSION_UNREADABLE`; storage failures close authentication with `OIDC_SESSION_UNAVAILABLE`.
These diagnostics contain no provider, credential or payload.

Use `operation resolve` after uncertain mutation delivery. An accepted operation returns its author’s
receipt under current snapshot disclosure. A missing or foreign acceptance installs a permanent
actor-scoped fence and returns final `NOT_ACCEPTED`. That ID cannot accept a later request by the
same actor. The fence stores no business data. Disabled actors must be re-enabled before resolving.
`operation.observe` remains a read; absence there is not final.

## Private configuration

Private inputs are absolute valid UTF-8 paths without NUL. Symlinks follow their targets, including secret volume links.
Narrow system-library interop opens read-only with nonblocking and close-on-exec flags, then checks regular-file type
and mode on that same descriptor: no other access or group write/execute; group read (0640/0440) remains allowed.
Bounded bytes come only from that handle and callers clear buffers. Directories, devices, FIFOs, sockets,
missing/oversized files and malformed paths refuse. Supported ABIs are little-endian Linux x64/arm64 with glibc exporting public `fstat`, and
macOS x64/arm64; musl/other ABIs refuse before use. No custom native binary or C compiler is required. Nonblocking
avoids FIFO peer waits; arbitrary filesystem calls have no hard deadline. Admission freezes neither contents nor
permissions and leaves no abandoned open worker.

Ownership and ACLs are not inspected. Filesystem mode semantics, the file owner and the host administrator
remain trusted; admission does not freeze concurrent changes to contents or permissions. Preserve all notices
when distributing a published application; [Development](development.md#first-checkout) owns qualified
publication modes.

## Backups and restore

Back up PostgreSQL with ordinary platform tools and protect copies as sensitive data. Retain trusted ledger
sequence/hash checkpoints independently. After restoring, use Database verification with the expected
checkpoint before reopening actor access. A restore from before later erasure can resurrect payload; `verify
--expect` against a later trusted position refuses that older state. Identify and address affected
erasures/copies before reopening. A checkpoint can detect rollback behind it, not prove that no newer
acceptance occurred. ClaimCore does not orchestrate backup, restore, replication, copy custody or multi-party
approval, or decide when restored data is ready for use.

## Before real data

Compose is evaluation setup, not production qualification. Operators own deployment, identity, least
privilege, TLS, backup/restore tests, checkpoints, monitoring, physical retention and incident response.
Verify actual privileges and erasure effects on copies. Green CI/source review grants no owner approval or
production assurance. Keep older installations with matching software; no conversion exists.

## Serialized writes and verification growth

Accepted writes intentionally serialize on one ledger head. Verification streams rows and retains folded
case/actor/grant state. A same-snapshot SQL prepass finds the earliest duplicate operation ID, with database
sorting/spill costs. Application memory still grows with folded resource cardinality; streaming does not imply constant
total-system memory. [Synthetic reports](development.md#synthetic-growth-qualification) qualify bounded isolated
workloads, not memory/asymptotic bounds or production capacity. Timeout/cancellation guards remain enforced. More
writers do not remove serialization or promise throughput; qualify actual workloads/storage.
