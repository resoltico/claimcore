# Getting started with ClaimCore

**Build the source, inspect the CLI, and exercise a first fictional case in a disposable, authenticated installation.**

The supported self-contained first run uses isolated primary and witness PostgreSQL clusters, a synthetic HTTPS OIDC issuer, and published Web and CLI binaries. It opens a fictional case, confirms a witnessed acceptance and exact replay, reads its history, and exercises recovery. The fixture removes its labelled containers when it finishes; it is not a persistent personal installation or a production deployment template. You can stop after the build to explore database-free CLI discovery.

> **Synthetic evaluation only.** The local fixture qualifies specific behavior; it does not establish independent key custody, off-host freshness, backup retention, recovery from an operator's data, or an internet-facing service. Read [Security and operations](operations.md) before considering real data. Do not use the fixture's principals, keys, certificates, databases, or credentials for adopted cases.

The executable private-file path is supported on macOS and Linux. Windows supports source builds, tests, and database-free discovery, but case-work private-file admission fails closed there. The automated browser fixture requires a locally running Docker engine and a supported Chromium installation.

## Before you begin

Use one trusted, non-synchronized checkout. Match the .NET SDK in [`global.json`](../global.json), Node.js in [`.node-version`](../.node-version), and npm requirements in [`web/package.json`](../web/package.json). The first run downloads locked dependencies, browser binaries, and pinned container images; it requires network access. Also install a C compiler (`cc`), Docker with Compose, Git, Bash, OpenSSL 3, `curl`, and `jq`. Docker must target the **local** engine, not a remote context or `DOCKER_HOST`.

Do not point this guide at an existing ClaimCore database, volume, private `.local` directory, or adopted recovery artifact. This installation has one fresh checksum-bound primary baseline and a separate witness; unsupported older namespaces are refused, not migrated, adopted, reset, or converted. Preserve older data with its matching software. The [Development guide](development.md) owns the full contributor toolchain and verification gates.

Confirm the active tools before starting:

```sh
dotnet --version
node --version
npm --version
cc --version
openssl version
docker compose version
docker context show
docker info --format '{{.Name}}'
jq --version
```

**Checkpoint:** the versions match the repository manifests, OpenSSL is version 3, and Docker names your intended local engine. Run as your ordinary account, not with `sudo`.

## 1. Build and inspect without case-work credentials

Clone into a new directory, or use an existing trusted checkout:

```sh
git clone https://github.com/resoltico/claimcore.git claimcore-first-run
cd claimcore-first-run
```

Run each block separately and stop on an error. This build uses exact checked dependency graphs and does not create a database:

```sh
dotnet restore ClaimCore.slnx --locked-mode
dotnet tool restore
npm --prefix web ci
npm --prefix web run contract:generate
dotnet build ClaimCore.slnx --configuration Release --no-restore
```

Inspect the CLI's semantic description and current invocation schema without an issuer, service, or database credential:

```sh
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- describe summary
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- schema invocation
```

**Checkpoint:** both discovery commands exit successfully and the schema identifies CLI protocol version 4. This is a complete stopping point for database-free exploration. `call` and `session` are different: they require an authenticated HTTPS service, not a primary PostgreSQL connection string. See the [CLI reference](cli.md) for their exact generated frames and recovery rules.

## 2. Run the isolated first-case qualification

The repository's published acceptance runner provisions the disposable two-cluster/OIDC fixture, builds matching browser assets, publishes CLI, Database, and Web, verifies their publication manifests, and runs the exact authenticated CLI acceptance suite through the HTTPS service. It uses synthetic principals and a fictional claimant only. No AWS account or permanent cloud subscription is needed.

```sh
bash eng/Run-PublishedCliAcceptance.sh
```

Allow the runner to finish; it may download the pinned PostgreSQL and Keycloak images and a Chromium binary on the first run. Do not start another repository Docker-backed test concurrently. The runner does not use your `.env`, a persistent Compose project, or an adopted installation. It creates a unique ignored `artifacts/acceptance-local.*` workspace and exact-labelled disposable containers. If it fails, keep its local evidence for diagnosis and use [Development](development.md) to rerun the exact check after correcting the cause; do not bypass publication checks or lower a test count.

**Checkpoint:** the final line says the published authenticated CLI-v4 `call`/`session` acceptance passed. The suite must report all 16 tests successful, with no skipped or failed tests. Its first-case path submits a fictional `OPEN` command, verifies witnessed `CONFIRMED`/`ACCEPTED` settlement, replays the same identity as `OBSERVED_ACCEPTED`, and reads the case and history. It also checks exact-digest recovery, owner-private export/import, service-principal grants, and live Keycloak client-credentials and interactive PKCE flows. This is stronger than a mock-only protocol check, but it is still synthetic local qualification, not a claim of production readiness.

The printed workspace path contains published binaries and a test-results directory. Stage and publish evidence is under ignored `artifacts/evidence/` paths bound to that run. Preserve it if investigating a failure; do not commit it or treat it as a backup. The runner removes only containers carrying its exact test label, while the OIDC helper removes its own disposable container and temporary secret files. It does **not** delete unrelated containers, volumes, private directories, or user data. If an interrupted run leaves resources behind, inspect their exact labels and ownership before any cleanup.

## What the fixture does not set up

Use [Service operation](service.md) and its [public CA trust procedure](service.md#trust-the-public-local-ca) for the maintained Docker operating model, persistent local evaluation and configured application deployments. The disposable qualification above remains a test run; it is not the service lifecycle. The former single-primary development Compose layout has been removed; existing volumes are preserved and are not automatically adopted or converted.

For an operator-managed installation, start with the ownership and custody boundaries in [Architecture](architecture.md), [Security and operations](operations.md), [Database](database.md), [Web](web.md), and [CLI](cli.md). Provision primary and witness independently, choose and store the installation's immutable canonical IANA business time zone, arrange private key custody and an OIDC issuer, and qualify backup freshness plus a restored primary/witness pair before considering real data. A green local fixture, a matching schema marker, or a successful `verify` alone does not prove that an older backup contains every accepted operation.

## When something fails

Stop at the first failed checkpoint. Inspect diagnostics locally; share only redacted, non-sensitive information through [Support](../SUPPORT.md). Sensitive findings belong under [Security](../SECURITY.md), not a public issue.

| Symptom | What to check next |
|---|---|
| SDK, npm, native build, or asset mismatch | Recheck pinned tools, `cc`, source/lock pairing, and exact build output. Do not update lock files or bypass a publish manifest merely to force this run through. |
| Docker or isolated PostgreSQL startup fails | Confirm the local engine and free resources; inspect the runner's exact-labelled containers and private logs. Do not prune host-wide resources or delete an existing volume. |
| OIDC discovery, TLS, or login fails | Check the fixture's HTTPS issuer, certificate validity, loopback hostname, and current disposable client identity. Do not disable certificate or hostname validation globally. |
| A private path is refused | Check physical absolute paths, ownership, `0600` files, `0700` directories, linked components, and extended ACLs. Do not recursively change home-directory permissions or pass administrator credentials to the CLI. |
| An accepted-result response is lost or uncertain | Preserve the exact operation ID, request bytes, and digest. Observe and reconcile the same identity using the [recovery reference](cli.md#canonical-request-identity-and-recovery); do not manufacture a replacement operation or infer that commit failed. |

The runner's success is a repeatable development check, not a release publication. It does not tag, publish, migrate, or modify adopted data.
