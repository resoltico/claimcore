# Operating an installation

The production Compose template uses the released server image and operator-provided HTTPS,
identity provider and credentials. Operators manage storage, key custody, backups and monitoring.
For a separate synthetic installation, see [Getting started](getting-started.md).

`claimcore-server serve` starts the HTTPS host. The same executable supplies owner administration
subcommands; it requires a subcommand and returns usage status 64 when none is supplied. Use
`claimcore-server help` for administration and `claimcore-server serve --help` for host settings.
Owner credentials belong only in the administration environment. The runtime image contains the
server and its assets and uses a [chiseled ASP.NET base](../deployment/Dockerfile) with ICU and
time-zone data.
The image contains no shell. Run the server's administration commands directly and inspect container
files with `docker cp`. The human CLI is distributed separately.

## Install from release artifacts

Choose one version from [GitHub Releases](https://github.com/resoltico/claimcore/releases). Use the
server image digest from its notes and download `SOURCE.tar.gz` and the CLI archive for your platform.
Verify the image and each downloaded archive against the release workflow before use. Authenticate
GitHub CLI to GitHub and your Docker client to GHCR. Replace `IMAGE_SHA256` with the 64 hexadecimal
digits from the release notes and select the CLI archive for your platform.

```sh
claimcore_release_workflow=resoltico/claimcore/.github/workflows/release.yml
gh attestation verify oci://ghcr.io/resoltico/claimcore-server@sha256:IMAGE_SHA256 \
  --repo resoltico/claimcore --signer-workflow "$claimcore_release_workflow"
gh attestation verify SOURCE.tar.gz \
  --repo resoltico/claimcore --signer-workflow "$claimcore_release_workflow"
gh attestation verify claimcore-cli-linux-x64.tar.gz \
  --repo resoltico/claimcore --signer-workflow "$claimcore_release_workflow"
```

Verify the checksum manifest against the same release workflow, then compare each downloaded
archive with its entry before extracting it.

```sh
gh attestation verify SHA256SUMS \
  --repo resoltico/claimcore --signer-workflow "$claimcore_release_workflow"
```

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
browser client's redirect URI at the web origin plus `/signin-oidc`, and its post-logout redirect
URI at the web origin plus `/`. The issuer must advertise an HTTPS `end_session_endpoint` in
discovery metadata. Keep logout query parameters out of identity-provider and proxy logs because
the request carries an ID token hint. Configure the native client and API audience for CLI access. See [CLI use](integrating.md#cli-use).

Prepare these paths under the absolute `CLAIMCORE_CONFIG_DIR`.

| Path | Contents |
| --- | --- |
| `data.key` | A private 32-byte master key file, mounted read-only by Web and administration. Generate it using a cryptographic random source and set mode 0600. |
| `postgres/superuser.password` | Separate bootstrap password for `postgres`, mounted only in PostgreSQL. Use 64 lowercase hexadecimal characters and mode 0600. |
| `postgres/owner.password`, `postgres/runtime.password` | Separate passwords for `claimcore_owner` and `claimcore_runtime`. Use 64 lowercase hexadecimal characters for each. |
| `ca.pem` | PostgreSQL trust root for the Compose database health probe. |
| `postgres/server.pem`, `postgres/server.key` | PostgreSQL TLS certificate for the `postgres` hostname and its private key. Make them readable by the PostgreSQL service and restrict the key to that service. |
| `web/server.pfx` | A passwordless PFX with one private key, its publicly trusted HTTPS leaf certificate for the web origin, and all required intermediate certificates. |
| `web/client.secret` | The confidential browser client's OIDC client secret. |
| `web/runtime.connection`, `web/ca.pem` | Runtime connection string and the PostgreSQL trust root. Set `Root Certificate=/etc/claimcore/runtime/ca.pem`. |
| `administration/owner.connection`, `administration/ca.pem` | Owner connection string and the PostgreSQL trust root. Set `Root Certificate=/etc/claimcore/administration/ca.pem`. |
| `web/witness.connection`, `administration/witness.connection` | Separate runtime and owner connections to the independent recovery witness. Use verified TLS and each role’s own credential. |
| `key-ring/` | Persistent Web key ring directory with mode 0700. |

Use `Host=postgres;Database=claimcore;SSL Mode=VerifyFull` in both connections, with the username and
password for that connection. Make the web and administration files readable by the configured UID
and GID, and give the key ring directory to that UID. See [Private configuration](#private-configuration).

Provision a dedicated PostgreSQL witness for each installation before initialization. Give its database owner the
`claimcore_witness_owner` login and create an unprivileged `claimcore_witness_runtime` login.
Use the owner to apply [witness.sql](../db/witness.sql). Restrict database connections to the
two roles, use the witness server’s own trusted TLS configuration, and preserve its storage
and recovery independently from the register. The two databases must run on independent
PostgreSQL instances. The runtime only calls the three granted witness functions.
For external primary provisioning, grant the primary owner and runtime roles execute permission on
`pg_catalog.pg_control_system()`. Grant the same function to both witness credentials in the
witness database. ClaimCore compares the connected PostgreSQL system identifiers. Install
[restore-provider.sql](../db/restore-provider.sql) as the register cluster administrator so the
register owner can verify the current timeline history. Neither application role needs replication
or general file-reading permission. The templates provision the required register permissions. The evaluation template provisions a second disposable PostgreSQL
instance with a distinct volume and credentials. It shares the evaluation host's failure domain.

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
The Compose database accepts TCP connections only for those two roles over TLS with SCRAM on the
`claimcore` database. The container validates password syntax, TLS PEM, private key ownership and permissions, key
pairing, and the certificate chain and hostname `postgres` before it launches the pinned
upstream PostgreSQL entrypoint. After the final listener starts, the startup wrapper uses the local socket
to provision a pristine database in one transaction. Restarting reuses a database interrupted
between initialization and provisioning. The observational health command verifies the database
profile, role grants and both TLS logins.

If startup finds an initialized cluster with no `claimcore` database, it stops with a missing
database diagnostic. Preserve the volume. Have the database operator inspect the cluster
through its local `postgres` control database and restore any missing data using the recovery
procedure. If the operator independently confirms an interrupted, pristine installation,
they can create the empty database as `postgres` using the profile below. Leave the
application roles absent and restart Compose to finish role provisioning.

```sql
CREATE DATABASE claimcore OWNER postgres TEMPLATE template0 ENCODING 'UTF8'
  LOCALE_PROVIDER builtin BUILTIN_LOCALE 'C.UTF-8';
```

Startup leaves database creation for an already-initialized cluster to the operator. A repeat
start checks the configured credentials without modifying them. Have the database operator
inspect partial provisioning, owner or grant drift, and credential mismatches. Preserve the
existing volume while investigating.
Compose limits each service's process and thread count. Set memory limits to suit the installation's
workload and host capacity.

## Install a fresh register

Use matching service, browser and CLI builds. Initialize an empty database with the current
[SQL baseline](../db/baseline.sql). ClaimCore reads installation format 12 and
[CLI recovery journal](../src/ClaimCore.Cli/Recovery/RecoveryJournal.fs) format 4. A service
or owner command checks the database against the supported
[installation format](../src/ClaimCore.RecordFormat/InstallationFormat.fs) and baseline identity
before accessing installation data. Initialize a fresh database for a new register. Backup
restore requires the supported format and baseline. Preserve any nonmatching database and recovery
records separately. This build refuses them without conversion.

Before cutover, resolve pending operations in each old browser profile and CLI state directory
against the matching installation and build. Keep the old database, matching keys, build and
unresolved operation IDs when settlement is unavailable. Keep browser profiles intact until their
old attempts have been settled. The new browser opens a separate recovery database with an expiry index and surfaces
earlier stores without opening or changing them. An authorized Operation ID lookup works even
when a journal entry is unreadable. There is no cross-installation settlement.

Prepare separate database-owner and runtime credentials, a protected Web key ring, an OIDC issuer
and audience, and HTTPS. The [evaluation Compose file](../deployment/compose.yaml) and
[image build](../deployment/Dockerfile) declare deployment inputs. Supported PostgreSQL versions and
image pins are in [postgresql-baseline.json](../db/postgresql-baseline.json). Framework-dependent CLI
distributions require the .NET runtime matching the SDK baseline in [global.json](../global.json);
the supplied application images include their pinned runtime.

### PostgreSQL database profile

Connection strings with `Multiplexing` or `Write Coalescing Buffer Threshold Bytes` are
refused. If you set `Application Name`, use that value when filtering the Npgsql
`db.client.connection.pool.name` metric label.

Use PostgreSQL 19. Create the database with `UTF8` encoding and the built-in `C.UTF-8` locale.
The PostgreSQL cluster must have data checksums enabled. The Compose files initialize this profile.
On a self-managed server, provision separate login roles and set their passwords securely. Create an
empty database owned by the dedicated owner role:

```sql
CREATE ROLE claimcore_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE claimcore_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE DATABASE claimcore OWNER claimcore_owner TEMPLATE template0 ENCODING 'UTF8'
  LOCALE_PROVIDER builtin BUILTIN_LOCALE 'C.UTF-8';
```

Owner commands require the current database owner with `NOSUPERUSER`, `NOCREATEROLE`,
`NOREPLICATION` and `NOBYPASSRLS`. Apply the [TLS-only access rules](../deployment/postgres-hba.conf)
to the ClaimCore database and roles when managing PostgreSQL yourself.

### Initialize the register

Initialize with `claimcore-server initialize ZONE --owner-issuer URL --owner-subject SUBJECT --owner-name NAME`.
Supply `--owner-contact TEXT` and repeat `--owner-role ROLE` as needed. The canonical IANA business zone is fixed for the installation. PostgreSQL must provide `pg_trgm` 1.6 before initialization/full restore; the owner
installs it in protected `public`. Runtime credentials receive no DDL authority.

During fresh setup, omitting `--case-reference-policy` selects `SYSTEM_ASSIGNED_ONLY`. Authorized
people and service accounts receive generated references. Set `--case-reference-policy CUSTOM_ALLOWED`
to let those callers supply a custom reference or request generation by omitting it. The database owner
chooses the policy at initialization. It is fixed for the life of the installation. Owner recovery
leaves the policy in place.

Initialization and status report the effective policy. On an existing supported installation,
`--skip-existing` returns `INITIALIZATION_SKIPPED` with its installation ID and recorded policy.
An explicitly supplied policy must match. Invalid configurations, including discrepancies with the
first accepted owner operation, refuse without changing the installation. The evaluation Compose
initializer omits the option, so a fresh evaluation installation uses generated references.

Set `CLAIMCORE_ADMIN_CONNECTION_FILE` to a private owner connection file. Initialization records the
human owner, authored profile and optional CASE_HANDLER, PAYMENT_RECORDER, DATA_STEWARD or AUDITOR roles
in the same transaction as the baseline. It mints the operation ID inside that transaction. The profile
is not copied from sign-in claims. A nonempty database refuses initialization; use status after an
uncertain result. Remote database connections require verified TLS.

| Owner command | Task |
| --- | --- |
| `initialize ZONE --owner-issuer URL --owner-subject SUBJECT --owner-name NAME` | Create the baseline, zone, case-reference policy and first owner atomically. |
| `head` | Emit a reusable checkpoint with installation, operation prefix/hash and expiry frontier. |
| `status` | Read identity, formats, zone, policy, head, maintenance, recovery publication generation and counts. |
| `maintenance acquire GENERATION PAUSE_ID` | Serialize with earlier writes and pause register writes. |
| `maintenance resume GENERATION PAUSE_ID` | End the current pause after inspecting status. |
| `verify [--checkpoint FILE]` | Check retained history, projections and optionally a trusted checkpoint. |
| `rebuild --checkpoint FILE --pause-generation GENERATION --pause-id PAUSE_ID` | Check projections under a maintenance fence and rebuild when they diverge. |
| `repair-attachment --checkpoint FILE --pause-generation GENERATION --pause-id PAUSE_ID --case-id ID --blob-id ID --payload-file FILE` | Restore the exact retained bytes for an authenticated accepting blob during maintenance. |
| `admit-restored-installation --checkpoint FILE --pause-generation GENERATION --pause-id PAUSE_ID` | Prepare and admit a separately fenced restore generation. See [Backups and restore](#backups-and-restore). |
| `rotate-encryption-generation --pause-generation GENERATION --pause-id PAUSE_ID` | Activate new cursor and durable encryption keys under the current maintenance handle. Restart all runtime processes before resuming service. |

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
| `PERMISSION_DENIED` | Use the dedicated database owner and check its ownership and role attributes. |
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

## Data-at-rest keys

Each fresh installation creates a random 256-bit data key during initialization. The database
keeps its wrapped form. Set `CLAIMCORE_DATA_KEY_FILE` in Web and administration to the
absolute path of the same private 32-byte master key file. The local configuration command
generates `data.key` with mode 0600, and both Compose templates mount it read-only. Web and
administration report a typed key fault when they cannot admit the file or unwrap the data key.

Back up the master key separately from database backups and WAL. Losing every copy of the
master key makes the encrypted attachment bytes and private ledger details unrecoverable.
Keep the key that matches each retained database recovery point, including recovery points
from before a rotation.

Attachment payloads and private ledger compartments use AES-256-GCM with fresh random
96-bit IVs. The compartment salt is encrypted too. Attachment duplicate lookup uses HMAC-SHA-256. Case snapshots, search text, notes and parties remain readable in
the projections to support search. Live erasure clears the covered records in the current
database. Existing backups, WAL, exports and copies keep the data they already hold.

Count all encryptions under a data key across its lifetime, including salts and all hosts
that use the key. [NIST SP 800-38D §8.3](https://csrc.nist.gov/pubs/sp/800/38/d/final)
sets an upper bound of 2³² invocations for random IVs. Master-key rotation rewraps the
installation data key. A fresh installation creates a different data key.

To rotate the master key, prepare a new private 32-byte file and run the owner command with
the current key still named by `CLAIMCORE_DATA_KEY_FILE`.

```sh
claimcore-server rotate-master-key --new-key-file /private/claimcore/replacement.key
```

After `MASTER_KEY_ROTATED`, configure Web and administration to use the replacement
file before their next start. Verify the installation with the replacement key. Keep the
previous key separately for older recovery points. If the rotation result is unconfirmed,
check which key authenticates the current installation before issuing another change.

## Access review and sessions

For a Keycloak realm with the ClaimCore theme installed, set the realm's login theme to
`claimcore` so both the web and native CLI clients use it.


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

**End my ClaimCore browser sessions** in Settings or on the access screen deletes every stored
browser ticket for the currently authenticated principal, including before registration or without
roles. An Owner can use **End this person’s ClaimCore browser sessions** to delete every stored
browser ticket for the selected person. The application rechecks ManageAccess against its locked
action state before deletion. Both actions are limited to ClaimCore browser tickets.
Identity-provider sessions, credentials and independent bearer tokens remain valid, and neither
action creates a ledger operation.

Confirmed counts report deleted stored tickets, including expired ones that had not yet been
pruned. Inspect an unconfirmed acknowledgement before deliberately starting another request.
It reports no count, and repeating deletion could also end sessions created since the original
attempt. These actions have no operation.get recovery record.

Ordinary Sign out deletes the current browser ticket first. When validated provider metadata
and the session's provider hint are available, the browser continues to provider sign-out.
Complete any confirmation the provider presents. During an outage ClaimCore confirms local sign-out
and reports that provider sign-out could not proceed. Session-ending actions leave independent bearer
tokens and the ledger untouched.

The configured issuer and TLS policy govern one refreshed discovery and signing-key cache. The
HTTPS listener and local readiness can come up while discovery is unavailable. Existing local
sessions continue under their normal limits. Check `GET /health/identity` separately for
identity-dependent access. It reports a bounded 503 with a sanitized dependency reason for
temporary unavailability or invalid trust. A new login or bearer request requiring unavailable
trust fails closed. Discovery and signing keys refresh without restarting the host.

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

Access requests identify the sign-in by issuer and subject. Names and email addresses are hints from
the provider. Review the email verification status and confirm the exact subject before admission.
Requests grant no rights, expire after 30 elapsed UTC days and are capped at 1,000. Admission removes a matching
request. Dismissal grants nothing, and a later sign-in can create another request. Names become
profiles only after an owner enters and confirms them.

## Disclosure trail

An Auditor with an installation-wide grant can open **Access and export log** in Web or run
`claimcore disclosure trail`. Filter by actor ID, case ID, event kind or UTC time range. Follow
the continuation to review further pages. Open a row’s details or use `--details` in the CLI
to see complete identifiers. Each authorized page read adds a trail-read event.

The trail records case detail, timeline and receipt reads, attachment downloads and previews,
exports, and refused case or attachment access. It uses actor, case and attachment identifiers.
Exports record their query, data row count, and selections such as IDs, dates and fixed choices.
Free-text searches and case-reference selectors are recorded by their presence. Lists and
searches do not add events.

A read that requires an event can release its content only after recording succeeds. A failed
trail write returns a store fault. Housekeeping prunes records older than 24 calendar months
in bounded batches using UTC. Whole-case erasure leaves trail records until that cutoff.

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

Hosts coordinate one durable attempt per recipient and business day. The database clock fences
the start and the send has a one-minute attempt budget. The durable outcomes are
`RELAY_ACCEPTED`, `FAILED_BEFORE_ACCEPTANCE`, `SKIPPED_EMPTY` and `UNKNOWN`. Acceptance confirms
the relay acknowledged submission. A later worker records expired incomplete claims as unknown.
ClaimCore does not automatically resend, including after an uncertain acknowledgement. Monitor
`digest_runs` log summaries for durable outcome counts, in-flight attempts and expired claims. The
`claimcore_digest_total` counter reports observations from the current process. Neither includes
recipient addresses or relay replies. Housekeeping prunes claims older than 30 days.

## Database clock

Operation IDs have a ten-minute first-execution lifetime with two minutes of future allowance.
Accepted lookup and replay come first. `operation.get` confirms expiry under the execution head lock,
commits a monotonic `expiredBefore` cutoff, then returns final absence without a business append.
Cancellation or uncertain acknowledgement does not establish final absence; a later lookup can resolve it.
See [Operation lookup](integrating.md#operation-lookup) for client handling in the live history and after a restore.

An unseen ID strictly older than the committed cutoff cannot execute in the intact installation, even
after a backward wall-clock step. Equality is not expired. The cutoff preserves full timestamp precision.
A high cutoff can conservatively refuse newly prepared IDs after clock rollback. Check acceptance,
correct or wait for the clock, and never lower/reset the frontier to recover availability.

PostgreSQL `clock_timestamp()` still supplies actual observation/recording time; business dates are not
clamped to the expiry frontier. An arbitrary forward clock jump cannot be distinguished from legitimate
idle time. Keep clocks synchronized and retain checkpoint evidence independently of database backups;
restoring an older database can lose later accepted history and confirmed expiry observations.

## Certificate, key and host replacement

Prepare the replacement PFX with one private key, its valid server-authentication leaf for the
configured hostname, and all required intermediates. Arrange client trust before changing the
certificate issuer. Stop new traffic, replace the complete PFX and restart the host. Verify the
served certificate chain and hostname from a client with the issuing root, then verify readiness
before returning traffic.

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

The checkpoint binds the installation, an accepted operation prefix and its hash, and `expiredBefore`.
For rebuild or restore admission, the prefix must include operation one, which retains the
case-reference policy. A sequence-zero checkpoint predates that evidence and is refused before changes
to projections, sessions or the expiry frontier. An older trusted prefix is valid in newer matching history when the current frontier is at least the trusted
frontier. Capture evidence while traffic is isolated or account for activity after capture.
Observations newer than every independently retained trusted checkpoint cannot be recovered from
that evidence.

1. Fence the installation in the independent witness before replay. Use the witness owner
   connection and retain a single fresh transition UUID and the currently active generation.
   Check the returned row and read it back after an uncertain acknowledgement. An exact retry of
   the pending fence can reuse its ID and original generation. A conflicting pending fence or a
   retired transition ID is refused without changing the active installation.

   ```sql
   BEGIN;
   SET LOCAL synchronous_commit = local;
   SELECT claimcore_witness.fence(:'installation'::uuid, :'generation'::uuid, :'transition'::uuid);
   COMMIT;
   SELECT generation, system_id, timeline, disclosure_lsn, transition, latest_dispatch, suppress_through,
          admitted_sequence, encode(admitted_hash, 'hex') AS admitted_hash, expiry_floor_ticks
   FROM claimcore_witness.installations WHERE installation = :'installation'::uuid;
   ```

2. Remove all old hosts from ingress, stop and drain them, and isolate outbound SMTP. Check that
   no worker holds an in-flight send before allowing either generation network access. Restore
   and replay into an isolated cluster on the supported PostgreSQL major with `pg_trgm` 1.6.
   For point-in-time recovery, preserve the archived WAL segments and timeline history files.
   Recover at least through the witnessed flush position on its branch. A higher position on
   a timeline that forked before that position cannot cover the old history.
3. Keep the target isolated and acquire its maintenance handle. The witness owner connection
   is read from `CLAIMCORE_WITNESS_ADMIN_CONNECTION_FILE`. Supply the original transition in
   `CLAIMCORE_RESTORE_TRANSITION`. Supply the actual archived history file for the promoted
   target in `CLAIMCORE_RESTORE_HISTORY_FILE`, using PostgreSQL's original filename in a private
   location. ClaimCore compares it against the live promoted target through the owner-only
   provider function. Preserve both copies and the recovery log as evidence.

   ```sh
   claimcore-server maintenance acquire EXPECTED_GENERATION PAUSE_ID
   claimcore-server admit-restored-installation --checkpoint /PRIVATE/checkpoint.json --pause-generation PAUSE_GENERATION --pause-id PAUSE_ID
   claimcore-server verify --checkpoint /PRIVATE/checkpoint.json
   claimcore-server status
   ```

4. If retained history or the trusted prefix differs, preserve both the target and the verification
   findings, and recover the missing data. Rebuild mismatched projections from verified history
   with the same maintenance handle. When disclosure coverage cannot be recovered, only the
   witness owner may record a gap for the original transition. Use `WAL_UNAVAILABLE` or
   `DISCONTINUOUS_HISTORY`. Review the required position and retain the recorded decision.
   Record any required gap before admission seals the decision. An unavailable witness leaves the
   transition fenced.

   ```sql
   BEGIN;
   SET LOCAL synchronous_commit = local;
   SELECT claimcore_witness.record_gap(:'installation'::uuid, :'transition'::uuid, :'reason');
   COMMIT;
   SELECT * FROM claimcore_witness.gaps WHERE installation = :'installation'::uuid;
   SELECT transition, prior_generation, fence_origin, gap_reason, sealed_at
   FROM claimcore_witness.transitions WHERE installation = :'installation'::uuid;
   ```

5. Restore admission verifies the retained ledger, projections, supplied trusted prefix and
   independently admitted prefix. The target must contain the witnessed ledger hash at its
   recorded sequence. The target's staged expiry cutoff must meet or exceed the witnessed cutoff.
   A disclosure gap waives only disclosure WAL coverage. In a new ordinary restore the owner can
   supply a newer trusted checkpoint that retains the witnessed prefix and cutoff. Admission
   reserves a local target identity, then durably seals the verified WAL coverage or recorded
   gap with that identity and the approved checkpoint and archive commitment at the witness.
   A new gap or changed reason is refused once sealed. Retry a lost reservation or seal
   acknowledgement on the original target with the same approval. The target then stages the trusted
   expiry cutoff, invalidates restored sessions, and activates fresh encryption keys before
   external activation. Local preparation stores the exact
   approved checkpoint, archive evidence, witness fence and admission result together.
   Retry uncertain preparation or activation with the same transition, target, checkpoint
   and archive evidence. A changed approval is refused and the original preparation is
   preserved. The witness binds a digest to the exact prepared target. Confirm the target
   and witness record the same active generation, the trusted checkpoint verifies and
   restored cookies no longer authenticate.
   Review later erasures and affected copies before opening traffic.

   If a sealed attempt's target is permanently lost before activation, keep ingress and SMTP
   isolated. Preserve any surviving target and preparation evidence. As the witness owner,
   compare the pending transition with the lost attempt and supply a fresh transition ID.

   ```sql
   BEGIN;
   SET LOCAL synchronous_commit = local;
   SELECT claimcore_witness.supersede_lost_target(
       :'installation'::uuid, :'lost_transition'::uuid, :'replacement_transition'::uuid);
   COMMIT;
   SELECT generation, transition, disclosure_lsn, latest_dispatch, suppress_through
   FROM claimcore_witness.installations WHERE installation = :'installation'::uuid;
   SELECT transition, supersedes, fence_origin, superseded_at, sealed_at, sealed_target
   FROM claimcore_witness.transitions WHERE installation = :'installation'::uuid;
   ```

   Check that the command returns true and the replacement is pending. If its acknowledgement
   is lost, repeat the same IDs while that replacement is pending. If the lost target had
   already activated, the command refuses. Confirm the active generation and begin the
   ordinary restore procedure with a fresh transition from that generation.

   Use the replacement ID as `CLAIMCORE_RESTORE_TRANSITION` and repeat target preparation
   from step 2, with the trusted checkpoint and the replacement target's own history file.
   The witness preserves the original WAL requirement, any recorded gap, the dispatch floor,
   and the superseded transition, approval commitment and any preparation binding. Every
   successor must use the exact originally approved checkpoint, including its operation expiry
   cutoff, with the successor's own verified history. Keep the full trusted checkpoint, archive
   evidence and any surviving local preparation separately. Restore the successor from recovery
   material that predates target reservation. The successor must independently cover that WAL
   or proceed under the original recorded gap. The coverage decision stays sealed against
   new gap decisions. The superseded target cannot activate. Keep it isolated if recovered.

6. Resume maintenance using the same handle, then reopen traffic. The owner command checks the
   current business date with the witness before clearing the pause. If the date changes before the
   command acquires the ledger head, the pause remains active and you can retry with the same handle.
   The worker suppresses dispatch through the later of the reopen business date and the latest
   date the witness previously admitted. Recovery can skip a digest. Mail relay acceptance can remain unknown.
   Restore SMTP egress only after the old hosts have been drained and isolated.

The witness prevents silent loss of acknowledged disclosure evidence under this procedure.
It cannot recreate missing WAL. A recorded gap remains visible in the witness's `gaps` view after
later restorations. The `activations` table records the accepted branch and suppression date.
Enrollment and each activation retain the admitted ledger sequence, hash and expiry cutoff.
Activation records refer to the preparations that hold those values, and each fence retains them
for every successor. This checkpoint evidence covers the state admitted at enrollment or restore.
For later business operations and expiry confirmations, retain current trusted checkpoints
independently and preserve their WAL. An ordinary runtime change alone does not advance the
witness's admitted checkpoint.
The procedure relies on independent, authenticated witness storage, trusted operators and
preserved recovery evidence. Restoring the witness along with the register defeats that
independence. Protect witness backups separately and reconcile their coverage before resuming.

`/health/ready` reports the local register, `/health/recovery` reports the independently admitted
generation, and `/health/identity` reports discovery. An unavailable witness blocks actor
operations and digest sends. Protected responses require a committed disclosure and an independent
acknowledgement. The main connection is released before waiting for that acknowledgement.

Budget reservations for random-IV durable encryption commit before taking the ledger head lock.
Reservations are never refunded. Rotate the encryption generation when the budget is exhausted.
Older generations decrypt retained data and same-format backups. Store backups and their necessary
wrapping keys together under your recovery policy. Rewrapping the installation root and rotating
the active encryption generation are separate owner operations. Rotating the active generation
requires clients to start cursor traversals again.

## Monitoring

The host closes runtime admission before draining actor, session, readiness and housekeeping users.
Existing row/pool waiters retain their leases through provider cleanup. A shutdown bound can expire
without forcibly disposing an active provider. `RUNTIME_DRAIN_BOUND_EXPIRED` and
`RUNTIME_TEARDOWN_FAILED` are explicit shutdown failures; an earlier primary failure keeps precedence.
Late drain remains awaitable, and a failed close never reports successful drain.

Every runtime read, write, Context, session and housekeeping transaction checks its admitted
installation ID, canonical business zone and case-reference policy before accessing installation data.
Changes to that tuple after startup cause the process to refuse transactions. Startup checks that the
recorded policy matches the hashed first owner operation without decrypting the profile. Compare
a separately retained checkpoint to detect changes to its trusted prefix, including a coherent
rewrite of the policy and bootstrap operation. Checkpoints, traffic isolation and restore admission
remain required.

Readiness checks supported installation/schema access; liveness is separate. Neither proves all work is
idle. The server publishes the `ClaimCore` meter and bounded storage-failure diagnostics. Request metrics
use endpoint/result labels. The `ABANDONED` request outcome has metric result `abandoned`. Fresh
write acceptances have metric result `accepted`.
Production storage diagnostics are bounded `storageFailure` records with
`phase`, `classification`, a distinct internal `attempt` GUID and an internal request/command
`correlation` GUID where available. Request logs share that correlation. Primary failures and cleanup
failures are distinct; diagnostics cannot replace confirmed outcomes or commit uncertainty. Records
exclude submitted values, credentials, SQL, provider messages, paths and incoming correlation strings.
Process counters reset on restart and are not a ledger inventory. Verification results include elapsed
invocation time, which is an observation rather than a response-time guarantee.

Framework exception logging retains exception types and method names instead of message/state bodies.
Request records retain endpoint, outcome, status, duration and account/operation identifiers.
The host records `ABANDONED` with `status: null` when the client cancels before dispatch or cancels
a read before a reply is classified. It sends no response and emits no exception or storage failure record
for that cancellation. Once classified, the reply keeps its outcome and metric even if the client leaves
before it can receive the response. Dispatched JSON writes complete independently of the client connection. Attachment transfers keep
their bounded transfer lifetime.
An unexpected exception is logged with `HOST_FAILURE`, including after a disconnect. Shutdown
cancellation continues to be logged as a host failure. Treat identifiers and private failure files as
sensitive when preparing support reports.

## Erasure limits

ERASE atomically removes related case, payment, work, reminder, note, party and source-link payloads/nonces, snapshots, narratives, search,
responsibility, derived follow-up and projected financial values. It retains CaseId, reference, versions,
change time, attribution and ledger metadata. All earlier custom references stay permanently reserved
to their case. In `CUSTOM_ALLOWED`, use identifiers free of personal data. The input grammar alone
cannot prevent personal data from being entered. Authorized registrars can infer whether a custom
value is reserved from a generic write refusal. Opening review reveals no reservation status.
Payment projections retain accepting IDs, parent IDs and effect positions. Erasure clears payment
dates, amounts, directions, currencies, references and payees. Removal markers remain and
rebuild restores them from retained payment effects. Closure outcomes and operation attribution
remain ledger metadata. Closing notes, reopening reasons and decision bases are erasable.

Case erasure clears all attachment bytes and private file descriptors, including withdrawn and
reattached files. Verification checks retained attachment bytes against accepted commitments.
Projection rebuild repairs the list. Use [exact payload repair](#repair-exact-attachment-payloads)
when the accepting record is intact and you have independently retained bytes. Use trusted restore
for damage to the accepting record or its references. Withdrawn files remain in the database and
its backups until the applicable erasure and backup retention policies take effect.
Verification compares available payment facts with accepted history and checks the cleared shape
after erasure. It uses the retained opening effect and any retained custom input to check policy,
assignment format, exact spelling, Case ID and uniqueness. The ledger does not preserve the entropy
used to generate an accepted reference or the contents of erased private facts. Authority identity and
grants remain.

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

Cancellation does not free a permit while provider cleanup still runs. Operation lookup releases
read capacity after the snapshot and provider cleanup. Definitive confirmation uses the head-write
queue while ingress stays bounded. An overload response leaves an earlier uncertain operation
unsettled. Check that change separately.

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
the sign-in dialog. Preferences use `claimcore.presentation.v3`. When saved preferences cannot be read,
ClaimCore shows a notice and uses browser defaults. Changes apply to the current session until you
choose **Reset saved preferences**, which replaces the unreadable preferences with your current
session choices. Direction, contrast and forced-colour preferences remain effective.
These choices do not alter facts, business dates or rights.

Publish the entire frontend build directory, including the private `public-assets.json` inventory.
Startup verifies and captures the workspace and sign-in recovery documents, legal notices and source
archive, build assets and catalogs listed in the inventory. Replace the complete matching asset
set and restart the host to serve it. A tab still using a different bundle may need to reload to
request the assets listed by the replacement host.
Only verified assets without Set-Cookie receive immutable public caching. Build-bound catalogs
are content-addressed and use strong ETags. HTML, authentication, APIs, refusals and fallback routes
remain no-store. Startup refuses a missing catalog or a mismatched manifest. Failed language
activation keeps the working language and offers reload. Startup, deferred workspace views and
sign-in recovery also offer reload if old assets are missing. Reload leaves the identity-only
recovery journal available. A discarded request body cannot be reconstructed.

The PDF viewer at `/attachment-preview/viewer` serves a startup-verified document with its own
Content Security Policy (CSP). The workspace requires Trusted Types for script sinks and disallows
creating policies. The service sends HSTS with a one-year lifetime and a Permissions Policy that
disables unused browser features. Proxies must forward these headers unchanged, including the
viewer's separate CSP.
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
the replayed state as projections. It prepares and checks the replay in a repeatable-read snapshot.
Attachment evidence is read in bounded pages ordered by accepting position in that snapshot.
Publication rechecks the active pause, head, expiry frontier, key admission and recovery
publication generation before replacing and reading back projections in one transaction. If the
preparation is stale, inspect status and rerun with current evidence. After a lost acknowledgement,
inspect the trusted checkpoint, maintenance and publication generation before any further action.
Replay supplies the environment for each recorded operation and checks reproduced part changes. Erased history is checked from retained inputs and available facts,
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

A healthy register returns `PROJECTIONS_CURRENT` after checking the fence. This path leaves both
the projections and the recovery publication generation intact. For damaged projections, the
command streams rows into transaction-local tables before acquiring the head. The live
replacement uses one delete and one insert per derived table, then verifies the live result
before committing. Existing MVCC readers keep their snapshot.

Table replacement, constraints, principal locks and readback still process installation data
while the head is held. Plan maintenance for that work.

### Repair exact attachment payloads

Use the protected attachment finding from `verify` to identify the case, blob and accepting
position. Use independently retained bytes and the owner connection during active maintenance.
Keep the payload file, checkpoint and command output private. The repair checks live retention,
the accepted descriptor and the supplied bytes, then encrypts the replacement and verifies
readback. It preserves the original accepting position. Damage to accepting history, identity
or references requires restore. A retained erasure cannot be reversed by payload repair. Verify
against the trusted checkpoint before resuming traffic.

## Licensing and modified deployments

Keep the delivered license, project/commercial notices, dependency notices and `SOURCE.tar.gz`
with each application. The web source link must work without login. Before each Compose/image build,
run `bash eng/source-archive.sh SOURCE.tar.gz`; the Web and identity images build from that captured source inventory.
A fresh evaluation realm imports nine login languages and sets the `claimcore` login theme for both the web and CLI clients.
The evaluation image preserves the Simplified and Traditional Chinese script choices when Keycloak matches login languages.
Keycloak keeps existing realm settings on restart. Operators of external issuers control their own login appearance. For a modified deployment,
rebuild the source archive and include all additional Corresponding Source required by the license;
do not point users only to an unmodified upstream release. See the [project notice](../PROJECT-NOTICE.md).
