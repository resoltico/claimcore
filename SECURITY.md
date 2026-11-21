# Security policy

## Report privately

Use GitHub **Security → Advisories → Report a vulnerability** for this repository. Supply the affected revision,
impact and a minimal synthetic reproduction. Exclude active credentials, claimant data and unredacted database
extracts even from a private report. Rotate exposed credentials through the operator's process.

If private reporting is unavailable, open a public issue asking for a private contact path. Keep vulnerability
details, affected components and reproductions out of that issue.

## Understand the trust boundary

OIDC identifies human/service principals; current scoped ClaimCore grants authorize work. Browser POSTs require
antiforgery. The browser and actor CLI have no database credentials. Owner administration uses the same
`claimcore-server` executable with a separate database-owner credential; the serving process uses runtime credentials. Current permissions and receipt disclosure are decided within the authoritative view.

The dedicated database owner has no superuser, role creation, replication or row-security bypass
privileges. The PostgreSQL bootstrap superuser is confined to the database container's local socket
in Compose. The IdP, service host, database owner and backup operator remain trusted. Hashes detect inconsistency against
trusted retained evidence; they cannot establish independent freshness or defeat an administrator rewriting all
evidence. Live erasure cannot reach old row versions, WAL, exports, replicas or backups. Even retained references
may identify people. Read [Operations](docs/operating.md) before handling personal data.

Reads covered by the disclosure trail require a committed event before the service returns
content. Installation-wide Auditors can review the trail. Its rows keep identifiers, outcomes
and structured export selections for 24 calendar months, including after whole-case erasure.
The database owner and backup operator remain trusted. See the
[disclosure trail](docs/operating.md#disclosure-trail).

Attachment bytes and private ledger compartments are encrypted in PostgreSQL with an
installation data key. Keep the wrapping master key outside the database and back it up
separately. Case snapshots, search text, notes and parties remain readable in projections.
Live erasure leaves existing backups and WAL intact. See [data-at-rest keys](docs/operating.md#data-at-rest-keys).

[Private-file admission](docs/operating.md#private-configuration) follows secret-volume symlinks and allows group
read. It checks neither ownership nor ACLs and does not freeze concurrent changes. Protect the Web key ring
separately and back it up with the database. Database-backed session sharing is qualified under the documented
clock conditions; arbitrary multi-host deployment is outside that evidence.

ClaimCore supplies separate Compose templates for evaluation and operator-managed hosting. Operators
own identity restrictions, TLS, deployment qualification, monitoring, backups, retention and incident
response. Each register trusts its operators and has no tenancy isolation. Identify the affected
revision in reports. Unmaintained revisions carry no security-fix commitment.
See [production hosting](docs/operating.md#install-from-release-artifacts).

Dependency/image declarations are pinned in source and lockfiles. Image or dependency changes need review and
verification. ClaimCore ships no PostgreSQL image. See the [database declaration](db/postgresql-baseline.json)
and [dependency guidance](docs/contributing.md#dependencies).

See the [glossary](docs/glossary.md) for project terminology.
