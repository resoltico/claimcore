# Operating an installation

Plan identity, access, backup/restore and retention before using real data. The supplied Compose setup is an
evaluation environment. Operators own deployment, TLS, least privilege, monitoring and incident response.
There is no production qualification, tenancy isolation or deployment orchestrator.

One PostgreSQL database owns acceptance. Reads show one snapshot. The IdP, service host, database owner and
backup operator are trusted. Hashes detect inconsistency against retained trusted evidence; they cannot prove
real-world facts, independent freshness or honesty of an administrator rewriting everything.

For a local evaluation, follow [getting started](getting-started.md). For an existing installation,
review access and session custody first, follow [delivery recovery](integrating.md#delivery-recovery) when needed, and verify a
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
restrictions. [Identity and hosting](#identity-and-hosting) checks the technical token claims.

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
an empty database with ordinary full-restore tools, then run `claimcore-database verify --expect SEQUENCE:HASH`
with the retained checkpoint before reopening actor access. A data-only restore into an initialized schema
is unsupported.

An older restore behind the checkpoint is detectable; no primary-only inspection proves there was no newer
acceptance. Identify later erasures and affected copies before use. ClaimCore does not orchestrate backup,
restore, replication, copy custody or multi-party approval. Preserve incompatible installations with matching
software. The administration tool provides no schema conversion.

## Serialized writes and verification growth

Accepted writes serialize on one ledger head. Adding writers does not remove that limit. Verification streams
entries but retains folded case/actor/grant state; its same-snapshot duplicate prepass can sort/spill in SQL.
Streaming therefore promises no constant total-system memory.

Run [synthetic growth qualification](contributing.md#growth) before release and on demand,
then qualify actual storage, workloads and concurrency. Those measurements establish neither an SLA nor a
production capacity guarantee. Timeout/cancellation guards remain enforced.

## Database administration

Use the separate `claimcore-database` tool with owner credentials. Offline help/version do not connect.
Command grammar errors return USAGE_ERROR with the applicable synopsis. REJECTED carries a diagnostic and
FAILED carries a core fault; read those details along with the shared [exit-code reference](cli.md#exit-codes).

| Command | Purpose |
| --- | --- |
| `initialize ZONE` | Atomically initialize the exact supported baseline and canonical IANA business zone. |
| `provision-owner` | Bootstrap an empty authority installation with one human owner. |
| `head` | Inspect the admitted installation's ledger position. |
| `verify` | Reconstruct retained ledger and projections under permanent invariants. |
| `verify --expect SEQUENCE:ENTRY_HASH` | Also compare an independently retained checkpoint. |

Set `CLAIMCORE_ADMIN_CONNECTION_FILE` to the private owner connection file. Provisioning additionally needs
`CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE` with operationId, issuer and subject. Keep those inputs stable for
an unchanged repeated invocation. Private-input validation precedes database access. Runtime credentials
have no schema-owner authority. Remote database connections require verified TLS.

The exact [SQL baseline](../db/baseline.sql), its checksum, installation identity and immutable zone are
checked on opening. Use a fresh database for initial setup. Initialization creates the baseline when the
`claimcore` schema is absent; repeating it requires the matching baseline and business zone. Partial,
unknown or incompatible schemas refuse untouched; there is no schema adoption or conversion. Supported
PostgreSQL majors, minimum minors and image pins are owned by
[postgresql-baseline.json](../db/postgresql-baseline.json).

Verification checks chain hashes, commitments, revisions, retained/erased payload shape, authority history,
projections and head. It does not reapply today's capability policy. VERIFIED and DIVERGED are structured
results; provider failures remain failures. Checkpoint sequences are canonical nonnegative exact integers
and hashes are lowercase hexadecimal. A checkpoint must belong to the verified chain; the ledger may extend
beyond it. The same-snapshot duplicate prepass can sort/spill, and folded resource state grows with cardinality.

## Monitoring

The server publishes the `ClaimCore` meter through `System.Diagnostics.Metrics`. Find its process with
`dotnet-counters ps`, then run:

```sh
dotnet-counters monitor --process-id PID --counters ClaimCore
```

Run the tool as the server's operating-system user. See the
[dotnet-counters reference](https://learn.microsoft.com/en/dotnet/core/diagnostics/dotnet-counters).
Counters restart with the process; they are operational observations, not a ledger inventory.

| Instrument | Unit | Tags |
| --- | --- | --- |
| `claimcore.requests` | count | `endpoint`, `result` |
| `claimcore.request.duration` | seconds | `endpoint` |
| `claimcore.ledger.append.duration` | seconds | — |
| `claimcore.ledger.head_wait.duration` | seconds | — |
| `claimcore.sessions.started` | count | — |
| `claimcore.sessions.ended` | count | `reason` |

Endpoint values are registered endpoint IDs or `unknown`. Results are `succeeded`, `refused`, `failed`
and `host_failure`; `failed` includes uncertain execution outcomes. Session-end reasons are `logout`,
`expired` and `invalid`. Missing keys do not count as ended sessions. Expiration cleanup contributes its
committed deletion count. Starts carry no ending reason. Tags contain no actor, operation, case or business
value. Ledger histograms measure append attempts and the database head-lock call, including failed waits.

For export, configure a .NET runtime metrics agent to subscribe to `ClaimCore` and send observations to your
OpenTelemetry-compatible collector. OpenTelemetry .NET automatic instrumentation supports
`OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES=ClaimCore`; follow its
[configuration guide](https://opentelemetry.io/docs/zero-code/dotnet/configuration/) for agent installation
and exporter settings. ClaimCore adds no exporter dependency.

`claimcore-database verify` includes `elapsedMilliseconds` in its `VERIFIED` and `DIVERGED` results. This is
elapsed verification time, measured by that invocation, and does not establish a response-time guarantee.

## Identity and hosting

Configure the HTTPS origin, OIDC issuer/audience and key-ring directory as described by
[getting started](getting-started.md) and the deployed process's configuration guidance. Use the supplied
[Compose definition](../deployment/compose.yaml) and [image build](../deployment/Dockerfile) as evaluation
inputs. Keep the service, browser and CLI on a matching build.

Bearer verification checks issuer, audience and JWT type. Human CLI tokens identify an immutable subject;
service clients identify their admitted client identity. Blank, repeated or conflicting identity claims
refuse. Provider client-flow restrictions above are an independent operator responsibility.

Request gates check connection, origin, credentials, media type, size and antiforgery before Application work.
Runtime leases keep resources alive through provider cleanup. Readiness checks installation/schema access
with bounded probe ownership; a deadline does not prove a hard response-time bound or freshness. Liveness
is separate. Request logs contain technical endpoint/outcome/status/duration and actor/operation identifiers,
not submitted business values, credentials or provider exception text.

See [Integrating](integrating.md) for request/result handling and the [glossary](glossary.md) for terminology.
