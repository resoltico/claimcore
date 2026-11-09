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
| `head` | Read the current sequence/hash position. |
| `status` | Read identity, formats, zone, head, maintenance and bounded counts. |
| `maintenance on` | Serialize with earlier writes and pause register writes. |
| `maintenance off` | End the current pause after inspecting status. |
| `verify [--expect SEQUENCE:HASH]` | Check retained history, projections and optionally a trusted checkpoint. |
| `rebuild --expect SEQUENCE:HASH` | Rebuild projections during explicit maintenance. |
| `invalidate-restored-sessions` | Remove restored sessions before reopening traffic. |

Help/version are offline. Command errors contain structured usage issues; inspect outcomes and the
exit-code topic: `claimcore help exit-codes`. An application Owner alone grants no case access.

## Register-write maintenance

Use database-owner credentials to pause register writes before a repair:

```sh
claimcore-database status
claimcore-database maintenance on
claimcore-database verify --expect SEQUENCE:HASH
# Rebuild only when authoritative history is sound and projections diverge:
claimcore-database rebuild --expect SEQUENCE:HASH
claimcore-database verify --expect SEQUENCE:HASH
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
Discard old maintenance instructions and inspect restored status before `maintenance off`.

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

ERASE atomically removes related case, work, reminder and note payloads/nonces, snapshots, narratives, search,
responsibility, derived follow-up and projected financial values. It retains CaseId, reference, versions,
change time, attribution and ledger metadata. The reference itself can identify a person.
Authority identity and grants remain.

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
