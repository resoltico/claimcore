# Getting started

Create a fresh local installation with one PostgreSQL database, an HTTPS identity provider,
and the browser service. Use fictional case data. The supplied local certificates and identity
configuration are for evaluation; operators must review the [trust and retention limits](operations.md)
before using personal data.

## Prerequisites

Use macOS or Linux with Git, Bash, OpenSSL, Docker Engine with Compose, the .NET SDK in
[`global.json`](../global.json), and Node/npm versions declared by
[`.node-version`](../.node-version) and [`web/package.json`](../web/package.json).
Use your intended local Docker engine. Downloads require network access. No C compiler is needed.

Clone into a new directory, then restore and build the source:

```sh
git clone https://github.com/resoltico/claimcore.git claimcore-first-run
cd claimcore-first-run
dotnet restore ClaimCore.slnx --locked-mode
npm --prefix web ci
npm --prefix web run build
dotnet build ClaimCore.slnx --configuration Release --no-restore
```

For an unpublished change, select its reviewed branch before restoring. The instructions in
a checkout describe that revision. Discover the CLI without service credentials:

```sh
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- help
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- describe summary
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- schema invocation
```

## Start a fresh installation

Choose an absolute configuration path that does not exist, an IANA business time zone,
and two unused loopback ports. The example uses 7443 and 7444. The time zone becomes immutable
at initialization. Do not reuse an existing database, configuration directory or volume.

```sh
claimcore_config="$HOME/claimcore-first-run-config"
bash deployment/configure-local.sh "$claimcore_config" Etc/UTC 7443 7444
docker compose --env-file "$claimcore_config/compose.env" -f deployment/compose.yaml up --build -d
docker compose --env-file "$claimcore_config/compose.env" -f deployment/compose.yaml ps -a
```

The initializer must exit successfully and Web must become healthy. PostgreSQL has no host
port. The service runs without a writable container filesystem; only its private configuration
is mounted read-only. Configuration generation refuses an existing path. Initialization refuses
an unsupported schema and supplies no conversion.

## Trust the certificate and sign in

Use a separate Firefox profile for evaluation trust. Install the NSS certificate utility
`certutil` as a prerequisite. Import only this installation's CA, then install the locked
Firefox engine into this new configuration directory:

```sh
mkdir -m 700 "$claimcore_config/browser-profile"
certutil -N --empty-password -d "sql:$claimcore_config/browser-profile"
certutil -A -d "sql:$claimcore_config/browser-profile" -n 'ClaimCore evaluation CA' -t 'C,,' -i "$claimcore_config/ca.pem"
certutil -L -d "sql:$claimcore_config/browser-profile"
export PLAYWRIGHT_BROWSERS_PATH="$claimcore_config/browser-runtime"
(cd web && npm exec -- playwright install firefox)
claimcore_firefox="$(cd web && node --input-type=module -e 'import { firefox } from "playwright"; console.log(firefox.executablePath());')"
"$claimcore_firefox" --no-remote --profile "$claimcore_config/browser-profile"
```

The certificate store must list this CA with SSL trust `C`. Certificate and hostname checks
remain enabled; do not dismiss a TLS warning to force a connection. These leaf certificates
expire after 30 days; the local CA expires after a year. This profile changes no operating
system trust store. Close this Firefox window when you finish; the terminal then continues.

Open `https://localhost:7443`. Choose sign in. The generated identity provider is
`https://identity.localhost:7444/realms/claimcore`. Sign in as `owner`; obtain its password
locally from `owner-login.password` in your configuration directory. Do not copy credentials
into terminal commands, logs or public reports. After authentication, the browser shows the
Cases view. An owner administers authority; owner status alone grants no case viewing or editing.
The resource-unavailable message is expected until a case role is granted.

The owner can administer authority. To use case commands, grant that human a scoped
`CASE_HANDLER` role through the authenticated authority API or CLI. Add `PAYMENT_RECORDER` when this person also records or clears payments. Use `authority.list`
to obtain the actor principal, then `authority.execute` with an operation ID, that `target`, and
`act: {kind: "SET_GRANT", role: "CASE_HANDLER", scope: {kind: "INSTALLATION"}, active: true}`.
Submit through the authenticated owner CLI; inspect `describe authority.execute` for the exact
schema. The first-run checkpoint is a signed-in browser with explicit case grants.

## Stop and retain data

```sh
docker compose --env-file "$claimcore_config/compose.env" -f deployment/compose.yaml down
```

This removes this project's containers and network, retaining its two named PostgreSQL and
identity volumes. Preserve its configuration and volumes if you intend to start it again.
Never use host-wide prune commands to clean up an installation. To discard a wholly disposable
installation, explicitly remove only its own volumes after confirming their project labels.

If startup or login fails, inspect only this project's status and private diagnostics. Check
ports, certificate trust and expiry, private-file permissions and the pinned tools. See
[Support](../SUPPORT.md) for redacted reporting. See [Operations](operations.md) for backups,
trusted checkpoints and the limits of live-database erasure.
