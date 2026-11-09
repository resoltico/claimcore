# Operating an installation

The supplied Compose setup is for local evaluation. Operators choose and validate their HTTPS,
identity provider, storage, key custody, backups and monitoring. Start with
[Getting started](getting-started.md) for a separate synthetic installation.

## Install a fresh register

Use matching service, browser and CLI builds. Initialize an empty database with the current
[SQL baseline](../db/baseline.sql); durable changes require a fresh installation.
[InstallationFormat](../src/ClaimCore.RecordFormat/InstallationFormat.fs) owns the single installation format.
The runtime refuses unsupported baseline identities and formats without converting them.

Prepare separate database-owner and runtime credentials, a protected Web key ring, an OIDC issuer
and audience, and HTTPS. The [Compose file](../deployment/compose.yaml) and
[image build](../deployment/Dockerfile) declare deployment inputs. Supported PostgreSQL versions and
image pins are in [postgresql-baseline.json](../db/postgresql-baseline.json).

Initialize with `claimcore-database initialize ZONE --owner-issuer URL --owner-subject SUBJECT --owner-name NAME [--owner-contact TEXT] [--owner-role ROLE]...`. The canonical IANA business zone is fixed for
that installation. PostgreSQL must provide `pg_trgm` 1.6 before initialization/full restore; the owner
installs it in protected `public`. Runtime credentials receive no DDL authority.

Set `CLAIMCORE_ADMIN_CONNECTION_FILE` to a private owner connection file. Initialization records the
human owner, authored profile and optional CASE_HANDLER, PAYMENT_RECORDER, DATA_STEWARD or AUDITOR roles
in the same transaction as the baseline. It mints the operation ID inside that transaction. The profile
is not copied from sign-in claims. A nonempty database refuses initialization; use status after an
uncertain result. Remote database connections require verified TLS.

| Owner command | Task |
| --- | --- |
| `initialize ZONE --owner-issuer URL --owner-subject SUBJECT --owner-name NAME` | Create the baseline, zone and first owner atomically. |
| `head` | Emit a reusable checkpoint with installation, operation prefix/hash and expiry frontier. |
| `status` | Read identity, formats, zone, head, maintenance and bounded counts. |
| `maintenance on` | Serialize with earlier writes and pause register writes. |
| `maintenance off` | End the current pause after inspecting status. |
| `verify [--checkpoint FILE]` | Check retained history, projections and optionally a trusted checkpoint. |
| `rebuild --checkpoint FILE` | Rebuild projections during explicit maintenance. |
| `admit-restored-installation --checkpoint FILE` | With all traffic isolated and maintenance active, validate the trusted prefix, raise expiry and invalidate sessions atomically. |

Help/version are offline. Owner failures use a separate bounded envelope,
`{"kind":"OPERATOR_FAULT","fault":"CONNECTION_FILE_REQUIRED"}`. They contain no submitted values,
paths, connection strings or provider messages. Syntax omissions still return structured usage. An
application Owner alone grants no case access.

| Fault | Operator action |
| --- | --- |
| `CONNECTION_FILE_REQUIRED`, `CONNECTION_FILE_REFUSED`, `CONNECTION_FILE_INVALID` | Set `CLAIMCORE_ADMIN_CONNECTION_FILE` to an absolute private file containing a nonempty UTF-8 connection string; check permissions. |
| `CONNECTION_INVALID`, `CONNECTION_REJECTED`, `UNSAFE_TRANSPORT` | Check connection syntax, credentials and verified TLS for remote hosts. |
| `PERMISSION_DENIED` | Use credentials with the privileges required by the owner command. |
| `TIME_ZONE_INVALID`, `INITIAL_OWNER_INVALID`, `INVOCATION_INVALID` | Check the command's help, canonical IANA zone, owner principal/profile and supported roles. |
| `ALREADY_INITIALIZED` | Inspect status; initialization does not adopt or upgrade an existing schema. |
| `UNSUPPORTED_SERVER`, `SCHEMA_MISMATCH`, `FORMAT_MISMATCH`, `INSTALLATION_INVALID` | Check the supported PostgreSQL version and matching build/installation; format changes require fresh installation. |
| `CHECKPOINT_FILE_REFUSED`, `CHECKPOINT_INVALID` | Check the absolute private path, permissions and closed checkpoint JSON. |
| `MAINTENANCE_REQUIRED` | Isolate traffic as required and enter register maintenance. |
| `INTEGRITY_FAILURE`, `HEAD_MISMATCH` | Keep traffic isolated and investigate accepted history and independent checkpoints. |
| `UNAVAILABLE`, `CANCELLED`, `INTERNAL_FAILURE` | Inspect provider availability or sanitized diagnostics; preserve any earlier uncertain outcome. |

