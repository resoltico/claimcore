# Operating an installation

The supplied Compose setup is for local evaluation. Operators choose and validate their HTTPS,
identity provider, storage, key custody, backups and monitoring. Start with
[Getting started](getting-started.md) for a separate synthetic installation.

## Install a fresh register

Use matching service, browser and CLI builds. Initialize an empty database with the current
[SQL baseline](../db/baseline.sql); durable changes require a fresh installation.
[RecordVersions](../src/ClaimCore.RecordFormat/Versions.fs) owns the accepted record tags.
The runtime refuses unsupported baseline identities and formats without converting them.

Prepare separate database-owner and runtime credentials, a protected Web key ring, an OIDC issuer
and audience, and HTTPS. The [Compose file](../deployment/compose.yaml) and
[image build](../deployment/Dockerfile) declare deployment inputs. Supported PostgreSQL versions and
image pins are in [postgresql-baseline.json](../db/postgresql-baseline.json).

Initialize with `claimcore-database initialize ZONE`. The canonical IANA business zone is fixed for
that installation. PostgreSQL must provide `pg_trgm` 1.6 before initialization/full restore; the owner
installs it in protected `public`. Runtime credentials receive no DDL authority.

Set CLAIMCORE_ADMIN_CONNECTION_FILE to a private owner connection file. Provisioning additionally uses
CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE with a fresh UUIDv7 operationId, issuer, subject and an entered
profile containing displayName and optional contact. Keep the exact input and ID for a retry.
The profile is not copied from sign-in claims. Remote database connections require verified TLS.

| Owner command | Task |
| --- | --- |
| `initialize ZONE` | Create the current baseline and business zone. |
| `provision-owner` | Create the first human owner in an empty authority installation. |
| `head` | Read the current sequence/hash position. |
| `status` | Read identity, formats, zone, head, maintenance and bounded counts. |
| `maintenance enter` | Serialize with earlier writes and enter/report maintenance. |
| `maintenance resume --generation N` | Resume the specified active generation. |
| `prune-sessions [--limit N]` | Remove expired sessions in a locked/rechecked batch. |
| `verify [--expect SEQUENCE:HASH]` | Check retained history, projections and optionally a trusted checkpoint. |
| `rebuild --expect SEQUENCE:HASH` | Rebuild projections during explicit maintenance. |
| `invalidate-restored-sessions` | Remove restored sessions before reopening traffic. |

