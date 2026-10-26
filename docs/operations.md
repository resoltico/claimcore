# Operations and trust

Plan identity, access, backup/restore and retention before using real data. The supplied Compose setup is an
evaluation environment. Operators own deployment, TLS, least privilege, monitoring and incident response.
There is no production qualification, tenancy isolation or deployment orchestrator.

One PostgreSQL database owns acceptance. Reads show one snapshot. The IdP, service host, database owner and
backup operator are trusted. Hashes detect inconsistency against retained trusted evidence; they cannot prove
real-world facts, independent freshness or honesty of an administrator rewriting everything.

For a local evaluation, follow [getting started](getting-started.md). For an existing installation,
review access and session custody first, preserve exact frames when delivery is uncertain, and verify a
restore before reopening access. Owner administration uses `claimcore-database`; actor work uses
`claimcore` or the browser.

## Erasure limits

ERASE atomically clears every case ledger payload/nonce and its business projection. It retains reference,
revision and ledger metadata. Authority history, issuer/subject, executor/target attribution and grants remain.
The retained reference itself may identify a person.

Erasure cannot reach dead row versions, WAL, replicas, exports, logs or backups. Operators decide retention,
copy custody and physical disposal. VOID preserves history and is not erasure. A restore before erasure can
resurrect data; address affected erasures before reopening access.

The append-only guard allows case-payload clearing. A trusted schema owner can use that exception without an
accepted ERASE; verification detects missing payload without an accepted erasure. The guard does not protect
against a hostile owner able to rewrite the schema and evidence.

## Access review and sessions

With installation OWNER or AUDITOR, list actor summaries, then inspect bounded grant pages for each selected
actor. Follow continuations; summaries contain no complete grant inventory. Disabled targets remain reviewable.
Every page checks current caller rights, so multiple pages are separate snapshots.

Scope grants deliberately and retain at least one enabled human installation owner. OWNER alone grants no
case work. SERVICE principals cannot receive OWNER or DATA_STEWARD. IdP membership and ClaimCore grants must
be managed independently.

Before registering a SERVICE actor, restrict its confidential IdP client to client credentials and disable user
flows. The public human CLI client uses authorization code/PKCE with service accounts disabled. Put API audience
in access tokens only. azp/client_id classification cannot establish grant type; operators must enforce these
restrictions. [Web admission](web.md#cc-web-001) checks the technical token claims.

Protected tickets live in PostgreSQL; cookies carry protected random lookup keys. Two Web instances sharing
the database and key ring can share sessions under the qualified clock conditions. Logout deletes the row;
renewal updates only an existing live row and cannot resurrect logout. Already admitted work may finish.

Database UTC idle/absolute deadlines survive restart. Each row retains its original idle duration and absolute
deadline; new deployment settings affect new sessions. Observed backward clock steps refuse, but arbitrary clock
corrections have no real-elapsed guarantee. Ticket lookups are asynchronous and independent of the ledger-head
and session-cap locks. This evidence does not qualify arbitrary multi-host/load-balancer/key distribution setups.

Protect the key-ring directory with 0700 directory access. Local Linux key XML is unencrypted. Retain keys for
live tickets and protected backups, and back up the database and key ring together under controlled custody.
Unprotectable sessions invalidate with OIDC_SESSION_UNREADABLE; storage failure closes authentication with
OIDC_SESSION_UNAVAILABLE. Diagnostics exclude payloads, credentials and provider text.

## Uncertain operation delivery

Preserve the exact frame and original ID. Resend unchanged or use operation.resolve. Resolve returns the
author's receipt with current disclosure, or installs a permanent actor-scoped fence and returns NOT_ACCEPTED.
That ID then cannot accept delayed work by the same actor. A foreign acceptance is neither revealed nor changed.
Fences contain no business payload and remain indefinitely. Disabled actors need re-enabling before resolving.
Observation is a read whose absence is not final. [CLI delivery](api.md#cc-cli-003) explains exits and frames.

## Private configuration

Use absolute valid UTF-8 paths without NUL. Symlinks follow their final targets, including secret-volume links.
The managed reader checks nonzero bounded target length before opening, then handle permissions/length and an
exact positional read. Group read is allowed; other access and group write/execute refuse. Empty/oversized
files, directories, devices, FIFOs, sockets and missing paths refuse. Callers clear returned buffers; failed
partial reads clear theirs. .NET-supported Unix platforms, including musl, are supported; Windows refuses.

The reader checks neither ownership nor ACLs and does not freeze permissions or contents. A directory writer
can substitute a FIFO between precheck and open and block startup; that principal can already replace the secret.
Arbitrary filesystem calls have no hard deadline. File ownership, filesystem semantics and administrators remain
trusted. See [PrivateFile](../src/ClaimCore.HostSecurity/PrivateFile.fs) and its real-object tests for admission.

## Backups and restore

Protect PostgreSQL backups as sensitive data and retain sequence/hash checkpoints independently. Restore into
an empty database with ordinary full-restore tools, then use Database verify with the expected checkpoint before
reopening actor access. A data-only restore into an initialized schema is unsupported.

An older restore behind the checkpoint is detectable; no primary-only inspection proves there was no newer
acceptance. Identify later erasures and affected copies before use. ClaimCore does not orchestrate backup,
restore, replication, copy custody or multi-party approval. Preserve incompatible installations with matching
software; no conversion is supplied.

## Serialized writes and verification growth

Accepted writes serialize on one ledger head. Adding writers does not remove that limit. Verification streams
entries but retains folded case/actor/grant state; its same-snapshot duplicate prepass can sort/spill in SQL.
Streaming therefore promises no constant total-system memory.

Run [synthetic growth qualification](development.md#synthetic-growth-qualification) before release and on demand,
then qualify actual storage, workloads and concurrency. Those measurements establish neither an SLA nor a
production capacity guarantee. Timeout/cancellation guards remain enforced.