Unknown commit remains `OUTCOME_UNKNOWN` with exit 4. Failed output after confirmed mutation also
returns exit 4; inspect state before retrying. Cleanup failures are captured separately and cannot
replace a confirmed result or the primary failure. See `claimcore help exit-codes` for exit meanings.

## Register-write maintenance

Use database-owner credentials to pause register writes before a repair:

```sh
claimcore-database status
claimcore-database maintenance on
claimcore-database verify --checkpoint /PRIVATE/checkpoint.json
# Rebuild only when authoritative history is sound and projections diverge:
claimcore-database rebuild --checkpoint /PRIVATE/checkpoint.json
claimcore-database verify --checkpoint /PRIVATE/checkpoint.json
claimcore-database status
claimcore-database maintenance off
```

Both on and off are idempotent. One operator owns the pause: inspect status immediately before turning
it off. After a lost acknowledgement, inspect fresh status rather than guessing. Maintenance does not
resume automatically. Rebuild preserves history, replaces projections atomically, reports prior divergence
and leaves the pause active.

Maintenance pauses case, work, reminder, note and authority changes. Authorized reads, authentication,
accepted-operation replay and final absence checks continue. It is not an all-host drain. For quiescence,
remove new traffic and gracefully stop every host; inspect process exit and outstanding database work.
Keep traffic isolated after an uncertain owner procedure.

Every host cleans expired sessions and access requests in bounded batches every ten minutes with up to
one minute of jitter. Hosts may clean concurrently; locked rows are skipped and expiry is rechecked.
No manual prune command is required. See [housekeeping](../src/ClaimCore.Postgres/Housekeeping.fs)
and the [host service](../src/ClaimCore.Web/HousekeepingService.fs) for the enforced limits and scheduling.

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

Keep the shared key ring protected with mode 0700 and retain keys needed by live sessions and backups.
Hosts sharing the database, key ring and application identity can share sessions. Logout takes effect
across hosts, though previously admitted work may finish. Session deadlines survive restart. Storage
or key failures close authentication; inspect sanitized diagnostics.

Access requests contain unverified hints, grant no rights and expire after 30 days. The cache retains
at most 1,000 requests. Admission removes a matching request; dismissal grants nothing. A later sign-in
can create another request. Names become profiles only after an owner enters and confirms them.

## Database clock