Help/version are offline. Command errors contain structured usage issues; inspect outcomes and the
[exit codes](cli.md#exit-codes). An application Owner alone grants no case access.

## Register-write maintenance

Maintenance requires database-owner credentials, independently of application roles. Inspect `status`,
then enter maintenance explicitly. Entry holds the ledger head, so earlier writes finish before entry
commits. It pauses new case, work, reminder, authority and initial-owner changes. Replay, authorized
reads and authentication continue. A final operation-absence confirmation may take the head lock during
maintenance because it appends nothing.

Repeated entry returns the current generation. Resume requires that active generation; a later interval
advances it. After lost acknowledgement, inspect fresh status before acting again. Maintenance neither
resumes itself nor retries prepared requests. Advice is a read snapshot; execution checks current mode.

Maintenance is not database read-only mode or an all-host drain. When a task needs quiescence, remove
new traffic and gracefully stop each host. Runtime leases remain held until actual provider cleanup
finishes; cancellation and drain deadlines do not release still-running work early. Inspect process
exit and outstanding database work separately. Keep traffic isolated after an uncertain owner procedure.

Rebuild requires explicit maintenance and an independently retained checkpoint. It refuses damaged
history/head evidence and replaces projections atomically, returning prior divergence. It does not
rewrite history, invalidate sessions or resume maintenance. Verify the result before explicitly resuming.

Session pruning is separate. The default batch is 128, maximum 1000. It skips locked rows and rechecks
expiry before deletion. Observation counts can differ from committed deletion counts after concurrent
renewal/logout/pruning. Live sessions and keys remain intact.

## Access review and sessions

Use [People and access](people-and-access.md) for account, profile and role tasks. An Owner manages
access; an Auditor with that role for all cases can review it. Inspect each account's grant pages through
the final continuation: row summaries do not list every assignment.

`claimcore authority history --actor-id ID` filters the account whose access changed, rather than the
executor. Every page rechecks current rights. Case erasure does not erase grants or access history.
Review the exact sign-in identity, role and scope before recording access. Selecting a different person
does not rewrite a prepared request; starting their change requires explicit discard consent.

Retain at least one enabled human owner. Service accounts cannot receive Owner or Data steward.
Manage IdP membership separately from ClaimCore grants. Confidential service clients must use client
credentials with user flows disabled. Public human CLI clients use authorization code/PKCE with service
accounts disabled. Put the API audience in access tokens; azp/client_id alone does not establish grant type.

Protected session tickets live in PostgreSQL; cookies contain protected random lookup keys. Hosts
sharing the database, key ring and application identity can share sessions. Logout deletes the row;
renewal only updates a live row and cannot recreate logout. Previously admitted work may finish.
Database idle/absolute deadlines survive restart, retaining their original durations. New settings apply
to new sessions. Backward session-clock observations refuse authentication.

Protect the shared key-ring directory with 0700 access. Local Linux key XML is unencrypted. Retain keys
needed for live cookies, tickets, antiforgery and backups. Unprotectable tickets are invalidated;
storage failures close authentication. Diagnostics exclude credentials, ticket bodies and provider text.

Unregistered sign-ins can create temporary access requests with unverified provider hints. These are
not profiles or authority. Requests expire after 30 days; refresh removes at most 50 expired rows and
inserts retain at most 1,000 requests. Registration/provisioning removes a matching request atomically.
Only Manage people and access can view/dismiss these requests; read-only access review is insufficient.
Dismissal grants nothing, and a later sign-in can recreate the request. Provider hints never become
entered profile names automatically.

## Database clock

Operation IDs have a ten-minute first-execution lifetime with two minutes of future allowance.
Replay is checked first. `operation.get` confirms expired absence under the execution lock and appends
nothing; see [delivery recovery](integrating.md#delivery-recovery).

Final absence assumes a nondecreasing database clock. PostgreSQL `clock_timestamp()` supplies wall time;
the ledger-head lock serializes execution and final absence checks. Synchronize by slewing. Before an
intentional backward adjustment, pause writers until database time has passed its previous value.
Backward clock steps are outside this bounded-operation guarantee. Validate clock behavior for your deployment.

## Certificate, key and host replacement

Prepare the replacement certificate/private key with the configured hostname, server-authentication
purpose, supported algorithm and validity. Distribute the issuing trust chain before changing issuer.
Replace the complete PFX, stop new traffic and restart the host; file replacement does not hot-reload
its admitted certificate. Verify the actual peer certificate, hostname and readiness before returning traffic.

ASP.NET Core Data Protection owns key creation and rollover. Distribute a new key before hosts need it,
retain old keys and verify propagation to every host. A certificate change does not require key loss.
Check an old live session and a new session on each host, then check that logout is effective on peers.
Session pruning never deletes keys.

## Backups and restore

Retain sequence/hash checkpoints independently of PostgreSQL. Capture a position with
`claimcore-database head`; protect backups as sensitive data and retain the matching software,
identity state and key ring with recovery material.

Restore into an empty database and isolate it from account traffic and owner mutations. Use a full
backup containing the `pg_trgm` declaration and dependent indexes; a schema-only dump omits that extension
dependency. Data-only restore into an initialized schema is unsupported.

Run `verify --expect SEQUENCE:HASH` against the trusted checkpoint. A restore behind that checkpoint is
detectable; inspecting one primary cannot prove that no newer acceptance existed elsewhere.
If authoritative history is sound but projections diverge, enter maintenance and rebuild with the
checkpoint. Preserve prior divergence for investigation. Rebuild restores erased identities, not erased contents.

Before reopening traffic, run `invalidate-restored-sessions` and confirm restored cookies no longer
authenticate. This is an explicit restore step, separate from ordinary rebuild.
Review later erasures and affected copies: a pre-erasure backup can restore erased content.
Discard old maintenance-resume instructions and inspect the restored generation before resuming.

## Monitoring

Readiness checks supported installation/schema access; liveness is separate. Neither proves all work is
idle. The server publishes the `ClaimCore` meter and bounded storage-failure diagnostics. Request metrics
use endpoint/result labels; diagnostic records exclude submitted values, credentials and provider text.
Process counters reset on restart and are not a ledger inventory. Verification results include elapsed
invocation time, which is an observation rather than a response-time guarantee.

Framework exception logging retains exception types and method names instead of message/state bodies.
Request records retain endpoint, outcome, status, duration and account/operation identifiers. Treat
identifiers and private failure files as sensitive when preparing support reports.

## Erasure limits

ERASE atomically removes related case, work and reminder payloads/nonces, snapshots, narratives, search,
responsibility, derived follow-up and projected financial values. It retains CaseId, reference, versions,
change time, attribution and ledger metadata. The reference itself can identify a person.
Authority identity and grants remain.

Profile erasure removes saved name/contact and earlier profile payloads/nonces, including private search
projections. Sign-in identity, enabled state, grants and work references remain. References show a former
account and short ID. A later profile update is a new recorded profile.

Erasure does not reach old row versions, WAL, replicas, exports, logs, URLs, backups, provisioning files
or client retry copies. Operators manage retention, custody and physical disposal. Address affected
erasures before reopening an older restore. VOID retains contents and history.

The append-only SQL guard permits parent-scoped payload clearing for erasure. A trusted schema owner
can misuse that exception; verification detects clearing without an accepted erasure. An owner able to
rewrite the schema and all evidence is outside that protection.

## Trust model

One PostgreSQL database owns acceptance; each read uses one consistent snapshot. The IdP, service host,
database owner and backup operator are trusted. Hashes detect inconsistency against trusted retained
evidence; they do not prove real-world facts, independent freshness or administrator honesty.
There is no tenancy isolation or deployment orchestrator.

### Identity and hosting

Bearer verification checks issuer, audience and JWT type. Human subjects and service-client identities
are immutable, kind-aware identities; blank, repeated or conflicting claims refuse.
Connection, origin, credentials, media type, byte limits and antiforgery gates apply before business work.
Keep the [deployment](../deployment/compose.yaml) issuer, audience, client and TLS restrictions intact.

## Private configuration

Private paths are absolute valid UTF-8 without NUL. Symlinks follow their final targets. The reader
checks a bounded nonempty regular target before opening, then checks the opened handle's mode/length and
reads exact contents. Group read is allowed; other-user access and group write/execute refuse.
Directories, devices, FIFOs, sockets, missing, empty and oversized files refuse. Failed partial buffers
are cleared. Linux (including musl) and macOS use this admission; Windows refuses it.

The reader does not verify ownership/ACLs or freeze files and permissions. A directory writer can replace
a target during opening, and filesystem calls have no hard deadline. Operators remain responsible for
filesystem behavior and administrators. See [PrivateFile](../src/ClaimCore.HostSecurity/PrivateFile.fs).

The CLI profile uses these rules on every read. Its public members, environment precedence, file modes,
secret/trust-root bounds and exact HTTPS forms are in [CLI use](integrating.md#cli-use).
`configure --show` does not open secret contents. Shell credentials/history remain in process memory.

## Request admission

Admission is per Web process. One canonical authenticated principal has two active reads, a FIFO of
eight queued reads and one active write. Queued reads wait at most five seconds; cancellation removes
them. Caller-supplied IDs/headers cannot choose another admission identity. Completed identities leave
no partition-cache entry.

The browser dispatches directly and cancels obsolete reads. A 429 produces a busy notice; reads are not
automatically retried. `Retry-After: 1` is a hint, not a larger admission allowance.

The [admission policy](../src/ClaimCore.Web/Admission.fs) also bounds pre-authentication ingress to 24,
cookie validations to two, bearer validations to two, interactive authentication to two and session
provider calls to four. CLAIMCORE_WEB_CORE_PERMITS bounds each read and write pool (CORE_PERMITS): 1–4, default 4.
CLAIMCORE_WEB_CORE_QUEUE bounds queued writes: 1–8, default 8.
Final operation absence uses read admission plus write admission for locked confirmation.

The private runtime connection string must enable pooling, with Maximum Pool Size at least
`2 * CORE_PERMITS + 6`: 14 at defaults. This covers both work pools, session storage and readiness probes.
POOL_CAPACITY_INSUFFICIENT identifies a setup error without exposing connection values.
Other processes and owner tools need their own capacity planning.

Permits and runtime leases last through awaited provider work and cleanup. Cancellation does not free
capacity while a transaction still runs. Predispatch overload is not a final rejection of an earlier
uncertain change; check that change separately. Permission/action-state checks always execute on the server.

## Browser preferences and assets

Settings owns language, regional format, green/blue accent and light/dark/system preferences, including
the sign-in dialog. Format 3 uses `claimcore.presentation.v3`. Invalid records use defaults; invalid
individual values fall back. Direction, contrast and forced-colour preferences remain effective.
These choices do not alter facts, business dates or rights.

Publish the entire frontend build directory, including `public-assets.json`. Startup verifies hashes
and captures its served bytes; file replacement does not update a running host. Restart to replace the
interface, and retain older hashed assets while old tabs may request deferred views or languages.
Only verified assets without Set-Cookie receive immutable public caching. HTML, authentication, APIs,
refusals and fallback routes remain no-store; business data and recovery identities are not service-worker cached.
Failed language activation keeps the working language. Protect an unconfirmed change's identity before reload;
reloading cannot reconstruct a discarded request body.

## Verification and reconstruction

Accepted writes serialize at one ledger head. Verification streams the ledger and retains folded
entity state, accepted operation identities and reference indexes. It checks hashes, commitments,
versions, erasure, authority, work/reminder causality, projections and the head. Domain owns shared
lifecycle/reminder transitions. Current admission is separate from retained-record decoding.

Unknown stored act, role or principal-kind tokens are corruption. SQL keeps structural and erasure
checks; record decoding and verification check vocabulary and meaning. VERIFIED and DIVERGED are
structured results; provider failures remain failures. A checkpoint is a canonical nonnegative sequence
and lowercase hash from the verified chain, which may extend beyond it.
See [Integrating](integrating.md) for results and [Support](../SUPPORT.md) for redacted reporting.
