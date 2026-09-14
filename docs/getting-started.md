# Getting started from source

ClaimCore 0.2.0 is a source-preview release with no downloadable application binaries. This guide
builds from a source checkout, creates a local PostgreSQL installation for synthetic data, applies
its schema, and opens the published Web application.

The runtime steps in this guide are supported on macOS and Linux, whose private files can be
verified through the shared handle-first service. Windows remains a source build/test host, but
private-file runtime operations fail closed there; database-free CLI, Database, and Web discovery
remain available. Keep every credential and certificate path private and absolute. Do not use real
claimant or operational data for this walkthrough.

## Prerequisites

Install:

- the exact .NET SDK selected by [`global.json`](../global.json);
- the exact Node.js release selected by [`.node-version`](../.node-version), with the npm release
  declared by [`web/package.json`](../web/package.json);
- Docker with Compose;
- a C compiler available as `cc` (Apple Command Line Tools on macOS or a distribution compiler on
  Linux) for the private-file native shim compiled into the application publish tree;
- OpenSSL for the local certificate example;
- Git and a POSIX-compatible shell.

[Development](development.md) lists the additional tools required for complete contributor
verification.

## Build the checkout

From the repository root, restore only locked dependency graphs and build the .NET solution:

```text
dotnet tool restore
npm --prefix web ci
dotnet restore ClaimCore.slnx --locked-mode
dotnet build ClaimCore.slnx --configuration Release --no-restore
```

## Start the synthetic database

Copy [`.env.example`](../.env.example) to `.env`. Fill `POSTGRES_PASSWORD` and
`CLAIMCORE_APP_PASSWORD` with different strong local values. The optional Compose project name and
host port allow independent checkouts; if the port changes, use the same value in both connection
files below. If another ClaimCore checkout or installation uses the defaults, select a unique
`CLAIMCORE_COMPOSE_PROJECT` and an unused `CLAIMCORE_POSTGRES_PORT` before the first start. On POSIX,
make `.env` owner-only with mode `0600` before starting Compose.

```text
docker compose up --detach --wait
```

Choose an owner-private physical directory, such as the ignored `.local/credentials` under a checkout
whose ancestors are not links, or another canonical absolute location. On macOS, `/var` and `/tmp`
are linked aliases; use their physical `/private/var` or `/private/tmp` paths if needed. The private
file service rejects any linked path component. Create two UTF-8 files there with owner-only
permissions. `admin.connection` contains the schema-owner connection:

```text
Host=127.0.0.1;Port=54329;Database=claimcore;Username=postgres;Password=<owner-password>;SSL Mode=Disable
```

`app.connection` contains the distinct runtime connection:

```text
Host=127.0.0.1;Port=54329;Database=claimcore;Username=claimcore_app;Password=<runtime-password>;SSL Mode=Disable
```

Both files must be mode `0600` and their directory mode `0700`. Do not place the
passwords directly in a command line, terminal transcript, or source-controlled file.

Apply every ordered schema migration with the owner connection:

```sh
CLAIMCORE_ADMIN_CONNECTION_FILE=/absolute/private/path/admin.connection \
dotnet run --project src/ClaimCore.Database --configuration Release --no-build -- migrate
```

The Web and CLI applications must use `app.connection`, never the owner connection. Database
administration and retention details are in [PostgreSQL storage and administration](database.md).

## Publish the Web application

Produce the browser assets once, then publish the host into a new ignored output directory. Keep this
shell open because the launch command below reuses the selected path:

```sh
npm --prefix web run build
claimcore_publish_root="artifacts/local-publish/run-$(date -u +%Y%m%dT%H%M%SZ)"
test ! -e "$claimcore_publish_root"
dotnet publish src/ClaimCore.Web/ClaimCore.Web.fsproj --configuration Release --no-restore --output "$claimcore_publish_root/web" -p:UseAppHost=false
```

Publication fails if the browser assets do not match the exact source, lock file, generated contract,
Node/npm toolchain, or asset manifest.

## Create a local certificate

The current host accepts a PKCS#12/PFX certificate with a private key and a blank PFX password. The
following example creates a short-lived self-signed certificate for `localhost`. Run it with a
private umask and replace `/absolute/private/path` with the directory chosen above:

```sh
umask 077
openssl req -x509 -newkey rsa:2048 -nodes -days 7 \
  -subj /CN=localhost -addext 'subjectAltName=DNS:localhost' \
  -keyout /absolute/private/path/claimcore-web.key \
  -out /absolute/private/path/claimcore-web.pem
openssl pkcs12 -export \
  -out /absolute/private/path/claimcore-web.pfx \
  -inkey /absolute/private/path/claimcore-web.key \
  -in /absolute/private/path/claimcore-web.pem -passout pass:
```

Before deleting the intermediate files, record the expected public-certificate fingerprint and verify
that the PFX can be opened with its blank password:

```sh
openssl x509 -in /absolute/private/path/claimcore-web.pem -noout -subject -ext subjectAltName -fingerprint -sha256
openssl pkcs12 -in /absolute/private/path/claimcore-web.pfx -passin pass: -info -noout
```

Delete the raw private-key file after that check. Keep the PEM certificate only as long as it is
needed for local trust setup or the served-certificate comparison below. Keep the PFX owner-private.

## Launch and log in

Create an owner-private Web state directory outside the checkout, then launch the published host:

```sh
CLAIMCORE_CONNECTION_FILE=/absolute/private/path/app.connection \
CLAIMCORE_WEB_CERTIFICATE_PATH=/absolute/private/path/claimcore-web.pfx \
CLAIMCORE_WEB_STATE_DIR=/absolute/private/path/web-state \
dotnet "$claimcore_publish_root/web/ClaimCore.Web.dll"
```

The terminal prints two non-secret locations: the local HTTPS login URL and the path of the current
bootstrap credential file. Open that URL, read the credential directly from its private file, and
paste it into the login form. Do not print, copy into shell history, or upload the credential.

The self-signed certificate is not trusted automatically. Before accepting a browser exception or
adding the generated PEM certificate to a local trust store, compare the SHA-256 fingerprint printed
above with the certificate actually served by the running host:

```sh
openssl s_client -connect localhost:5443 -servername localhost </dev/null 2>/dev/null \
  | openssl x509 -noout -subject -ext subjectAltName -fingerprint -sha256
```

Proceed only when the fingerprints and `localhost` identity match. Trust only this generated
certificate, using the operating system or browser's local certificate controls; do not disable TLS
validation globally. If `CLAIMCORE_WEB_ORIGIN` selects another port, use that port in the command.

Use fictional case facts. The Web interface can open, amend, decide, withdraw a decision, record or
clear payment, close, and reopen a case. Its Recovery view handles exact retained preparations.

For a structured terminal lifecycle instead, follow the [synthetic CLI
walkthrough](../examples/README.md).

## Stop without destroying data

Stop the Web host with the terminal interrupt, then stop Compose:

```text
docker compose down
```

That command keeps the PostgreSQL volume. Volume removal destroys the synthetic database and must be
a separate, deliberate action. Delete private credentials, certificates, downloads, and state only
when their retention is no longer required.
