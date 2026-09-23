# Security and operations

## Current trust model

ClaimCore assumes a single trusted local administrative boundary and one installation-wide,
case-sensitive reference namespace. It has a shared bootstrap admission secret and in-memory browser
sessions, but no individual operator identities, per-user authentication or authorization,
multi-tenancy, remote-access design, approval workflow, or nonrepudiation.

The runtime database role can bypass some domain transitions through direct SQL. Internal F# types
prevent accidental API bypass; they do not defend against hostile reflection, substituted assemblies,
administrators, or anyone holding database credentials.

Do not expose CLI, the native core, the loopback Web host, or database credentials to untrusted
clients. Shared or remote use requires a separately designed authenticated service with command
authorization and disclosure rules.

## Credentials and sensitive surfaces

- Application environment variables select private files rather than carrying raw connection strings
  or credentials. Local Compose initialization reads its two passwords from the ignored `.env` file;
  they remain secrets even though Compose passes them to the container environment.
- Keep connection, certificate, Web state, recovery, download, and diagnostic files in ignored,
  owner-controlled locations.
- Do not print or upload connection strings, passwords, request bodies, claimant data, cookies,
  clipboard contents, downloaded recovery envelopes, canonical recovery records, or private terminal
  captures. CLI delivery diagnostics may contain only an operation ID, a digest, and safe recovery
  direction after mutation admission.
- Local Compose database connections may disable TLS only while bound to its loopback development
  port. Any non-local PostgreSQL connection requires authenticated TLS and managed secrets; ClaimCore
  still provides no supported remotely accessible application service.
- Private-file runtime operations are supported on macOS and Linux only. Windows source builds,
  tests, and database-free discovery work, but CLI, Web, and Database private-file operations fail
  closed until an independently verified Windows handle/ACL implementation exists.
- On macOS, use physical canonical paths; system aliases such as `/var` and `/tmp` have linked
  ancestors and are intentionally refused. An ignored repo-local `.local` under a physical `/Users`
  checkout is a suitable private location when its permissions and retention are controlled.

Initialization and retention credentials own schema administration and must never be application
credentials. This build intentionally refuses every old installation without changing its data.
Retain old databases and artifacts under their compatible software; provision a separate fresh
installation using `ClaimCore.Database initialize <canonical-IANA-ID>`. There is no automatic
upgrade, reset, deletion, database-content import or artifact conversion. See the
[installation boundary](database.md#fresh-installation-boundary).
Test credentials and containers must point only to disposable synthetic databases. Browser downloads
and the operating-system clipboard leave ClaimCore's process boundary; see the
[Web reference](web.md#recovery-downloads-and-clipboard).

## The installation calendar

The fresh initializer stores one mandatory canonical IANA zone atomically with installation
lineage. Every business date comes from that stored zone and one
captured instant, never from a host default. The identifier is validated when it is set and again on
every runtime opening: it must be enumerable on the host, must resolve, and must resolve back to
exactly itself, and must be an IANA identifier rather than a platform-native one, so an alias, a Windows identifier, an abbreviation, or an offset literal is refused on every host.
A host that cannot resolve the stored zone refuses to open the runtime rather than producing a date
from some other calendar.

That validation binds the identifier, not the rules behind it. **ClaimCore resolves the zone through
the host's IANA time-zone database, so the offsets and daylight-saving rules it uses are the host's.**
Two hosts running different tzdata releases can therefore derive different business dates for the
same instant, but only for an operation that falls inside a transition whose rules changed between
those releases. The same applies to one host across an operating-system update.

Treat the time-zone database as part of the installation:

- Keep the hosts that serve one installation on the same operating-system time-zone data, and update
  them together.
- After a tzdata update, prefer a quiet period before resuming case work, for the same reason a
  restore needs one.
- `ClaimCore.Database initialize` refuses a calendar different from the installed one. Choosing a
  different calendar is a new installation decision, not an edit.

ClaimCore does not ship its own time-zone database and does not detect tzdata skew between hosts.
Nothing in the product weakens this by running with invariant globalization, which would remove IANA
zone resolution altogether.

## Data and recovery

History preserves prior facts after corrections. There is no general deletion, redaction, backup, or
restore workflow. The supplied `ClaimCore.Database prune` command removes only bounded accepted or
durably revoked technical preparations; it never deletes accepted claim history or durable revocation
authority.
Schema owners retain unrestricted administrative power outside that command. Recovery files contain
claimant data and do not prove that a command committed. Submission attempts and their definite
technical settlements are recovery evidence, not accepted claim history; an accepted operation
receipt independently proves acceptance.

Detailed recovery inspection pages actual identified attempts and definite settlements. An unsettled
attempt stays unsettled even after a later definite attempt and excludes its preparation from
pruning. Never treat a bounded list or a momentarily absent receipt as proof of non-commit. Durable
revocation prevents future unaccepted execution without rewriting earlier uncertainty. Historical
unidentified-start and dismissal compatibility is absent because old installations are refused;
there is no inferred or retroactively fabricated authority transfer.

For an uncertain mutation, preserve and replay only the exact CLI-v3 operation identity and retained
format-3 request or format-2 recovery-envelope bytes described in
[CLI and protocol](cli.md#canonical-request-identity-and-recovery). Do not infer failure from missing
output, a delivery loss, or a momentarily absent receipt. A restored database keeps its installation
lineage and retained preparations, so recovery exports remain installation-bound after restoration.
Lineage is not a freshness proof: restoring an older backup can remove later accepted receipts.
Exact replay can establish only the accepted history present in the current database. ClaimCore has
no automatic backup/restore workflow or cross-restore rollback fence; an operator must reconcile a
restore against independently kept backup and operation evidence before resuming case work.

The payment command records an operator assertion; ClaimCore does not transfer funds or contact a
provider. Future external effects require durable intent, provider idempotency, acknowledgements, and
reconciliation.

## Before real data

Before processing personal or operational data, establish and test:

- successful CI and deployment qualification for the exact revision;
- credential custody, host and network access, monitoring, and protected logs and exports;
- a defined business timezone and operator-identity model;
- encrypted backups, restoration drills, retention, deletion, and incident response;
- recovery from a lost commit acknowledgement;
- applicable legal, regulatory, and organizational controls.

ClaimCore supplies technical CI qualification and exact-request recovery, but not the surrounding
identity, access, secret-custody, monitoring, backup, restoration, retention, deletion, legal, or
incident-response processes. Passing tests does not establish those controls or constitute a
security certification.
