# Operating an installation

Start with a fresh installation, grant access deliberately, and plan backups and retention before recording
real data. The supplied Compose setup is for local evaluation. Operators choose and qualify their deployment,
TLS, storage, identity provider and monitoring.

## Install a fresh register

Use [getting started](getting-started.md) for a separate local installation. For another deployment, prepare
HTTPS, an OIDC issuer/audience, a protected Web key ring, and separate database-owner and runtime credentials.
The [Compose definition](../deployment/compose.yaml) and [image build](../deployment/Dockerfile) show the
required inputs. Keep service, CLI and browser on matching builds and regenerate integration consumers
from the checked contracts.

Initialize an empty database with `claimcore-database initialize ZONE`. The canonical IANA business zone is
fixed for that installation. Provision its first human owner with `claimcore-database provision-owner`, then
grant the case roles needed for actual work. OWNER alone grants no case viewing or editing.

### Database administration

Set `CLAIMCORE_ADMIN_CONNECTION_FILE` to a private owner connection file. Provisioning also needs
`CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE`, containing operationId, issuer and subject. Keep those inputs
unchanged when repeating the same provisioning operation. Remote database connections require verified TLS.
Runtime credentials have no schema-owner authority. Offline help/version do not connect.

| Command | Purpose |
| --- | --- |
| `initialize ZONE` | Initialize the exact SQL baseline and immutable business zone. |
| `provision-owner` | Bootstrap an empty authority installation with one human owner. |
| `head` | Read the installation's current sequence/hash position. |
| `verify` | Reconstruct retained ledger state and compare projections and head. |
| `verify --expect SEQUENCE:ENTRY_HASH` | Also check an independently retained checkpoint. |

