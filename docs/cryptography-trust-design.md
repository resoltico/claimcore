# Cryptography and trust-material lifecycle

## Findings and constraints

Witness AES-GCM uses fresh random 96-bit nonces and installation/lineage/epoch/operation/phase AAD;
the independent journal, primary ticket and full audit bind the other scope facts. Recovery exports
use distinct encryption and MAC keys, authenticated metadata, and a `(key_id, nonce)` uniqueness
constraint. Exact recovery and historical verification still require retained old keys; deleting a
key is not a safe substitute for rotation or erasure. Owner signatures and witness tickets are
rechecked under current authority. Keep those mechanisms and their ordering.

Current remote HTTPS and PostgreSQL `VerifyFull` admit certificates without enabling revocation
checks, because both platform handlers default that option off. The owner command can also pass an
un-normalized private primary connection string into owner suboperations that construct connections
directly. In loopback custom-root paths, the root's CA properties and the presented leaf's
server-authentication purpose are not applied consistently. Web's internal IdP backchannel follows
redirects even though its startup check does not.

Witness and suppression private JSON loaders compare member-name sets without counts, accepting
duplicate names with ambiguous last-value interpretation. The Database witness loader duplicates
the Hosting witness-key parser. Key rings can reuse the same material under distinct IDs or accept
obviously zero material; that defeats the meaning of rotation, and a repeated AES key can reuse a
nonce across nominal key IDs. A zero managed-copy commitment key is publicly predictable.

## Chosen design

Use the existing transport admission owner to force online certificate-revocation checking on
non-loopback `VerifyFull` PostgreSQL connections. Normalize primary owner and witness-private
connection strings at the Database entry boundary before an operation can open any connection.
Retain the existing typed refusal and do not print connection content. Configure production Web
OIDC startup, discovery, token and bearer-metadata backchannels and CLI system-trust clients for
revocation checks; do not follow IdP backchannel redirects. The loopback private-root fixture keeps
its explicit no-CRL exception and mandatory hostname check.

Put the closed CA/root and server-authentication facts in one HostSecurity value policy used by Web
and CLI. A root is a current, public-only, self-issued CA with KeyCertSign. A presented leaf must
carry serverAuth EKU, and the custom chain is built for that purpose. Reject malformed, expired,
wrong-purpose or wrong-host certificates. The existing TLS client interface remains typed to
HttpClientHandler; there is no new TLS option or alternate trust mode.

Use one Witness-owned strict key-ring parser for both Hosting and Database, zeroing decoded input
after construction. Bound direct KeyRing construction and refuse all-zero or repeated witness
material across IDs. Apply exact-member and nonzero checks to suppression and copy commitment keys;
recovery artifact rings already reject duplicate JSON members, and now also refuse repeated
encryption/MAC material across IDs. Missing historical material still quarantines reads/audits
rather than relabeling accepted or revoked history.

## Operational limits and dependents

System and database CA issuers must publish reachable revocation data for remote connections.
Revocation checks apply at TLS handshakes; existing connections and cached issuer signing keys or
browser sessions are not an immediate issuer-key revocation channel. After a known compromise,
operators must close admission, stop affected hosts/CLI sessions, revoke actor grants as needed,
replace credentials and issuer keys/roots, then reopen only after exact audit and independent
evidence checks. Historical ciphertext still requires old decryption keys, so compromise cannot
be undone by rotating an ID or deleting a file. This does not promise safety against a compromised
primary, witness and independent evidence custodian acting together.

Update local certificate fixtures to separate CA and server keys, add negative controls for leaf
purpose, root/key-ring shapes, reused material and remote revocation admission, update affected
docs and Unreleased notes, regenerate inventories and contract lock only if the published
diagnostic shape actually changes. No reset, adoption, format conversion or automatic key migration.

## Newly published tooling advisories at verification

The registry now reports two high-severity advisories with no patched versions: `braces`
GHSA-vfj7-8cjw-p6xm, reached through Stylelint, and `http-cache-semantics`
GHSA-ch52-4w7c-c8xp, reached through the license-checking tool. Both paths are development-only;
the production dependency audit is clean. Removing Stylelint would discard CSS lint assurance,
and downgrading either parent does not patch the vulnerable package. Keep the full audit visible,
require a clean production audit, and permit only these two exact development advisory roots until
a short review deadline. Resolve every indirect finding to one of those roots. Unknown advisories,
malformed or unavailable audit results, production exposure, and stale exceptions refuse the gate.
The exception is a time-bounded operational risk, not a claim that the vulnerable code is fixed.
