# Operating the HTTPS service

Docker runs the Linux service process. Linux servers and Docker Desktop clients use the same
published Web application and Compose model. [Web](web.md) owns HTTPS/admission and configuration;
[Operations](operations.md) owns real-data qualification; [Database](database.md) owns owner-only
initialization, recovery and data administration.

## Application deployment

Use [`deployment/compose.yaml`](../deployment/compose.yaml) for the Web process and
[`deployment/administration.compose.yaml`](../deployment/administration.compose.yaml) for explicitly
invoked owner tools. The Web image contains neither the Database entry point nor schema-owner
inputs. Do not mount an owner directory or the Docker socket into Web.

Provide a physical absolute private configuration directory on a Linux backing filesystem,
outside the build context. Docker Desktop host sharing remaps UID ownership and must not carry
private runtime inputs; use the local overlay’s Linux volume model there. Its `web`
subdirectory contains private runtime files and `web.env`; `web-state` is persistent, writable
service state. Select the UID/GID that owns those inputs. Files must meet native private-file
admission; ordinary startup refuses wrong ownership, broad permissions or links rather than fixing
them. Use [the environment example](../deployment/web.env.example) as a list of settings, not as a
credential bundle. Connection strings, keys and client secrets are file contents.

The public HTTPS origin is independent of the socket address/port. Native defaults remain
loopback. The Docker configuration explicitly binds a container interface; Compose publishes on
host loopback by default. A server operator can explicitly publish a remote interface and configure
its public DNS origin and matching certificate. No forwarded header establishes transport or actor
identity. Remote identity providers use validated HTTPS and system trust.

The local `initialize` command is one-time: an existing primary is refused. If a stage is
interrupted or uncertain, preserve configuration and volumes and follow owner reconciliation;
do not rerun initialization as a restart or repair command. Primary and witness creation are
separate owner operations, not one cross-database transaction.

Normal `up`, `start` and `restart` never initialize schemas, create actors, regenerate keys or repair
uncertain work. Provision independently managed dependencies and their credentials first. A source
preview or a same-host Compose deployment cannot establish real-data readiness.

```sh
export CLAIMCORE_CONFIG_DIR=/absolute/private/claimcore
export CLAIMCORE_SERVICE_UID=1654
export CLAIMCORE_SERVICE_GID=1654

docker compose -f deployment/compose.yaml up --build --detach --wait
docker compose -f deployment/compose.yaml ps
docker compose -f deployment/compose.yaml logs web
docker compose -f deployment/compose.yaml stop web
docker compose -f deployment/compose.yaml start web
```

`unless-stopped` supplies daemon restart behavior. A refused configuration remains refused; inspect
bounded diagnostics and correct the cause. A process restart is not a recovery decision. Shutdown
closes HTTP admission and awaits runtime cleanup; a forced kill or lost response still requires
exact-identity recovery rather than assuming rollback. The configured 90-second manager stop budget
is a hard process boundary, not a promise of a definite result for every request.

## Persistent local evaluation

The local overlay supplies separate TLS PostgreSQL primary/witness servers and a native HTTPS OIDC
provider. Their data volumes, private input volume and identity state survive ordinary stop/replacement.
Role-specific volume subpaths isolate Web, owner tools, PostgreSQL, the issuer and public CRL publication. It is deliberately
`SYNTHETIC_ONLY`: use fictional data. The local OIDC development database and same-host topology are
not a production identity-provider or independent-custody qualification.

The local CA publishes an initially empty signed CRL from a separate read-only container;
PostgreSQL leaves advertise its Docker-reachable endpoint and clients retain online revocation validation.
Browser app/IdP leaves omit that Docker-only distribution point. Local certificates and the CRL expire
after one year. Production deployments use their managed CA and revocation/rotation procedure.

Create configuration once, in an empty Linux volume and an empty physical operator directory.
The creator refuses occupied volumes or operator roots. Private role files use real Linux UID/mode
semantics; the host receives only public settings/certificates and the separate human login credential. Partial creation remains available for inspection; do not treat
failure as authorization to erase data or recreate identities.