Command errors return `USAGE_ERROR` with the applicable syntax. Read `REJECTED` diagnostics and `FAILED`
faults alongside the shared [exit-code reference](cli.md#exit-codes).

The [SQL baseline](../db/baseline.sql), its checksum, installation identity and business zone are checked
when storage opens. Repeating initialization requires the matching baseline and zone. Partial, unknown or
incompatible schemas are refused unchanged. Initialization does not migrate or adopt a different schema;
only the matching baseline and durable formats are supported. Supported PostgreSQL versions and image pins
are owned by
[postgresql-baseline.json](../db/postgresql-baseline.json).

## Access review and sessions

With installation OWNER or AUDITOR, list actors and inspect each selected actor's grant pages. Follow every
continuation; an actor summary is not a complete grant inventory. Disabled actors remain reviewable. Each
page checks current rights and uses a separate snapshot.

Use `claimcore authority history` to audit authority changes newest first. Installation OWNER and
AUDITOR may read the history; a case-scoped grant does not grant installation authority review.
Use `--actor-id ID` to select the target actor, rather than the executing actor. Each entry retains
its ledger sequence, UTC recording time, executor, target and typed principal or grant facts. Follow
`nextCursor` to continue. Every page rechecks current rights inside its read snapshot, including after
revocation. History payloads are permanent authority records; erasing a case does not erase its grants
or authority history. CSV export protects formula-leading cells; JSON preserves exact values.

Selecting an actor starts its offered GRANT action when available, with the disclosed principal bound
to that actor. Review the target, role and scope before confirming. Selecting another actor does not
rewrite an authored, prepared or unresolved request. Starting that actor's action requires the normal
discard acknowledgement and creates a fresh request; declining keeps the original target and request.

Scope grants to the work required and retain at least one enabled human installation owner. SERVICE
principals cannot receive OWNER or DATA_STEWARD. Manage IdP membership and ClaimCore grants separately.
Before registering a SERVICE actor, restrict its confidential IdP client to client credentials and disable
user flows. The public human CLI client uses authorization code/PKCE with service accounts disabled. Put the
API audience in access tokens only; azp/client_id claims do not establish the grant type.

Protected session tickets live in PostgreSQL. Cookies contain protected random lookup keys. Two Web
instances sharing the database and key ring can share sessions under the qualified clock conditions.
Logout deletes the session row; renewal updates an existing live row and cannot recreate logout. Work
already admitted before logout may finish.

Database UTC idle and absolute deadlines survive restarts. Each row keeps its original durations; new
settings affect new sessions. Backward clock observations refuse authentication. Arbitrary clock changes,
load balancers and key-distribution arrangements require deployment-specific qualification. Session lookup
waits for storage asynchronously and does not take the ledger-head or session-creation-cap lock.

Protect the key-ring directory with 0700 access. Local Linux key XML is unencrypted. Preserve keys for live
tickets and protected backups, and back up the database and key ring together. Unprotectable tickets are
invalidated with `OIDC_SESSION_UNREADABLE`; storage failure closes authentication with
`OIDC_SESSION_UNAVAILABLE`. Diagnostics exclude payloads, credentials and provider text.

## Backups and restore

Retain sequence/hash checkpoints independently of the database and protect backups as sensitive data.
Capture a position with `claimcore-database head` and use your PostgreSQL full-backup tools. Preserve the
matching software, identity state and protected key ring with the installation's recovery material.

Restore into an empty database, then run `claimcore-database verify --expect SEQUENCE:HASH` with the retained
checkpoint before reopening actor access. A data-only restore into an initialized schema is unsupported.
An older restore behind the checkpoint is detectable; inspecting one primary cannot establish that no
newer acceptance existed elsewhere.

Check later erasures and affected copies before using a restored database. A pre-erasure backup can bring
back erased content. ClaimCore does not orchestrate backup, restore, replication, copy custody or approvals.

## Monitoring

The server publishes the `ClaimCore` meter through `System.Diagnostics.Metrics`. Install `dotnet-counters`
using the [dotnet-counters reference](https://learn.microsoft.com/en/dotnet/core/diagnostics/dotnet-counters).
Run it as the server's operating-system user; on Linux/macOS it must share the server's `TMPDIR`.
For a container outside the tool's process namespace, follow the reference's diagnostic-port setup.
Find the server process with `dotnet-counters ps`, then run:

```sh
dotnet-counters monitor --process-id PID --counters ClaimCore
```

Counters restart with the process; they are operational observations, not a ledger inventory.

| Instrument | Unit | Tags |
| --- | --- | --- |
| `claimcore.requests` | count | `endpoint`, `result` |
| `claimcore.request.duration` | seconds | `endpoint` |
| `claimcore.ledger.append.duration` | seconds | — |
| `claimcore.ledger.head_wait.duration` | seconds | — |
| `claimcore.sessions.started` | count | — |
| `claimcore.sessions.ended` | count | `reason` |

Endpoint values are registered endpoint IDs or `unknown`. Results are `succeeded`, `refused`, `failed` and
`host_failure`; `failed` includes uncertain execution outcomes. Session-end reasons are `logout`, `expired`
and `invalid`. Missing keys do not count as ended sessions. Expiration cleanup contributes its committed
deletion count. Starts carry no ending reason. Tags contain no actor, operation, case or business value.
Ledger histograms measure append attempts and the database head-lock call, including failed waits.

For export, configure a .NET runtime metrics agent to subscribe to `ClaimCore` and send observations to your
OpenTelemetry-compatible collector. OpenTelemetry .NET automatic instrumentation exposes the experimental
setting `OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES=ClaimCore`; follow its
[configuration guide](https://opentelemetry.io/docs/zero-code/dotnet/configuration/) for agent installation
and exporter settings. ClaimCore adds no exporter dependency.

`claimcore-database verify` includes `elapsedMilliseconds` in `VERIFIED` and `DIVERGED` results. This is
elapsed verification time for that invocation, not a response-time guarantee.

## Erasure limits

ERASE atomically clears every case ledger payload/nonce, its snapshot, and projected status, progress and
`search_text`. It retains reference, revision, `changed_at` and ledger metadata. Authority history,
issuer/subject, executor/target attribution and grants remain. The reference itself may identify a person.

Erasure does not reach dead row versions, WAL, replicas, exports, logs, browser history/shared URLs or
backups. Operators decide retention,
copy custody and physical disposal. VOID preserves history. A pre-erasure restore can resurrect content;
address affected erasures before reopening access.

The append-only guard permits case-payload clearing. A trusted schema owner can use that exception without
an accepted ERASE; verification detects the missing erasure. A hostile owner able to rewrite the schema and
all evidence remains outside that protection.

## Trust model

One PostgreSQL database owns acceptance and each read uses one snapshot. The IdP, service host, database
owner and backup operator are trusted. Hashes detect inconsistency against retained trusted evidence; they
cannot prove real-world facts, independent freshness or an administrator's honesty. There is no tenancy
isolation or deployment orchestrator.

### Identity and hosting

Bearer verification checks issuer, audience and JWT type. Human CLI tokens identify an immutable subject;
service tokens identify an admitted client. Blank, repeated or conflicting identity claims refuse. The IdP
flow restrictions in [access review](#access-review-and-sessions) remain an operator responsibility.

Request gates check connection, origin, credentials, media type, size and antiforgery before case work.
Readiness checks installation/schema access; liveness is separate. A timeout is not proof of a hard
response-time bound or freshness. Request logs contain endpoint, outcome, status, duration and
actor/operation identifiers, not submitted business values, credentials or provider exception text.

## Private configuration

Use absolute valid UTF-8 paths without NUL. Symlinks follow their final targets, including secret-volume
links. The reader checks a bounded, nonempty final target before opening, then validates the opened handle's
permissions and length and reads its exact contents. Group read is allowed; other access and group
write/execute refuse. Empty/oversized files, directories, devices, FIFOs, sockets and missing paths refuse.
Callers clear returned buffers and failed partial reads clear theirs. Supported .NET Unix platforms include
musl; Windows refuses private-file admission.

The reader does not check ownership or ACLs, or freeze file contents and permissions. A directory writer
can replace a target during opening; that principal can already replace the secret. Filesystem operations
have no hard deadline. File ownership, filesystem behavior and administrators remain trusted. See
[PrivateFile](../src/ClaimCore.HostSecurity/PrivateFile.fs) and its real-object tests.

## Serialized writes and verification growth

Accepted writes serialize on one ledger head. More writers do not remove that limit. Verification streams
entries while retaining folded case/actor/grant state; its same-snapshot duplicate prepass may sort or spill
in SQL. Streaming does not promise constant total-system memory.

Verification checks chain hashes, commitments, revisions, retained/erased payload shape, authority history,
all worklist projection columns and the head. It applies permanent invariants rather than today's admission
policy. Unknown stored act, role or principal-kind tokens are corruption; verification reports divergence.
`VERIFIED` and `DIVERGED` are structured results; provider failures remain failures. Checkpoint sequences
are canonical nonnegative integers and hashes are lowercase hexadecimal. The checkpoint must belong to the
verified chain; the ledger may extend beyond it.

Select [synthetic growth measurements](contributing.md#growth) for the performance question or agreed
release scope. Qualify actual storage, workloads and concurrency separately; synthetic measurements
establish neither an SLA nor production capacity.

See [Integrating](integrating.md) for result handling and [Support](../SUPPORT.md) for redacted reporting.
