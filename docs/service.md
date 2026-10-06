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

Provide a physical absolute private configuration directory outside the build context. Its `web`
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
provider. Their data volumes and identity state survive ordinary stop/replacement. It is deliberately
`SYNTHETIC_ONLY`: use fictional data. The local OIDC development database and same-host topology are
not a production identity-provider or independent-custody qualification.

The local CA publishes an initially empty signed CRL from a separate read-only container;
PostgreSQL clients retain online revocation validation. Local certificates and the CRL expire
after one year. Production deployments use their managed CA and revocation/rotation procedure.

Create configuration once, in an empty owner-private directory. The creator refuses occupied,
linked, broad or other-owner roots. An empty root owned by the requested UID or root
(Docker Desktop’s initial mount view) is assigned to the requested UID during explicit creation. Partial creation remains available for inspection; do not treat
failure as authorization to erase data or recreate identities.

```sh
install -d -m 700 "$PWD/.local/installation"
export CLAIMCORE_CONFIG_DIR="$PWD/.local/installation"
export CLAIMCORE_SERVICE_UID="$(id -u)"
export CLAIMCORE_SERVICE_GID="$(id -g)"
export CLAIMCORE_COMPOSE_PROJECT=claimcore-local

docker compose -f deployment/compose.yaml -f deployment/administration.compose.yaml \
  -f deployment/local.compose.yaml run --rm --no-deps --build configure \
  /configuration "$CLAIMCORE_SERVICE_UID" "$CLAIMCORE_SERVICE_GID" Europe/Riga

docker compose -f deployment/compose.yaml -f deployment/administration.compose.yaml \
  -f deployment/local.compose.yaml up --detach --wait primary witness identity revocation

docker compose -f deployment/compose.yaml -f deployment/administration.compose.yaml \
  -f deployment/local.compose.yaml run --rm --build initialize

docker compose -f deployment/compose.yaml -f deployment/administration.compose.yaml \
  -f deployment/local.compose.yaml up --build --detach --wait web
```

Open `https://app.localhost:5443`. The local public CA is `web/ca.pem`; verify and explicitly trust it
in the browser through the platform's normal trust procedure. Never disable certificate validation.
The generated individual `owner` password is in `administration/owner.password`; do not publish it
or paste it into a diagnostic report. Initial ownership does not invent case-work grants. Use the
authenticated CLI `authority.setGrant` endpoints to grant the owner `CASE_EDITOR`,
`RECOVERY_OPERATOR` and `RECOVERY_EXPORTER` for the intended installation scope. The exact private
issuer/subject binding is in `administration/initial-owner.json`. [CLI](cli.md) owns those frames and
interactive authentication. Do not write grants directly through SQL.

Keep configuration, trust material, database volumes and copies under explicit operator custody.
`docker compose down` retains volumes; ordinary operation never uses `down --volumes`, a prune,
reset, adoption or upgrade conversion. The removed single-primary development Compose file is not
an installation migration; retained old volumes require their matching software and evidence.

## Health and verification

Container health uses `ClaimCore.Web probe live`: it connects to this listener, retains the public
TLS/HTTP identity, and validates certificates. It does not compose a runtime or read database
credentials. `probe ready` independently observes real-data readiness; synthetic and quarantined
installations stay unready even while Web is live. Neither probe repairs state or changes grants.

[Development](development.md) owns complete verification. Container qualification exercises the
real HTTPS graph, non-root operation, separate mounts and database roles, TLS-name and foreign-Host
refusal, synthetic readiness refusal, unsafe-input refusal, startup stop and persisted installation
identity across container replacement. Build-context canaries prove private-input exclusions. Native tests cover startup cancellation and lease-preserving shutdown.
Report tested platforms and actual failures separately from design/source review. Installation
qualification and owner merge/release approval remain separate requirements.
