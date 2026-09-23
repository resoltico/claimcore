# Getting started with ClaimCore

**Build the source, open the local browser interface, and record your first fictional case.**

This walkthrough creates a separate local PostgreSQL installation, private credentials and a
short-lived HTTPS certificate. It ends with an accepted case you can find again after restarting.
You can stop after the build to explore the CLI without a database.

> **For evaluation with synthetic data only.** Use macOS or Linux and one trusted local account.
> ClaimCore is a source preview, not a production deployment template. Its browser login admits a
> trusted local operator; it does not provide individual user identities, per-user authorization,
> remote access or multi-tenancy. Read [Security and operations](operations.md) before real data.

Windows supports source builds, tests and database-free discovery, but private-file runtime work
fails closed there. The runnable setup below is for macOS and Linux, not Windows or WSL qualification.

## Before you begin

Use the instructions and tools belonging to the **same source checkout**. Do not switch branches,
update packages or reuse an older installation partway through the walkthrough. Old ClaimCore
schemas and recovery artifacts are refused, not upgraded or converted. Preserve existing databases,
volumes and their matching software; this guide does not reset them.

| Prerequisite | What to check |
|---|---|
| .NET SDK | Install the exact SDK in [`global.json`](../global.json), for your host architecture. An installed runtime alone is not enough. |
| Node.js and npm | Match [`.node-version`](../.node-version) and `packageManager` / `engines` in [`web/package.json`](../web/package.json). Do not substitute whichever version is globally installed. |
| Docker with Compose | The engine must be running **locally** and support `docker compose up --wait`. The repository supplies the pinned PostgreSQL image. |
| C compiler | `cc` must be available: Apple Command Line Tools on macOS, or your distribution's compiler on Linux. The build compiles the private-file native shim. |
| OpenSSL 3.x | The certificate recipe uses `-noenc` and `-addext`. On macOS, check that `openssl` resolves to OpenSSL 3, not the bundled LibreSSL executable. |
| Git, Bash and a browser | Use a trusted, non-synchronized checkout and local private storage. Initial dependency and container-image downloads need network access. |

The [Development guide](development.md) owns contributor tooling and full verification. You do not
need to set up its entire browser-test or CI toolchain merely to follow this walkthrough.

## 1. Choose the checkout and your local settings

Clone into a new directory, or start in an existing trusted checkout with no first-run `.env`:

```sh
git clone https://github.com/resoltico/claimcore.git claimcore-first-run
cd claimcore-first-run
```

Use **Bash** for the following examples, including on macOS where your usual shell may be zsh:

```sh
bash
```

Keep this terminal open. Run each block separately and stop at any error; do not paste the entire
guide as one script. Parenthesized blocks stop on failure without changing your interactive shell's
error settings.

### Settings for this session

