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

The Web bootstrap credential, cookie, and in-memory session admit a local browser to the running
host. They do not establish individual identity, per-user authorization, multi-tenancy, remote API
security, or a tamper-proof audit. Runtime database credentials can bypass parts of the domain model;
never expose them to browser clients, untrusted agents, or generated code.

Use only synthetic data unless the deployment has separately defined and tested identity, access,
secret custody, TLS, monitoring, backup and restore, retention and deletion, and incident response.
The complete operating limits are in [Security and operations](docs/operations.md).

CI scans the exact PostgreSQL image for fixed high and critical vulnerabilities. A temporary finding
proved unreachable may be entered in `container-vulnerability-exceptions.yaml` with a technical
statement and expiry. Expiry or any unlisted applicable finding fails the gate; remove an exception
as soon as the official image includes the fix.
