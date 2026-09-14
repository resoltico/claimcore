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

Migration and retention credentials own schema administration and must never be used as application
credentials. Upgrading through migration 005 requires planned downtime: stop local CLI and Web
sessions, take the operator's backup, apply ordered migrations with the schema-owner Database
executable, then start current applications. The 004 provenance generalization and 005 read-only
legacy-marker grant preserve adopted records, format-2 request bytes, preparations, attempts,
settlements, lineage, and legacy provenance bytes; neither reinterprets an old operation.
Test credentials and containers must point only to disposable synthetic databases. Browser downloads
and the operating-system clipboard leave ClaimCore's process boundary; see the
[Web reference](web.md#recovery-downloads-and-clipboard).

## Data and recovery

History preserves prior facts after corrections. There is no general deletion, redaction, backup, or
restore workflow. The supplied `ClaimCore.Database prune` command removes only bounded, provably
settled or explicitly dismissed technical preparations; it never deletes accepted claim history.
Schema owners retain unrestricted administrative power outside that command. Recovery files contain
claimant data and do not prove that a command committed. Submission attempts and their definite
technical settlements are recovery evidence, not accepted claim history; an accepted operation
receipt independently proves acceptance.

Detailed recovery inspection shows actual identified attempts and their definite settlements, plus
the independent pre-003 uncertainty marker. An unset settlement or inherited marker remains explicit
even after a later definite attempt; never treat a bounded list or a momentarily absent receipt as
proof of non-commit.

For an uncertain mutation, preserve and replay only the exact CLI-v3 operation identity and retained
format-2 request or recovery-envelope bytes described in
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