Run this block from the repository root. The values are not secrets. The defaults below identify a
new evaluation setup; choose a different lowercase, hyphenated project name and unused ports if
these are already in use. Retain your choices for [returning later](#returning-later).

```sh
set +x
claimcore_root="$(pwd -P)"
claimcore_project=claimcore-first-run
claimcore_db_port=54329
claimcore_web_port=5443
claimcore_zone=Etc/UTC
claimcore_private="$(cd -- "$HOME" && pwd -P)/.$claimcore_project"
claimcore_tls="$claimcore_private/tls"
claimcore_publish="$claimcore_root/artifacts/$claimcore_project"

claimcore_compose() (
  unset POSTGRES_PASSWORD CLAIMCORE_APP_PASSWORD
  unset CLAIMCORE_COMPOSE_PROJECT CLAIMCORE_POSTGRES_PORT
  unset COMPOSE_REMOVE_ORPHANS
  docker compose --project-name "$claimcore_project" \
    --env-file "$claimcore_root/.env" \
    --file "$claimcore_root/compose.yaml" "$@"
)
```

`Etc/UTC` is an explicit **test installation** choice, not an inferred local calendar. The business
zone becomes immutable when initialized. Choose a different supported canonical IANA ID now if
needed; browser language, display formats and recorded currency do not change it.

`claimcore_compose` is only a helper in this Bash session, not an installed ClaimCore command. It
uses one explicit Compose file and environment file, and prevents inherited password/port variables
from overriding that file. It also disables inherited automatic orphan removal. It does not source
`.env` as shell code or change your Docker context.

Check your active tools and Docker target:

```sh
dotnet --version
node --version
npm --version
cc --version
openssl version
docker compose version
docker context show
docker info --format '{{.Name}}'
```

**Checkpoint:** the versions match the manifests, the compiler and OpenSSL are available, and you
have confirmed that Docker connects to your local engine. A remote Docker context or `DOCKER_HOST`
is not a supported target for this walkthrough. Run the build and host as your ordinary account,
not with `sudo`.

## 2. Build, inspect and publish before creating a database

Restore the checked-in dependency graphs and build the solution:

```sh
(
  set -eu
  dotnet tool restore
  npm --prefix web ci
  dotnet restore ClaimCore.slnx --locked-mode
  dotnet build ClaimCore.slnx --configuration Release --no-restore
)
```

Inspect the CLI without PostgreSQL, credentials or a Web host:

```sh
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- describe summary
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- schema invocation
```

**Checkpoint:** the Release build succeeds, and discovery prints the semantic summary and a JSON
invocation schema. This is a complete stopping point for database-free exploration. The
[CLI reference](cli.md) explains structured automation; the [synthetic walkthrough](../examples/README.md)
adds a full lifecycle after database setup.

For the browser, produce its assets and publish the host into a **new output directory**:

```sh
(
  set -eu
  npm --prefix web run build
  mkdir -p "$claimcore_root/artifacts"
  mkdir "$claimcore_publish"
  dotnet publish src/ClaimCore.Web/ClaimCore.Web.fsproj \
    --configuration Release --no-restore \
    --output "$claimcore_publish/web" -p:UseAppHost=false
  test -f "$claimcore_publish/web/ClaimCore.Web.dll"
)
```

**Checkpoint:** publication succeeds and produces `web/ClaimCore.Web.dll` beneath your chosen output
path. An ordinary .NET build does not produce browser assets. Publication checks that those assets
match the source, locked dependencies, contracts and Node/npm toolchain; do not bypass a mismatch.
This local publish is not a claim that the complete CI qualification or release-packaging process ran.

## 3. Create private credentials for a fresh setup

The following block creates two different random passwords and writes matching configuration in
one operation sequence. You do not need to view, retype or paste either password.

It refuses an existing `.env`, private directory, labelled project container or expected database
volume. **Do not remove those guards to reuse an unknown setup.** For a known existing setup, use
[Returning later](#returning-later). For another installation, use a separate checkout and identity.

```sh
(
  set -euC
  set +x
  umask 077
  test ! -e "$claimcore_root/.env"
  test ! -L "$claimcore_root/.env"

  containers=$(docker ps --all --quiet \
    --filter "label=com.docker.compose.project=$claimcore_project")
  volumes=$(docker volume ls --quiet)
  if [ -n "$containers" ] || \
    printf '%s\n' "$volumes" | grep -Fxq "${claimcore_project}_postgres-data"; then
    printf '%s\n' 'Existing Docker resources: stop and choose a separate setup.' >&2
    exit 1
  fi

  mkdir -m 700 "$claimcore_private"
  admin_password=$(openssl rand -hex 32)
  app_password=$(openssl rand -hex 32)
  test "$admin_password" != "$app_password"

  printf 'POSTGRES_PASSWORD=%s\nCLAIMCORE_APP_PASSWORD=%s\nCLAIMCORE_COMPOSE_PROJECT=%s\nCLAIMCORE_POSTGRES_PORT=%s\n' \
    "$admin_password" "$app_password" "$claimcore_project" "$claimcore_db_port" \
    > "$claimcore_root/.env"
  printf 'Host=127.0.0.1;Port=%s;Database=claimcore;Username=postgres;Password=%s;SSL Mode=Disable\n' \
    "$claimcore_db_port" "$admin_password" > "$claimcore_private/admin.connection"
  printf 'Host=127.0.0.1;Port=%s;Database=claimcore;Username=claimcore_app;Password=%s;SSL Mode=Disable\n' \
    "$claimcore_db_port" "$app_password" > "$claimcore_private/app.connection"
)
```

This implements the four settings in [`.env.example`](../.env.example), with generated hexadecimal
passwords that need no connection-string or Compose escaping. Bash's builtin `printf` writes them
directly to files. Password variables disappear when the subshell exits. Keep tracing off; do not
print `.env`, connection strings, expanded Compose configuration or container environments.

| Location | What belongs there |
|---|---|
| Checkout `.env` | Ignored, owner-private database startup settings. Keep it for this volume; it is not a password-reset mechanism. |
| `$claimcore_private/admin.connection` | Schema-owner credential, used only by `ClaimCore.Database`. |
| `$claimcore_private/app.connection` | Restricted runtime credential, used by Web and CLI. |
| `$claimcore_private/tls/` | Local certificate and private-key container, created next. |
| `$claimcore_private/web-state/` | Host-owned bootstrap/session-admission state, created at startup. Not case storage. |
| Docker volume `${claimcore_project}_postgres-data` | Persistent PostgreSQL data, created at database startup. |

The private directory is outside the checkout. New private directories must be mode `0700`; private
files must be mode `0600`, owned by your account, with no extended ACLs or linked path components.
The setup uses physical home/checkout paths, but ClaimCore still checks every private path itself.
Do not recursively change home-directory permissions to force admission. On macOS, `/var` and `/tmp`
are linked aliases; their physical paths are `/private/var` and `/private/tmp`.

**Checkpoint:** the block succeeds without printing passwords. If it stops partway through, preserve
what it created and inspect the failure locally before proceeding. The block is not a transactional
installer and deliberately does not clean up, overwrite files or regenerate an existing setup.

## 4. Start PostgreSQL and initialize ClaimCore

Start the pinned service from [the repository's Compose file](../compose.yaml):

```sh
claimcore_compose up --detach --wait
claimcore_compose ps
```

**Checkpoint:** `postgres` is healthy, with its host port bound to `127.0.0.1`. The health check tests
the runtime role's database connection. It does **not** mean the ClaimCore schema has been initialized.
`SSL Mode=Disable` in this example is limited to that local development connection, not a remote
PostgreSQL configuration.

Create the absent ClaimCore namespace, then verify it read-only:

```sh
(
  set -eu
  CLAIMCORE_ADMIN_CONNECTION_FILE="$claimcore_private/admin.connection" \
    dotnet run --project src/ClaimCore.Database --configuration Release --no-build -- \
    initialize "$claimcore_zone"
  CLAIMCORE_ADMIN_CONNECTION_FILE="$claimcore_private/admin.connection" \
    dotnet run --project src/ClaimCore.Database --configuration Release --no-build -- verify
)
```

**Checkpoint:** both commands exit successfully and return an `administrationResult` with
`operationOutcome: "COMPLETED"`. Schema, baseline identity, installation lineage and the business
zone commit together. Repeating initialization against an exact current installation with the same
zone only verifies it; it does not upgrade an old database.

An unsupported, partial or differently configured installation is a stop condition. Do not drop its
schema, delete its volume or edit its marker to make setup pass. If completion is unknown, use
read-only verification and [reconciliation guidance](database.md), not a blind repeat. Web and CLI
must never use `admin.connection`.

## 5. Create and check a localhost certificate

The current host requires an owner-private PKCS#12/PFX containing a private key with a **blank PFX
password**. Its protection therefore depends on filesystem permissions and custody. The following
OpenSSL 3 recipe creates a seven-day self-signed server certificate, not a certificate authority.
It creates a new directory rather than replacing a working certificate:

```sh
(
  set -eu
  umask 077
  mkdir -m 700 "$claimcore_tls"
  openssl req -x509 -newkey rsa:2048 -sha256 -noenc -days 7 \
    -subj /CN=localhost \
    -addext 'subjectAltName=DNS:localhost' \
    -addext 'basicConstraints=critical,CA:FALSE' \
    -addext 'keyUsage=critical,digitalSignature,keyEncipherment' \
    -addext 'extendedKeyUsage=serverAuth' \
    -keyout "$claimcore_tls/localhost.key" \
    -out "$claimcore_tls/localhost.pem"
  openssl pkcs12 -export -passout pass: \
    -inkey "$claimcore_tls/localhost.key" \
    -in "$claimcore_tls/localhost.pem" \
    -out "$claimcore_tls/localhost.pfx"
  openssl pkcs12 -in "$claimcore_tls/localhost.pfx" -passin pass: -info -noout
  openssl x509 -in "$claimcore_tls/localhost.pem" -noout \
    -subject -ext subjectAltName -dates -fingerprint -sha256
  rm "$claimcore_tls/localhost.key"
)
```

**Checkpoint:** PFX inspection succeeds; the public certificate shows `DNS:localhost`, its validity
period and SHA-256 fingerprint. Keep that fingerprint available for the next step. Only the extra
raw key created by this successful block is removed; the PFX still contains the private key.
Keep both the PFX and the public PEM certificate. Do not upload the PFX or import it merely to trust
the website.

## 6. Launch, verify the served certificate, then sign in

In your original terminal, start the published host:

```sh
CLAIMCORE_CONNECTION_FILE="$claimcore_private/app.connection" \
CLAIMCORE_WEB_CERTIFICATE_PATH="$claimcore_tls/localhost.pfx" \
CLAIMCORE_WEB_STATE_DIR="$claimcore_private/web-state" \
CLAIMCORE_WEB_ORIGIN="https://localhost:$claimcore_web_port" \
  dotnet "$claimcore_publish/web/ClaimCore.Web.dll"
```

**Checkpoint:** the process stays running and prints the login URL and the path of a private
bootstrap-credential file. Those locations are not the credential itself. Do not start a second
host against the same state directory.

Open a **second terminal** while the host runs. The command below uses the default Web port; replace
`5443` if you chose a different one. It needs none of the original terminal's shell variables:

```sh
openssl s_client -connect localhost:5443 -servername localhost </dev/null 2>/dev/null \
  | openssl x509 -noout -subject -ext subjectAltName -dates -fingerprint -sha256
```

Compare the served certificate's SHA-256 fingerprint with the one from step 5. Proceed only when
they match, the identity is `localhost`, and the certificate is within its validity period. This
comparison identifies your test server; it does not install browser trust.

Open the **exact HTTPS URL printed by the host**, not an HTTP URL, LAN address or substituted IP.
For this verified local test certificate, use the browser's certificate-specific exception or the
operating system's trust controls for the **public PEM certificate only**. Browser behavior differs;
if an exception is unavailable, arrange appropriate local trust rather than disabling TLS validation
globally. A browser trust decision must not require disclosing the PFX or its private key.

After establishing that trust, open the printed bootstrap file in a local, non-synchronizing editor,
read its value and enter it in the login form. Do not print it to the terminal, put it in shell
history, or attach it to a support report. Treat any clipboard copy as a secret and clear it after
use. This is shared local admission, not your personal user account.

**Checkpoint:** signing in opens the case interface without a session, contract or storage error.

## 7. Record one fictional case

Use English for the following button names. Choose **Open new case** and enter:

| Field | Synthetic value |
|---|---|
| Handler's case reference | `DEMO-GETTING-STARTED-001` |
| Incident date | `2025-01-02` |
| Incident notification date (FNOL) | `2025-01-03` |
| Country of incident | `Latvia` |
| Claimant name | `Example Claimant` |
| Allegedly responsible insurer | `Example Insurer` |
| Amount claimed | `1000.00` |
| Currency of claimed amount | `EUR` |

Select **Prepare exact request**. Review the target, expected revision, fields and operation
identity. Preparation has **not** yet changed the accepted case. Tick **I will submit this exact
prepared request**, then select **Submit exact request**. Continue only after the accepted outcome;
select **Return to case** to inspect it.

**Checkpoint:** the case is `OPENED` at revision `1`, with one accepted history entry. Decision and
payment facts are unrecorded. Accepted amount display can omit insignificant trailing zeroes;
that does not mean the original authored request was rewritten. Recording a later payment would
record an assertion, not transfer money or automatically close the case.

The browser offers English, Latvian and Arabic, independent display formats, and expanded-English
layout testing. Switching language preserves drafts, preparation consent and recovery state; it
neither submits an operation nor changes the stored business calendar or currency. Input dates and
amounts still use their documented canonical syntax. See [Browser presentation](web.md#browser-presentation).

If a submit result is lost or uncertain, preserve the displayed operation identity and follow
[Recovery](cli.md#canonical-request-identity-and-recovery). Do not create a new operation or re-enter
the case to guess whether it succeeded. To explore recovery without simulating a failure, choose
**Keep for Recovery** on a separate, deliberately unsubmitted preparation and inspect it there.

## Stop without destroying data

After resolving or deliberately retaining any pending work, stop the host with **Ctrl+C** in its
terminal. Then, in the same Bash session:

```sh
claimcore_compose down
```

This removes the project's containers and network but retains its **named PostgreSQL volume**.
It is not a backup. Do not add `--volumes`, delete the private directory, or run host-wide pruning as
routine shutdown. Keep `.env`, credentials, certificates, Web state and recovery evidence under your
retention policy. Unsaved browser drafts are not persisted by shutting down or reloading the host.

### Returning later

Open Bash in the **same checkout** and rerun only the
[session-settings block](#settings-for-this-session), with your original project name, ports and
paths. Do not rerun cloning, password creation, initialization or certificate generation for an
ordinary restart. Do not regenerate `.env`: editing it does not rotate an existing volume's database
passwords, and changing the project name selects a different installation.

```sh
claimcore_compose up --detach --wait
```

After the service becomes healthy, run the read-only `verify` command from step 4, then the host
launch command from step 6. Each host restart invalidates old sessions and issues a **new bootstrap
credential**. Repeat certificate verification and sign in with the current credential. Find
`DEMO-GETTING-STARTED-001`; the accepted case should still be present.

The test certificate expires after seven days. To renew it, stop the host, set `claimcore_tls` to a
new unused directory such as `"$claimcore_private/tls-2"`, and repeat **only** the certificate and
launch/trust steps. Retain that new path for subsequent sessions. Do not regenerate database
credentials or delete data to fix certificate expiry.

For another source revision, use matching rebuilt assets and a fresh publish output, then review
its installation/format compatibility before opening existing data. This guide is not an upgrade
procedure. Restoring an old database can lose later acceptance evidence; read
[Data and recovery](operations.md#data-and-recovery) before resuming work after a restore.

## When something fails

Stop at the first failed checkpoint. Inspect diagnostics locally; share only redacted, non-sensitive
information through [Support](../SUPPORT.md). Sensitive findings belong under
[Security](../SECURITY.md), not a public issue.

| Symptom | What to check next |
|---|---|
| SDK, npm, native build or asset mismatch | Recheck the pinned tools, `cc`, and the source/lock pairing. Build browser assets before publishing. Do not update lock files, disable a check, or serve a development server to force setup through. |
| Existing setup guard or occupied port | Preserve the existing resources. For the same known setup use the restart path; for a new one use a separate checkout/project and unused ports. Do not delete another project's containers or volumes. |
| PostgreSQL is unhealthy | Confirm the Docker engine is local/running and the port is free. Inspect logs privately. An old volume still uses its original database passwords; replacing `.env` does not reset them. |
| `DB_INSTALLATION_UNSUPPORTED`, a baseline mismatch or a different installed zone | The target is not this fresh supported installation. Preserve it and follow the [database boundary](database.md#fresh-installation-boundary); there is no automatic repair or migration. |
| A private file or state directory is refused | Check physical absolute paths, ownership, `0600` files, `0700` private directories and extended ACLs. Do not use symlink aliases, broad chmod changes or administrator credentials for Web/CLI. |
| Certificate warning, expiry, refused origin or failed login | Check the served fingerprint, validity, exact `https://localhost` origin and port, then the current bootstrap file. Trust only your verified certificate; never disable TLS checks globally. |
| Missing output or an uncertain command result | Preserve exact identity and evidence. Use read-only observation/verification and the [recovery reference](cli.md#canonical-request-identity-and-recovery), not a new ID, blind retry or data reset. |

For the full configuration and ownership rules, continue with [Web](web.md), [CLI](cli.md),
[Database](database.md), or the [documentation map](README.md).
