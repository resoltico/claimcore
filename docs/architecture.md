# Architecture

ClaimCore is one HTTPS service over one PostgreSQL authority. Browser and CLI clients carry
no database credentials. Domain owns business and stewardship decisions; Application owns
identity, authorization and outcomes; the store executes pure decisions against locked state.

<a id="cc-arch-001"></a>
### CC-ARCH-001 — Component graph and compiled boundaries

[The architecture manifest](../config/architecture.json) owns direct project references in both
directions. Domain, RecordFormat, Application and Contracts call no clock, file, environment,
randomness or network APIs. Only Hosting and Database reference Postgres; only Web references
Hosting. These referencer rules concern other projects, not a component's own types.
TimeZoneInfo belongs only to Hosting, Database and Postgres. Compiled negative fixtures prove
each rule rejects a forbidden dependency or call. Internals grants name a permitted referencer
or an actual test project. Test projects reference only product or tooling projects.

RecordFormat owns canonical commands, snapshots, stewardship, authority actions and ledger
metadata encoding. Contracts owns the fourteen-endpoint catalogue, request/response schemas,
strict codecs, fingerprint and generated client types. HostSecurity admits private files.
Postgres owns transactions, projection persistence, schema administration and verification.
Hosting owns case-work composition and runtime lifetime. Web binds authenticated principals;
Cli delivers generated-contract frames; Database is the separate owner administration process.
Database references Application to validate principals and render shared ledger/authority values,
so it does not duplicate their definitions. ContractGenerator derives artifacts from Contracts.

<a id="cc-run-001"></a>
### CC-RUN-001 — Runtime admission and shutdown

A runtime lease keeps admitted work alive. Shutdown closes admission, drains admitted leases
within its bound and closes the source once. Expiry does not relabel an operation's outcome or
cancel an already admitted durable mutation. Readiness is bounded installation/schema access,
not a claim of ledger freshness or an operator approval.

<a id="cc-auth-001"></a>
### CC-AUTH-001 — Actor-bound authorization and disclosure

ClaimCore's five roles and nine capabilities are defined once in Application. Grants are
installation-wide or case-scoped, default-deny, and checked under the write lock or in the
read snapshot. Identity-provider membership and a database role grant no case authority.
Only humans receive OWNER or DATA_STEWARD; at least one enabled human installation owner
must remain. OWNER is authority administration and does not by itself grant case work.
Absent, inaccessible and erased-beyond-view cases or operations have the same public refusal.
Cursors are opaque, principal/grant/query-bound, short-lived and checked against current grants.

<a id="cc-app-001"></a>
### CC-APP-001 — Rejection changes no accepted state

A rejection appends nothing and changes no projection. Writers first lock the singleton ledger
head, then read authority, operation identity and case state in the same READ COMMITTED
transaction. The captured database instant and stored zone determine the business date.

<a id="cc-app-002"></a>
### CC-APP-002 — Exact replay and commit uncertainty

Every accepted case operation and authority change appends exactly one ledger entry in the
transaction updating its projections. Exact replay returns the original accepted result;
different content under the same ID is refused. OUTCOME_UNKNOWN means a commit confirmation
was lost. It does not prove rejection and never permits rebasing or replacement identity.
Erased request content is unavailable. Reads use one REPEATABLE READ snapshot.
