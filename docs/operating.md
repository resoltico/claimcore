# Operating an installation

The production Compose template uses the released server image and operator-provided HTTPS,
identity provider and credentials. Operators manage storage, key custody, backups and monitoring.
For a separate synthetic installation, see [Getting started](getting-started.md).

`claimcore-server serve` starts the HTTPS host. The same executable supplies owner administration
subcommands; it requires a subcommand and returns usage status 64 when none is supplied. Use
`claimcore-server help` for administration and `claimcore-server serve --help` for host settings.
Owner credentials belong only in the administration environment. The runtime image contains the
server and its assets and uses the ASP.NET chiseled Ubuntu 26.04 base with ICU and time-zone data.
The image contains no shell. Run the server's administration commands directly and inspect container
files with `docker cp`. The human CLI is distributed separately.

## Install from release artifacts

Choose one version from [GitHub Releases](https://github.com/resoltico/claimcore/releases). Use the
server image digest from its notes and download `SOURCE.tar.gz` and the CLI archive for your platform.
Check each download against its entry in the release's `SHA256SUMS` before extracting it.

| Platform | CLI archive |
| --- | --- |
| Linux x64 (glibc) | `claimcore-cli-linux-x64.tar.gz` |
| Linux x64 (musl, including Alpine) | `claimcore-cli-linux-musl-x64.tar.gz` |
| macOS ARM64 | `claimcore-cli-osx-arm64.tar.gz` |

Extract the CLI archive into its own directory, keep the apphost and supporting files together, and
run `./claimcore help` there. Install the matching .NET runtime for the framework-dependent CLI. See
[global.json](../global.json) for the SDK baseline and [CLI use](integrating.md#cli-use) for configuration.

From the extracted source, use [compose.yaml](../deployment/production/compose.yaml) and copy
[production.env.example](../deployment/production/production.env.example) to a private
operator-controlled path. Set `CLAIMCORE_SERVER_SHA256` to the 64 hexadecimal digits after
`sha256:` in the release notes. The published server image runs on Linux x64. The template joins
PostgreSQL and the server on a private network and publishes the server's HTTPS port.

Set the public web origin and OIDC client settings for your issuer. Register the confidential
browser client's redirect URI at the web origin plus `/signin-oidc`. Configure the native client
and API audience for CLI access. See [CLI use](integrating.md#cli-use).

Prepare these paths under the absolute `CLAIMCORE_CONFIG_DIR`.

| Path | Contents |
| --- | --- |
| `postgres/owner.password`, `postgres/runtime.password` | Separate passwords for `claimcore_owner` and `claimcore_runtime`. Use 64 lowercase hexadecimal characters for the runtime password. |
| `postgres/server.pem`, `postgres/server.key` | PostgreSQL TLS certificate for the `postgres` hostname and its private key. Make them readable by the PostgreSQL service and restrict the key to that service. |
| `web/server.pfx`, `web/client.secret` | A passwordless PFX with the private key and publicly trusted HTTPS certificate for the web origin, and the OIDC client secret. |
| `web/runtime.connection`, `web/ca.pem` | Runtime connection string and the PostgreSQL trust root. Set `Root Certificate=/etc/claimcore/runtime/ca.pem`. |
| `administration/owner.connection`, `administration/ca.pem` | Owner connection string and the PostgreSQL trust root. Set `Root Certificate=/etc/claimcore/administration/ca.pem`. |
| `key-ring/` | Persistent Web key ring directory with mode 0700. |

Use `Host=postgres;Database=claimcore;SSL Mode=VerifyFull` in both connections, with the username and
password for that connection. Make the web and administration files readable by the configured UID
and GID, and give the key ring directory to that UID. See [Private configuration](#private-configuration).

Start PostgreSQL, initialize through the administration profile, and then start the web host.
Substitute your business zone, issuer, owner subject and owner name. Run these from the extracted
source directory with your environment file path.

```sh
docker compose --env-file /PRIVATE/production.env -f deployment/production/compose.yaml up -d --wait postgres
docker compose --env-file /PRIVATE/production.env -f deployment/production/compose.yaml \
  --profile administration run --rm administration initialize ZONE \
  --owner-issuer URL --owner-subject SUBJECT --owner-name "OWNER NAME"
docker compose --env-file /PRIVATE/production.env -f deployment/production/compose.yaml up -d --wait web
```

Use the same administration profile to run later owner commands. The web host works with the
runtime connection, and the owner connection is mounted only by the administration service.

## Install a fresh register

Use matching service, browser and CLI builds. Initialize an empty database with the current
[SQL baseline](../db/baseline.sql). ClaimCore reads installation format 8 and
[CLI recovery journal](../src/ClaimCore.Cli/Recovery/RecoveryJournal.fs) format 4. A service
or owner command checks the database against the supported
[installation format](../src/ClaimCore.RecordFormat/InstallationFormat.fs) and baseline identity
before accessing installation data. Use a fresh database when the format changes. Preserve any
existing database and recovery records separately. The available owner commands initialize,
verify, rebuild or restore matching data. There is no migration or cross-format reader.

If you used the unreleased format-8 attachments build from PR #178, preserve that database with its
matching build and use a fresh installation for this build. Both builds report format 8 and share a
SQL baseline identity. The earlier build stored a `preview` field in attachment descriptors. The
current [record codec](../src/ClaimCore.RecordFormat/PartCodecs.fs) rejects that field.

Prepare separate database-owner and runtime credentials, a protected Web key ring, an OIDC issuer
and audience, and HTTPS. The [evaluation Compose file](../deployment/compose.yaml) and
[image build](../deployment/Dockerfile) declare deployment inputs. Supported PostgreSQL versions and
image pins are in [postgresql-baseline.json](../db/postgresql-baseline.json). Framework-dependent CLI
distributions require the .NET runtime matching the SDK baseline in [global.json](../global.json);
the supplied application images include their pinned runtime.

### PostgreSQL database profile

Use PostgreSQL 19. Create the database with `UTF8` encoding and the built-in `C.UTF-8` locale.
The PostgreSQL cluster must have data checksums enabled. The Compose files initialize this profile.
On a self-managed server, create an empty database with:

```sql
CREATE DATABASE claimcore TEMPLATE template0 ENCODING 'UTF8'
  LOCALE_PROVIDER builtin BUILTIN_LOCALE 'C.UTF-8';
```

### Initialize the register

Initialize with `claimcore-server initialize ZONE --owner-issuer URL --owner-subject SUBJECT --owner-name NAME [--owner-contact TEXT] [--owner-role ROLE]...`. The canonical IANA business zone is fixed for
that installation. PostgreSQL must provide `pg_trgm` 1.6 before initialization/full restore; the owner
installs it in protected `public`. Runtime credentials receive no DDL authority.

Pass `--skip-existing` to keep a readable installation of this build and return
`INITIALIZATION_SKIPPED` with its installation ID. An empty database is initialized. Connection
and installation faults still fail the command. The local evaluation Compose file uses this option.

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
| `maintenance acquire GENERATION PAUSE_ID` | Serialize with earlier writes and pause register writes. |
| `maintenance resume GENERATION PAUSE_ID` | End the current pause after inspecting status. |
| `verify [--checkpoint FILE]` | Check retained history, projections and optionally a trusted checkpoint. |
| `rebuild --checkpoint FILE --pause-generation GENERATION --pause-id PAUSE_ID` | Rebuild projections during explicit maintenance. |
| `admit-restored-installation --checkpoint FILE --pause-generation GENERATION --pause-id PAUSE_ID` | With all traffic isolated and maintenance active, validate the trusted prefix, raise expiry and invalidate sessions atomically. |

## Recover owner access

Use the database owner connection and run:

```sh
claimcore-server recover-owner --owner-issuer URL --owner-subject SUBJECT --owner-name "OWNER NAME"
```

Supply a name when creating a new person. Use `--actor-id ID` to recover a specific person
and bind the supplied sign-in identity if it changed. Recovery enables the person and grants
installation Owner while keeping their other grants. The same command can be repeated safely.
When the target already has the requested identity and an enabled Owner grant, the result is
unchanged and the ledger does not advance. The installation must already have a person. Recovery
works during maintenance. Initialization and recovery are recorded as by the
installation operator.

Help/version are offline. Owner failures use a separate bounded envelope,
`{"kind":"OPERATOR_FAULT","fault":"CONNECTION_FILE_REQUIRED"}`. They contain no submitted values,
paths, connection strings or provider messages. Syntax omissions still return structured usage. An
application Owner alone grants no case access.

Administration registers SIGINT/SIGTERM handling before database I/O on Unix and console cancellation
on supported platforms. It awaits work and cleanup. Cancellation known before COMMIT is definite;
lost confirmation after COMMIT dispatch remains `OUTCOME_UNKNOWN`. Confirmed commit success survives
later cancellation or cleanup faults. Inspect status/checkpoint evidence before another uncertain
administrative change. SIGKILL, power loss and forced termination cannot guarantee graceful cleanup or
an output reply. Stdout remains one JSON reply; storage diagnostics use stderr.


| Fault | Operator action |
| --- | --- |
| `CONNECTION_FILE_REQUIRED`, `CONNECTION_FILE_REFUSED`, `CONNECTION_FILE_INVALID` | Set `CLAIMCORE_ADMIN_CONNECTION_FILE` to an absolute private file containing a nonempty UTF-8 connection string; check permissions. |
| `CONNECTION_INVALID`, `CONNECTION_REJECTED`, `UNSAFE_TRANSPORT` | Check connection syntax, credentials and verified TLS for remote hosts. |
| `PERMISSION_DENIED` | Use credentials with the privileges required by the owner command. |
| `TIME_ZONE_INVALID`, `INITIAL_OWNER_INVALID`, `INVOCATION_INVALID` | Check the command's help, canonical IANA zone, owner principal/profile and supported roles. |
| `ALREADY_INITIALIZED` | Inspect status; initialization does not adopt or upgrade an existing schema. |
| `UNSUPPORTED_DATABASE` | Use UTF8 encoding, the built-in C.UTF-8 locale, and a cluster with data checksums enabled. The owner command exits with code 2. Web startup reports `RUNTIME_UNSUPPORTED_DATABASE`. |
| `UNSUPPORTED_SERVER`, `SCHEMA_MISMATCH`, `FORMAT_MISMATCH`, `INSTALLATION_INVALID` | Check the supported PostgreSQL version and matching build/installation; format changes require fresh installation. |
| `CHECKPOINT_FILE_REFUSED`, `CHECKPOINT_INVALID` | Check the absolute private path, permissions and closed checkpoint JSON. |
| `MAINTENANCE_REQUIRED` | Isolate traffic as required and enter register maintenance. |
| `INTEGRITY_FAILURE`, `HEAD_MISMATCH` | Keep traffic isolated and investigate accepted history and independent checkpoints. |
| `UNAVAILABLE`, `CANCELLED`, `INTERNAL_FAILURE` | Inspect provider availability or sanitized diagnostics; preserve any earlier uncertain outcome. |

Unknown commit remains `OUTCOME_UNKNOWN` with exit 4. Failed output after confirmed mutation also
returns exit 4; inspect state before retrying. Cleanup failures are captured separately and cannot
replace a confirmed result or the primary failure. See `claimcore help exit-codes` for exit meanings.

## Register-write maintenance

Read the inactive generation from `status` before acquiring a pause and supply a fresh nonzero UUID.
Retain the returned generation and pause ID for resume, takeover, rebuild and restore admission.
`maintenance takeover GENERATION PAUSE_ID NEW_PAUSE_ID` compares the exact current handle.
Successful changes advance the generation; an exact active acquisition can be retried. A released
acquisition and an old resume are stale and have no effect. There is no expiry or automatic resume
on process exit. After an uncertain acknowledgement, inspect current status; it does not establish
historical command execution. Rebuild and restore check the expected handle after acquiring the
ledger serialization lock. The handle does not prove that external traffic is isolated.

Use database-owner credentials to pause register writes before a repair:

```sh
claimcore-server status
claimcore-server maintenance acquire EXPECTED_GENERATION PAUSE_ID
claimcore-server verify --checkpoint /PRIVATE/checkpoint.json
# Rebuild only when authoritative history is sound and projections diverge:
claimcore-server rebuild --checkpoint /PRIVATE/checkpoint.json --pause-generation PAUSE_GENERATION --pause-id PAUSE_ID
claimcore-server verify --checkpoint /PRIVATE/checkpoint.json
claimcore-server status
claimcore-server maintenance resume PAUSE_GENERATION PAUSE_ID
```

Only acquisition of the same currently active pause is retryable without changing generation. A
repeated resume or takeover with an old handle conflicts, including after a lost acknowledgement.
Inspect fresh status before deciding on another command; current state is not historical proof that
your earlier command executed. Neither process exit nor elapsed time releases a pause. Rebuild
preserves history, replaces projections atomically, reports prior divergence and leaves the pause
active. A queued rebuild or restore admission must still own the exact handle after acquiring the
common ledger head lock; it never substitutes the current operator's handle.

Maintenance pauses case, work, reminder, note and people changes. Authorized reads, authentication,
accepted-operation replay and final absence checks continue. It is not an all-host drain. For quiescence,
remove new traffic and gracefully stop every host; inspect process exit and outstanding database work.
Keep traffic isolated after an uncertain owner procedure.

Every host cleans expired sessions and access requests in bounded batches every ten minutes with up to
one minute of jitter. Hosts may clean concurrently; locked rows are skipped and expiry is rechecked.
No manual prune command is required. See [housekeeping](../src/ClaimCore.Application/Storage/Housekeeping.fs)
and the [host service](../src/ClaimCore.Server/HousekeepingService.fs) for the enforced limits and scheduling.

## Access review and sessions

Use [People and access](people-and-access.md) for account, profile and role tasks. An Owner manages
access; an Auditor with that role for all cases can review it. Inspect each account's grant pages through
the final continuation: row summaries do not list every assignment.

`claimcore people history --actor-id ID` filters the account whose access changed, rather than the
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

**End my ClaimCore browser sessions** in Settings deletes every stored browser ticket for the
currently authenticated principal, including before registration or without roles. An Owner can use
**End this person’s ClaimCore browser sessions** on the selected person's page. The application
rechecks ManageAccess against its locked action state; a displayed control does not authorize a later
request. Ordinary Sign out still ends only the current session.

These actions do not end identity-provider sessions, revoke credentials or independent bearer tokens,
or create ledger operations. Confirmed counts report deleted stored tickets, including expired ones
that had not yet been pruned. An unconfirmed acknowledgement reports no count and is not retried
automatically: repeating deletion could also end sessions created since the original attempt. Inspect
the outcome before deliberately starting another request; there is no operation.get recovery record.

Session creation and deletion serialize on the canonical principal index, including principal kind,
issuer and subject. The index is derived from the validated protected ticket, not names or sign-in
hints. Retrieval checks the decrypted ticket against this index. Renewal checks both the index and
previous protected ticket under its row lock, updates only an existing row and preserves its original
deadlines. Runtime writers cannot retarget a session's principal or rewrite its original deadlines.

The lock order is: owner action ledger/head serialization and current access; principal lock; session
rows. Creation needs no head lock and takes principal, global capacity, then cleanup/insert row locks.
Cleanup skips locked rows. Retrieve, renew and current-session logout take only their single row and
never acquire head, principal or capacity locks afterward. This avoids a reverse lock dependency.
The successful deletion commit is the cut: earlier tickets cannot be retrieved afterward; work whose
retrieval was admitted before it may finish. A callback that started earlier but creates its ticket
after that commit can survive. Self success expires the current cookie and clears private browser
workspace ownership; it does not erase durable recovery identities belonging to the principal.

Access requests contain unverified hints, grant no rights and expire after 30 days. The cache retains
at most 1,000 requests. Admission removes a matching request; dismissal grants nothing. A later sign-in
can create another request. Names become profiles only after an owner enters and confirms them.

## Daily reminder digest

An Owner opts a person in by setting their digest address. Once per business day after the
configured local time, the host can send one count-only email to each opted-in, enabled person.
The Reminders link opens the Mine view. Counts use the person's current case access and
responsibility. The digest is a daily prompt. Consult Reminders for current deadline status.

Set the following required environment variables together. Leave the group unset to disable delivery.
Startup reports missing required settings.

| Setting | Value |
| --- | --- |
| `CLAIMCORE_DIGEST_SMTP_HOST` | SMTP relay hostname or IP address. |
| `CLAIMCORE_DIGEST_SMTP_PORT` | SMTP port, from 1 to 65535. |
| `CLAIMCORE_DIGEST_SMTP_TLS` | `IMPLICIT` for a TLS connection or `STARTTLS` for a required upgrade. |
| `CLAIMCORE_DIGEST_FROM` | Sender mailbox address accepted by the relay. |
| `CLAIMCORE_DIGEST_TIME` | Local time in `HH:MM` format in the installation business zone. |

`CLAIMCORE_DIGEST_LANGUAGE` is an offered message language and defaults to `en`. For an authenticated
relay, set `CLAIMCORE_DIGEST_SMTP_CREDENTIALS_FILE` to a private UTF-8 file with the username on the first
line and password on the second. Set `CLAIMCORE_DIGEST_SMTP_CA_CERT_FILE` to a private PEM file
when the relay uses a private trust root. The relay hostname is verified in both TLS modes.
See [Private configuration](#private-configuration) for file admission. A digest worker consumes
one additional connection pool slot.

Hosts coordinate claims through the installation database. A claim allows one attempt across all
hosts. An empty queue skips delivery. Relay failures and missed days are recorded or skipped with
no resend. Monitor the digest outcomes in host logs and the `claimcore_digest_total` counter.
Logs identify the account and the outcome. The counter is grouped by outcome. Housekeeping prunes
claims older than 30 days.

## Database clock

Operation IDs have a ten-minute first-execution lifetime with two minutes of future allowance.
Accepted lookup and replay come first. `operation.get` confirms expiry under the execution head lock,
commits a monotonic `expiredBefore` cutoff, then returns final absence without a business append.
Cancellation or uncertain acknowledgement does not establish final absence; a later lookup can resolve it.
See [delivery recovery](integrating.md#recover-an-uncertain-change).

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

Take PostgreSQL physical base backups with continuous WAL archiving. Configure and test WAL
archiving before the first base backup. Retain each usable base backup and its continuous WAL
sequence from the start of the backup through the intended recovery point. Protect the archive
and retain PostgreSQL configuration, identity state and the key ring with the recovery material.
See the PostgreSQL documentation on [base backups](https://www.postgresql.org/docs/19/app-pgbasebackup.html)
and [continuous archiving](https://www.postgresql.org/docs/19/continuous-archiving.html).

Capture trusted ClaimCore checkpoints before a loss and retain them independently of PostgreSQL
and its backups. `head` emits the JSON object accepted by `--checkpoint FILE`. Save it in an
absolute private path with restricted permissions.

```sh
umask 077
claimcore-server head > /PRIVATE/checkpoint.json
```

The checkpoint binds the installation, an accepted operation prefix/hash and `expiredBefore`. An older
trusted prefix is valid in newer matching history when the current frontier is at least the trusted
frontier. Capture evidence while traffic is isolated or account for activity after capture.
Observations newer than every independently retained trusted checkpoint cannot be recovered from
that evidence.

Restore the base backup and replay archived WAL into an isolated PostgreSQL cluster with the
matching PostgreSQL major and `pg_trgm` 1.6 available. A PostgreSQL 18 physical backup must stay
with its matching build and cluster. Start with a fresh PostgreSQL 19 installation for this baseline.
Restore requires the current ClaimCore installation format. Keep application servers and all client traffic isolated, including
browser, bearer/CLI and automation traffic, throughout restore admission and verification.

With traffic still isolated, use the independently retained checkpoint to admit and verify the restored
installation:

```sh
claimcore-server maintenance acquire EXPECTED_GENERATION PAUSE_ID
claimcore-server admit-restored-installation --checkpoint /PRIVATE/checkpoint.json --pause-generation PAUSE_GENERATION --pause-id PAUSE_ID
claimcore-server verify --checkpoint /PRIVATE/checkpoint.json
# If authoritative history is sound and only projections diverge:
claimcore-server rebuild --checkpoint /PRIVATE/checkpoint.json --pause-generation PAUSE_GENERATION --pause-id PAUSE_ID
claimcore-server verify --checkpoint /PRIVATE/checkpoint.json
claimcore-server status
```

Restore admission checks the installation, accepted prefix and full retained ledger before raising
the expiry frontier to the greater current or trusted cutoff and invalidating restored sessions.
Both writes commit together. Use `verify` separately to check projections. If replay finds a
missing or mismatched prefix or invalid accepted history, keep traffic isolated and preserve the
database, recovery records and trusted checkpoints. Rebuild repairs projections only when replay
validates the retained ledger. Recover missing acceptances from trusted backup/WAL evidence. Never
lower the frontier to recover availability. Inspect status after an uncertain acknowledgement or
output failure, and keep the exact checkpoint.

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

Every runtime read, write, Context, session and housekeeping transaction checks its admitted
installation ID and canonical business zone before accessing installation data. Retargeting future
connections to a different installation fails closed; readiness becomes false. This tuple does not
detect an older restore or a clone retaining the same ID and zone, or protect against trusted-owner
schema surgery. Checkpoints, traffic isolation and restore admission remain required.

Readiness checks supported installation/schema access; liveness is separate. Neither proves all work is
idle. The server publishes the `ClaimCore` meter and bounded storage-failure diagnostics. Request metrics
use endpoint/result labels. Production storage diagnostics are bounded `storageFailure` records with
`phase`, `classification`, a distinct internal `attempt` GUID and an internal request/command
`correlation` GUID where available. Request logs share that correlation. Primary failures and cleanup
failures are distinct; diagnostics cannot replace confirmed outcomes or commit uncertainty. Records
exclude submitted values, credentials, SQL, provider messages, paths and incoming correlation strings.
Process counters reset on restart and are not a ledger inventory. Verification results include elapsed
invocation time, which is an observation rather than a response-time guarantee.

Framework exception logging retains exception types and method names instead of message/state bodies.
Request records retain endpoint, outcome, status, duration and account/operation identifiers. Treat
identifiers and private failure files as sensitive when preparing support reports.

## Erasure limits

ERASE atomically removes related case, payment, work, reminder, note, party and source-link payloads/nonces, snapshots, narratives, search,
responsibility, derived follow-up and projected financial values. It retains CaseId, reference, versions,
change time, attribution and ledger metadata. The reference itself can identify a person.
Payment projections retain accepting IDs, parent IDs and effect positions. Erasure clears payment
dates, amounts, directions, currencies, references and payees. Removal markers remain and
rebuild restores them from retained payment effects. Closure outcomes and operation attribution
remain ledger metadata. Closing notes, reopening reasons and decision bases are erasable.

Case erasure clears all attachment bytes and private file descriptors, including withdrawn and
reattached files. Verification checks retained attachment bytes against accepted commitments.
Projection rebuild repairs the list and reports authoritative byte loss as a fault requiring trusted
restore. Withdrawn files remain in the database and its backups until the applicable erasure and
backup retention policies take effect.
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
Keep the issuer, audience, client and TLS restrictions in the
[production template](../deployment/production/compose.yaml) intact.

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

Ordinary cookie authentication has two active slots and an eight-request FIFO. Each acquisition waits
at most two seconds, cancelled by client disconnect, host stop or disposal. The wait deadline ends on
promotion; active provider cleanup still owns its permit. Bearer and interactive authentication each
have separate two-slot budgets with immediate refusal. Authentication capacity is released before
actor work or actor-post reacquisition. Queue exhaustion or expiry returns overload, never a sign-out.

The browser dispatches directly and cancels obsolete reads. A 429 produces a busy notice; reads are not
automatically retried. `Retry-After: 1` is a hint, not a larger admission allowance.

Set CLAIMCORE_WEB_CORE_PERMITS and CLAIMCORE_WEB_CORE_QUEUE within the bounds declared by
[AdmissionPolicy](../src/ClaimCore.Server/Admission.fs). The private runtime connection string must
enable pooling and reserve `2 * CORE_PERMITS + 7` connections: 15 with default permits. This includes
both work pools, session storage, readiness and housekeeping. Other processes and owner tools need
separate capacity planning. POOL_CAPACITY_INSUFFICIENT identifies inadequate configuration without
exposing connection values.

Cancellation does not free a permit while provider cleanup still runs. An overload response does not
settle an earlier uncertain change. Check that change separately.

### Attachment transfer budgets

The service reserves at most two simultaneous attachment transfers per process and one per
principal. It refuses excess transfers immediately. The total host deadline is 180 seconds and the
shipped clients allow 200 seconds. Uploads are buffered within those transfer slots before taking a
database write permit. Downloads release their read snapshot before sending bytes to the client.

The maximum upload body is 25 MiB plus the configured JSON cap plus 4,636 bytes for framing.
The default maximum is 26,284,572 bytes. Multipart boundaries are limited to 70 ASCII characters.
Run the configured proxy with at least those transfer sizes and deadlines. Preserve security
headers, the original host and origin, and identity encoding. Forward downloads and previews
without response transformations. The multipart reader tolerates bounded preamble, epilogue and
delimiter whitespace within the aggregate limit.

Attachment intake applies the blocked-suffix list to the canonical saved name. The list is a
decision rule used during replay. Before 1.0, changing it requires a fresh installation. Malware
scanning is the deployment operator's responsibility. Recipients control when and how they open
downloads.

## Browser preferences and assets

Settings owns language, regional format, green/blue accent and light/dark/system preferences, including
the sign-in dialog. Preferences use `claimcore.presentation.v3`. Invalid records use defaults; invalid
individual values fall back. Direction, contrast and forced-colour preferences remain effective.
These choices do not alter facts, business dates or rights.

Publish the entire frontend build directory, including `public-assets.json`. Startup verifies hashes
and captures its served bytes; file replacement does not update a running host. Restart to replace the
interface, and retain older hashed assets while old tabs may request deferred views or languages.
Only verified assets without Set-Cookie receive immutable public caching. Runtime catalogs use strong ETags and no-cache; HTML, authentication, APIs,
refusals and fallback routes remain no-store; business data and recovery identities are not service-worker cached.
Failed language activation keeps the working language. Protect an unconfirmed change's identity before reload;
reloading cannot reconstruct a discarded request body.

The PDF viewer at `/attachment-preview/viewer` serves a startup-verified document with its own
Content Security Policy (CSP). Proxies must forward the viewer and workspace CSP headers unchanged.
Each preview runs in a fresh opaque-origin sandbox with access to the selected PDF and the renderer
resources relayed by the workspace. The viewer's CSP permits its in-frame worker and WebAssembly
image decoders. A compromised viewer could navigate its own frame and send data to an external host.
The workspace transfers the file over a private message port, and a navigated frame receives no
further data.

## Verification and reconstruction

Ordinary live checks authenticate the roots and selected facts they consume, with scalar projection
agreement for money and next due. They do not prove that the database supplied a complete child set.
Their bounded acquisition does not replace complete replay, verification, reconstruction or trusted
checkpoint custody, and aggregate queries can still scan a growing history.

Accepted writes serialize at one ledger head. Verification replays retained requests through the same
Domain decisions, using each entry's stored business date and authority at that position. It checks
hashes, commitments, installation format, erasure, effects, projections and the head. Rebuild writes
the replayed state as projections. One fold supplies the historical environment, applies effects and
checks reproduced part changes. Erased history is checked from retained inputs and available facts,
including owner/compartment consistency; cleared private values cannot be reconstructed. First-execution
windows and present maintenance are not replayed.

Unknown stored task, role or principal-kind tokens are corruption. SQL keeps structural and erasure
checks; record decoding and verification check vocabulary and meaning. VERIFIED and DIVERGED are
structured results; provider failures remain failures. A checkpoint contains `installationId`,
`operationSequence`, `operationHash` and the nullable full-precision `expiredBefore` frontier. A matching
older prefix is accepted in newer verified history; a foreign installation, missing or changed prefix,
or insufficient restored expiry frontier refuses. Restore admission requires maintenance, preserves
accepted history and cannot lower the frontier.
See [Integrating](integrating.md) for results and [Support](../SUPPORT.md) for redacted reporting.

## Licensing and modified deployments

Keep the delivered license, project/commercial notices, dependency notices and `SOURCE.tar.gz`
with each application. The web source link must work without login. Before each Compose/image build,
run `bash eng/source-archive.sh SOURCE.tar.gz`; the Web and identity images build from that captured source inventory.
A fresh evaluation realm imports nine login languages and the ClaimCore Blue login theme for the Web client.
The evaluation image preserves the Simplified and Traditional Chinese script choices when Keycloak matches login languages.
Keycloak keeps existing realm settings on restart. Operators of external issuers control their own login appearance. For a modified deployment,
rebuild the source archive and include all additional Corresponding Source required by the license;
do not point users only to an unmodified upstream release. See the [project notice](../PROJECT-NOTICE.md).