```sh
install -d -m 700 "$PWD/.local/installation"
export CLAIMCORE_CONFIG_DIR="$PWD/.local/installation"
export CLAIMCORE_SERVICE_UID="$(id -u)"
export CLAIMCORE_SERVICE_GID="$(id -g)"
export CLAIMCORE_COMPOSE_PROJECT=claimcore-local

docker compose -f deployment/compose.yaml -f deployment/administration.compose.yaml \
  -f deployment/local.compose.yaml run --rm --no-deps --build configure \
  /configuration /metadata "$CLAIMCORE_SERVICE_UID" "$CLAIMCORE_SERVICE_GID" Europe/Riga

docker compose -f deployment/compose.yaml -f deployment/administration.compose.yaml \
  -f deployment/local.compose.yaml up --detach --wait primary witness identity revocation

docker compose -f deployment/compose.yaml -f deployment/administration.compose.yaml \
  -f deployment/local.compose.yaml run --rm --build initialize

docker compose -f deployment/compose.yaml -f deployment/administration.compose.yaml \
  -f deployment/local.compose.yaml up --build --detach --wait web
```

Open `https://app.localhost:5443`. The local public CA is `web/ca.pem`; verify and explicitly trust it
through [the explicit local trust procedure](#trust-the-public-local-ca). Never disable certificate validation.
The generated individual `owner` password is in the operator directory’s `owner.password`; do not publish it
or paste it into a diagnostic report. Initial ownership does not invent case-work grants. Use the
authenticated CLI `authority.setGrant` endpoints to grant the owner `CASE_EDITOR`,
`RECOVERY_OPERATOR` and `RECOVERY_EXPORTER` for the intended installation scope. The exact private
issuer/subject binding is in `administration/initial-owner.json`. [CLI](cli.md) owns those frames and
interactive authentication. Do not write grants directly through SQL.

Keep the private input volume, operator files, trust material, database volumes and copies under explicit operator custody.
`docker compose down` retains volumes; ordinary operation never uses `down --volumes`, a prune,
reset, adoption or upgrade conversion. The removed single-primary development Compose file is not
an installation migration; retained old volumes require their matching software and evidence.

## Trust the public local CA

The creator exports **only the public CA certificate** to
`$CLAIMCORE_CONFIG_DIR/web/ca.pem`. A valid signature does not create browser trust.
Both `app.localhost:5443` and `identity.localhost:5444` need that issuer trusted.
Public issuers do not issue ordinary certificates for reserved localhost identities
([Let's Encrypt](https://letsencrypt.org/docs/certificates-for-localhost/)).

Before importing anything, compare the exported certificate's SHA-256 fingerprint
with the installation authority's public certificate, read directly from its Linux
volume through the configured Compose project:

```sh
openssl x509 -in "$CLAIMCORE_CONFIG_DIR/web/ca.pem" -noout -sha256 -fingerprint -dates

docker compose -f deployment/compose.yaml -f deployment/administration.compose.yaml \
  -f deployment/local.compose.yaml run --rm --no-deps --entrypoint openssl configure \
  x509 -in /configuration/installation/authority/ca.pem -noout -sha256 -fingerprint -dates
```

Keep the verified fingerprint with the installation's public metadata. Refuse a
mismatch or expired issuer. Inspect the served chain, SAN, server-authentication
purpose and validity for each origin; the verification result must succeed:

```sh
openssl s_client -connect app.localhost:5443 -servername app.localhost \
  -CAfile "$CLAIMCORE_CONFIG_DIR/web/ca.pem" -verify_hostname app.localhost \
  -verify_return_error </dev/null
openssl s_client -connect identity.localhost:5444 -servername identity.localhost \
  -CAfile "$CLAIMCORE_CONFIG_DIR/web/ca.pem" -verify_hostname identity.localhost \
  -verify_return_error </dev/null
```

These checks use explicit CA trust and do not prove the browser trusts it. Browser
acceptance must use normal validation with no warning override. Reissuing a leaf
cannot fix missing root trust. Importing this root permits it to issue TLS
certificates beyond these two sites: protect its private signing key in owner
custody. Never import a PFX, `ca.key`, or a runtime private key as browser trust.

### Chrome on macOS

After the operator explicitly approves root trust, import the verified `ca.pem`
into **Keychain Access → login** for the current user. Select that exact
certificate, confirm its SHA-256 fingerprint, open **Trust**, and set **Secure
Sockets Layer (SSL)** to **Always Trust**, leaving other purposes unchanged.
Close the dialog and authenticate the requested change. System-keychain trust is
machine-wide and requires a separate administrator decision. See
[Apple's trust procedure](https://support.apple.com/guide/keychain-access/change-the-trust-settings-of-a-certificate-kyca11871/mac)
and [Chromium's local-trust policy](https://chromium.googlesource.com/chromium/src/+/main/net/data/ssl/chrome_root_store/faq.md).

Quit Chrome completely, reopen it, and navigate to both exact origins, then sign
in. Inspect Chrome's connection/certificate details. A previous warning override
must be cleared through Chrome's site controls before relying on this check;
changing a personal profile's trust or overrides requires explicit operator
approval. Managed policy can override local trust: ask the browser administrator
rather than disabling certificate or revocation checks.

To remove trust, select the certificate in the same keychain, verify its saved
SHA-256 fingerprint, and delete only that certificate. Do not select by common
name alone. Restart Chrome and verify a fresh connection is refused if no other
approved trust source remains.

### Chromium on Linux

Use **Settings → Privacy and security → Security → Manage certificates** to
import the verified public CA into the current user's trusted authorities.
For the supported NSS command-line alternative, install `libnss3-tools` on
Debian/Ubuntu or `nss-tools` on Fedora. [Chromium documents the NSS store and
trust flags](https://chromium.googlesource.com/chromium/src/+/main/docs/linux/cert_management.md).
Since M146 the default store is `$HOME/.local/share/pki/nssdb`; an existing
`$HOME/.pki/nssdb` takes precedence. Verify which store your browser uses. Do not
replace or recreate an existing database.

Set `claimcore_nss` to that existing store, `claimcore_ca` to the verified public
PEM, and `claimcore_ca_id` to `ClaimCore-` followed by its SHA-256 fingerprint
without colons. After explicit operator approval:

```sh
certutil -d "sql:$claimcore_nss" -A -t 'C,,' -n "$claimcore_ca_id" -i "$claimcore_ca"
certutil -d "sql:$claimcore_nss" -L -n "$claimcore_ca_id" -a \
  | openssl x509 -noout -sha256 -fingerprint
```

Verify the imported fingerprint, restart Chromium, and check both origins and
login with certificate validation enabled. For removal, first repeat the listing
and compare its fingerprint with the saved installation fingerprint, then run
`certutil -d "sql:$claimcore_nss" -D -n "$claimcore_ca_id"`. Restart the browser and
check rejection on a fresh connection. Do not delete a store or a certificate
selected only by its display name. A system-wide or enterprise-managed trust
store requires its administrator's procedure, not this current-user import.

### Expiry and revocation

Browser app/IdP leaves omit the Docker-only CRL URL. This synthetic trust path has
no managed browser online-revocation service. If policy requires one, use approved
managed PKI. PostgreSQL leaves retain `http://revocation:8000/ca.crl`; their clients
still require valid online revocation evidence.

Expiry or changed issuer trust requires reviewed certificate provisioning and
fingerprint verification under owner custody. The fresh creator refuses occupied
installations; do not rerun it over retained state. Preserve cases, histories,
recovery identities, configuration and database volumes while arranging renewed
credentials. Never repair trust by resetting an installation or bypassing errors.

## Health and verification

Container health uses `ClaimCore.Web probe live`: it connects to this listener, retains the public
TLS/HTTP identity, and validates certificates. It does not compose a runtime or read database
credentials. `probe ready` independently observes real-data readiness; synthetic and quarantined
installations stay unready even while Web is live. Neither probe repairs state or changes grants.

[Development](development.md) owns complete verification. Container qualification exercises the
real HTTPS graph, non-root operation, separate mounts and database roles, TLS-name and foreign-Host
refusal, synthetic readiness refusal, unsafe-input refusal, startup stop and persisted installation
identity across container replacement. Build-context canaries prove private-input exclusions. Native tests cover startup cancellation and lease-preserving shutdown. The database revocation experiment starts with healthy initialized primary/witness reads and populated synthetic case/recovery evidence. It varies valid, unavailable, malformed, wrongly signed, expired and separately revoked primary/witness CRLs, recreating the publisher and using fresh client container filesystems for every read. It compares the relevant TLS/cryptography runtime files with the service runtime, checks unchanged read snapshots, and requires an isolated source build with revocation disabled to reach the unhealthy reads and fail the same boundary qualification. This fixture does not provide a production disable option or alter personal certificate caches.
Report tested platforms and actual failures separately from design/source review. Installation
qualification and owner merge/release approval remain separate requirements.
