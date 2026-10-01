# Security policy

## Reporting a vulnerability

Do not place vulnerabilities, credentials, connection strings, request or recovery files, claimant
information, database extracts, or unredacted diagnostics in a public issue.

Use this repository's GitHub private vulnerability reporting workflow:

1. Open the repository's **Security** tab.
2. Open **Advisories**, then select **Report a vulnerability**.
3. Include the affected revision, likely impact, a minimal synthetic reproduction, and whether any
   credential or real data may have been exposed.

Never attach active credentials, real claimant data, or an unredacted database extract, even to the
private report. Revoke or rotate an exposed credential through its operator-controlled process and
use synthetic replacements in the reproduction.

If GitHub does not offer **Report a vulnerability**, open a public issue containing only a request
for a maintainer to enable a private reporting path. Do not identify the affected component,
vulnerability class, impact, reproduction steps, or sensitive value in that fallback issue.

## Supported security boundary

ClaimCore is a pre-1.0 application for a single trusted local administrative boundary. The repository
does not promise security fixes for historical revisions; reproduce findings against the current
maintained revision.

The Web service validates individual OIDC browser identities and CLI/automation bearer identities.
Browser tickets remain server-side behind secure cookies; ClaimCore actor and resource grants are
default-deny and rechecked under authority locks. Owner administration is a separate credential and
executable boundary. The browser and CLI receive neither database nor witness credentials.

The service is loopback-bound and does not supply multi-tenancy or a qualified nonloopback deployment.
Primary schema owners, witness administrators and host/storage administrators remain trusted; control
of both databases, keys or all retained evidence can defeat the application boundary. Keep runtime
and owner credentials out of browser clients, untrusted agents and generated code. Individual login,
a green test run or a current-pair audit does not certify independent-host recovery or total erasure.

Use only synthetic data unless the deployment has separately defined and tested identity, access,
secret custody, TLS, monitoring, backup and restore, retention and deletion, and incident response.
The complete operating limits are in [Security and operations](docs/operations.md).

The local development and test database uses the official PostgreSQL image pinned by digest in
`db/postgresql-baseline.json`; ClaimCore ships no database image. Dependabot proposes image updates
weekly, and a newer image is adopted only through that reviewed change.
