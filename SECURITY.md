# Security policy

## Report privately

Use this repository's GitHub Security → Advisories → Report a vulnerability flow. Include
the affected revision, impact and a minimal synthetic reproduction. Never send active
credentials, claimant data or unredacted database extracts, including in a private report.
Rotate exposed credentials through the operator's process.

If private reporting is unavailable, open a public issue asking for a private reporting path.
Do not disclose the vulnerability, affected component or reproduction in that issue.

## Trust boundary

OIDC identifies human and service principals. Scoped actor grants are default-deny and
rechecked against authoritative locked state. Browser sessions use secure cookies and
antiforgery admission. The browser and CLI are HTTPS clients and receive no database credentials.
Database administration uses a separate owner credential and executable.

The [private-input policy](docs/operations.md#private-configuration) allows group read and follows
secret-volume symlinks; it does not inspect ownership or ACLs. Protect the required Web key-ring
directory and its encrypted server-side session tickets as a secret outside database backups.
Sessions retain their deadlines across a single Web process restart; concurrent processes sharing
tickets are not qualified. See [session operation](docs/operations.md#access-review-and-sessions).

The identity provider, service host, database owner and backup operator remain trusted.
Ledger hashes detect inconsistency against trusted retained evidence. They do not prove
independent freshness or protect against rewriting all evidence. Erasure clears live case
payloads; it cannot reach dead row versions, WAL, replicas, exports or backups, and the
retained case reference may still identify someone.

The included loopback Compose installation is for evaluation. Operators own deployment,
TLS, identity administration, monitoring, backup/restore, retention and incident response.
Read [Operations](docs/operations.md). The project supplies no multi-tenancy or production
qualification. The repository promises no security fixes for historical revisions;
identify the affected revision and reproduce against maintained source where possible.

Format invariants preserve retained history independently of current admission policy. Current
snapshot disclosure needs case read; superseded snapshots need full history. Current actor grants
and the result come from the same locked/read context, including accepted replay.

PostgreSQL image identity is pinned in `db/postgresql-baseline.json`; ClaimCore ships no
database image. Dependency and image updates require review and verification.
