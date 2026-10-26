# Security policy

## Report privately

Use GitHub **Security → Advisories → Report a vulnerability** for this repository. Supply the affected revision,
impact and a minimal synthetic reproduction. Exclude active credentials, claimant data and unredacted database
extracts even from a private report. Rotate exposed credentials through the operator's process.

If private reporting is unavailable, open a public issue asking for a private contact path. Keep vulnerability
details, affected components and reproductions out of that issue.

## Understand the trust boundary

OIDC identifies human/service principals; current scoped ClaimCore grants authorize work. Browser POSTs require
antiforgery. The clients have no database credentials, and owner administration is a separate executable with
a separate credential. Current permissions and receipt disclosure are decided within the authoritative view.

The IdP, service host, database owner and backup operator remain trusted. Hashes detect inconsistency against
trusted retained evidence; they cannot establish independent freshness or defeat an administrator rewriting all
evidence. Live erasure cannot reach old row versions, WAL, exports, replicas or backups. Even retained references
may identify people. Read [Operations](docs/operating.md) before handling personal data.

[Private-file admission](docs/operating.md#private-configuration) follows secret-volume symlinks and allows group
read. It checks neither ownership nor ACLs and does not freeze concurrent changes. Protect the Web key ring
separately and back it up with the database. Database-backed session sharing is qualified under the documented
clock conditions; arbitrary multi-host deployment is outside that evidence.

Compose supplies an evaluation environment. Operators own identity restrictions, TLS, deployment, monitoring,
backup/restore, retention and incident response. There is no multi-tenancy or production qualification. Identify
the affected revision in reports; no security-fix commitment is made for unmaintained revisions.

Dependency/image declarations are pinned in source and lockfiles. Image or dependency changes need review and
verification. ClaimCore ships no PostgreSQL image. See the [database declaration](db/postgresql-baseline.json)
and [dependency guidance](docs/contributing.md#dependencies).

See the [glossary](docs/glossary.md) for project terminology.