Operation IDs have a ten-minute first-execution lifetime with two minutes of future allowance.
Accepted lookup and replay come first. `operation.get` confirms expiry under the execution head lock,
commits a monotonic `expiredBefore` cutoff, then returns final absence without a business append.
Cancellation or uncertain acknowledgement does not establish final absence; a later lookup can resolve it.
See [delivery recovery](integrating.md#delivery-recovery).

An unseen ID strictly older than the committed cutoff cannot execute in the intact installation, even
after a backward wall-clock step. Equality is not expired. The cutoff preserves full timestamp precision.
A high cutoff can conservatively refuse newly prepared IDs after clock rollback. Check acceptance,
correct or wait for the clock, and never lower/reset the frontier to recover availability.

PostgreSQL `clock_timestamp()` still supplies actual observation/recording time; business dates are not
clamped to the expiry frontier. An arbitrary forward clock jump cannot be distinguished from legitimate
idle time. Keep clocks synchronized and retain checkpoint evidence independently of database backups;
restoring an older database can lose later accepted history and confirmed expiry observations.


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

Retain checkpoints independently of PostgreSQL and before a loss. `head` emits the canonical JSON
object accepted by `--checkpoint FILE`; save it in an absolute private path with restricted permissions:

```sh
umask 077
claimcore-database head > /PRIVATE/checkpoint.json
```

The checkpoint binds the installation, an accepted operation prefix/hash and `expiredBefore`. An older
trusted prefix is valid in newer matching history when the current frontier is at least the trusted
frontier. Protect backups and retain the matching software, identity state and key ring with recovery
material. Capture evidence while traffic is isolated or account for activity after capture. Observations
newer than every independently retained trusted checkpoint cannot be recovered from that evidence.

Restore into an empty database using a full backup containing the `pg_trgm` declaration and dependent
indexes. Data-only restore into an initialized schema is unsupported. Isolate **all** traffic before
restore admission: stop or block application servers, cookies, bearer/CLI clients and automation.
Maintenance blocks register writes but does not prove that external traffic is isolated.

With traffic still isolated, use the independently retained checkpoint, not a replacement captured from
the restored database:

```sh
claimcore-database maintenance on
claimcore-database admit-restored-installation --checkpoint /PRIVATE/checkpoint.json
claimcore-database verify --checkpoint /PRIVATE/checkpoint.json
# If authoritative history is sound and only projections diverge:
claimcore-database rebuild --checkpoint /PRIVATE/checkpoint.json
claimcore-database verify --checkpoint /PRIVATE/checkpoint.json
claimcore-database status
```

Restore admission verifies installation and accepted prefix, raises the frontier to the greater current
or trusted cutoff, and invalidates restored sessions in one transaction. A missing/mismatched accepted
prefix requires actual history recovery; advancing expiry cannot restore missing acceptances. Never
lower/reset the frontier to recover availability. An uncertain acknowledgement or failed output after
mutation requires inspecting the result; keep traffic isolated and retain the exact checkpoint.

Confirm restored cookies no longer authenticate before reopening traffic. Session invalidation alone
does not protect bearer/CLI clients; the trusted prefix and durable expiry frontier are required too.
Rebuild preserves erased identities and removal markers while keeping private contents erased. Preserve
prior divergence for investigation, and review later erasures and affected copies: a pre-erasure backup
can restore private content. Inspect restored status and turn maintenance off only after successful
verification, then reopen traffic.


## Monitoring

The host closes runtime admission before draining actor, session, readiness and housekeeping users.
Existing row/pool waiters retain their leases through provider cleanup. A shutdown bound can expire
without forcibly disposing an active provider. `RUNTIME_DRAIN_BOUND_EXPIRED` and
`RUNTIME_TEARDOWN_FAILED` are explicit shutdown failures; an earlier primary failure keeps precedence.
Late drain remains awaitable, and a failed close never reports successful drain.

Readiness checks supported installation/schema access; liveness is separate. Neither proves all work is
idle. The server publishes the `ClaimCore` meter and bounded storage-failure diagnostics. Request metrics
use endpoint/result labels; diagnostic records exclude submitted values, credentials and provider text.
Process counters reset on restart and are not a ledger inventory. Verification results include elapsed
invocation time, which is an observation rather than a response-time guarantee.

Framework exception logging retains exception types and method names instead of message/state bodies.
Request records retain endpoint, outcome, status, duration and account/operation identifiers. Treat
identifiers and private failure files as sensitive when preparing support reports.

## Erasure limits

ERASE atomically removes related case, work, reminder and note payloads/nonces, snapshots, narratives, search,
responsibility, derived follow-up and projected financial values. It retains CaseId, reference, versions,
change time, attribution and ledger metadata. The reference itself can identify a person.
Payment projections retain accepting IDs, parent IDs and effect positions. Erasure clears payment
dates and amounts; removal markers remain and rebuild restores them from public payment effects.
Verification compares available payment facts with accepted history and checks the cleared shape
after erasure; it cannot reconstruct erased facts. Authority identity and grants remain.

Profile erasure removes saved name/contact and earlier profile payloads/nonces, including private search
projections. Sign-in identity, enabled state, grants and work references remain. References show a former
account and short ID. A later profile update is a new recorded profile.

Erasure does not reach old row versions, WAL, replicas, exports, logs, URLs, backups, operator files
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

Use absolute paths to bounded, nonempty regular files. Mode 0600 is sufficient; group read is allowed,
while other-user access and group write/execute refuse. Symlinks follow their final targets. Windows
private-file admission refuses. Protect containing directories, ownership, ACLs and filesystem administrators;
the reader cannot freeze a file against a directory writer.

[PrivateFile](../src/ClaimCore.HostSecurity/PrivateFile.fs) enforces file admission and handle checks.
Use `claimcore configure --show` to inspect public configuration without opening secret contents.
See [CLI use](integrating.md#cli-use) for settings and overrides.

## Request admission

Admission is per Web process. One canonical authenticated principal has two active reads, a FIFO of
eight queued reads and one active write. Queued reads wait at most five seconds; cancellation removes
them. Caller-supplied IDs/headers cannot choose another admission identity. Completed identities leave
no partition-cache entry.

The browser dispatches directly and cancels obsolete reads. A 429 produces a busy notice; reads are not
automatically retried. `Retry-After: 1` is a hint, not a larger admission allowance.

Set CLAIMCORE_WEB_CORE_PERMITS and CLAIMCORE_WEB_CORE_QUEUE within the bounds declared by
[AdmissionPolicy](../src/ClaimCore.Web/Admission.fs). The private runtime connection string must
enable pooling and reserve `2 * CORE_PERMITS + 7` connections: 15 with default permits. This includes
both work pools, session storage, readiness and housekeeping. Other processes and owner tools need
separate capacity planning. POOL_CAPACITY_INSUFFICIENT identifies inadequate configuration without
exposing connection values.

Cancellation does not free a permit while provider cleanup still runs. An overload response does not
settle an earlier uncertain change. Check that change separately.

## Browser preferences and assets

Settings owns language, regional format, green/blue accent and light/dark/system preferences, including
the sign-in dialog. Format 3 uses `claimcore.presentation.v3`. Invalid records use defaults; invalid
individual values fall back. Direction, contrast and forced-colour preferences remain effective.
These choices do not alter facts, business dates or rights.

Publish the entire frontend build directory, including `public-assets.json`. Startup verifies hashes
and captures its served bytes; file replacement does not update a running host. Restart to replace the
interface, and retain older hashed assets while old tabs may request deferred views or languages.
Only verified assets without Set-Cookie receive immutable public caching. Runtime catalogs use strong ETags and no-cache; HTML, authentication, APIs,
refusals and fallback routes remain no-store; business data and recovery identities are not service-worker cached.
Failed language activation keeps the working language. Protect an unconfirmed change's identity before reload;
reloading cannot reconstruct a discarded request body.

## Verification and reconstruction

Accepted writes serialize at one ledger head. Verification replays retained requests through the same
Domain decisions, using each entry's stored business date and authority at that position. It checks
hashes, commitments, installation format, erasure, effects, projections and the head. Rebuild writes
the replayed state as projections. Erased payloads receive structural erasure checks; request-admission
windows and maintenance mode are not replayed.

Unknown stored act, role or principal-kind tokens are corruption. SQL keeps structural and erasure
checks; record decoding and verification check vocabulary and meaning. VERIFIED and DIVERGED are
structured results; provider failures remain failures. A checkpoint is a canonical nonnegative sequence
and lowercase hash from the verified chain, which may extend beyond it.
See [Integrating](integrating.md) for results and [Support](../SUPPORT.md) for redacted reporting.
