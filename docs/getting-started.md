# Getting started

Create a fresh local installation with one PostgreSQL database, an HTTPS identity provider,
and the browser service. Use fictional case data. The supplied local certificates and identity
configuration are for evaluation; operators must review the [trust and retention limits](operations.md)
before using personal data.

## Prerequisites

Use macOS or Linux with Git, Bash, OpenSSL, Docker Engine with Compose, the [native compiler prerequisites](development.md#first-checkout), and the .NET SDK in
[`global.json`](../global.json), and Node/npm versions declared by
[`.node-version`](../.node-version) and [`web/package.json`](../web/package.json).
Use your intended local Docker engine. Downloads require network access. The local CLI build needs
the native compiler/SDK above; image builds carry their pinned Linux compiler.
For isolated browser trust, also install the NSS certificate utility `certutil` before starting.

Choose a new absolute checkout location, then prepare the CLI and locked browser tools. Compose
builds the Web and administration images. The example uses
`$HOME/claimcore-first-run`; use the same chosen path in both terminals if you select another location:

```sh
claimcore_source="$HOME/claimcore-first-run"
git clone https://github.com/resoltico/claimcore.git "$claimcore_source"
cd "$claimcore_source"
dotnet restore src/ClaimCore.Cli --locked-mode
dotnet build src/ClaimCore.Cli --configuration Release --no-restore
npm --prefix web ci
```

Discover the CLI without service credentials:

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

The initializer must exit successfully and Web must become healthy; repeat `ps -a` while startup
is in progress. PostgreSQL has no host port. Web runs without a writable container filesystem;
its private configuration is mounted read-only. Configuration generation refuses an existing path.
Initialization refuses an unsupported schema and supplies no conversion.

## Trust the certificate and sign in

Use a separate Firefox profile for evaluation trust. Import only this installation's CA, then
install the locked Firefox engine into this new configuration directory. On Linux, the installer
also installs required browser system libraries and may require administrator access:

```sh
mkdir -m 700 "$claimcore_config/browser-profile"
certutil -N --empty-password -d "sql:$claimcore_config/browser-profile"
certutil -A -d "sql:$claimcore_config/browser-profile" -n 'ClaimCore evaluation CA' -t 'C,,' -i "$claimcore_config/ca.pem"
certutil -L -d "sql:$claimcore_config/browser-profile"
export PLAYWRIGHT_BROWSERS_PATH="$claimcore_config/browser-runtime"
if [ "$(uname -s)" = Linux ]; then
  (cd web && npm exec -- playwright install --with-deps firefox)
else
  (cd web && npm exec -- playwright install firefox)
fi
claimcore_firefox="$(cd web && node --input-type=module -e 'import { firefox } from "playwright"; console.log(firefox.executablePath());')"
"$claimcore_firefox" --no-remote --profile "$claimcore_config/browser-profile"
```

The certificate store must list this CA with SSL trust `C`. Certificate and hostname checks
remain enabled; do not dismiss a TLS warning to force a connection. These leaf certificates
expire after 30 days; the local CA expires after a year. This profile changes no operating
system trust store. Leave this trusted Firefox window open through the CLI grants below;
use a second terminal for those commands. Close Firefox when you finish the walkthrough.

Open `https://localhost:7443`. Choose sign in. The generated identity provider is
`https://identity.localhost:7444/realms/claimcore`. Sign in as `owner`; obtain its password
locally from `owner-login.password` in your configuration directory. Do not copy credentials
into terminal commands, logs or public reports. After authentication, the browser shows the
Cases view. An owner administers authority; owner status alone grants no case viewing or editing.
The resource-unavailable message is expected until a case role is granted.

## Configure the human owner CLI and grant case work

Keep the trusted evaluation Firefox open. In a second terminal, select the checkout and the
same configuration directory. The following URLs match the example ports; substitute your chosen
ports if different. The CLI needs its own HTTPS trust and interactive OIDC configuration;
browser trust alone is insufficient. Use the generated public CA, never its private key or a
client secret:

```sh
claimcore_source="$HOME/claimcore-first-run"
cd "$claimcore_source"
claimcore_config="$HOME/claimcore-first-run-config"
export CLAIMCORE_SERVICE_URL="https://localhost:7443/"
export CLAIMCORE_OIDC_ISSUER="https://identity.localhost:7444/realms/claimcore"
export CLAIMCORE_OIDC_CLIENT_ID=claimcore-cli
export CLAIMCORE_CLI_AUTH_MODE=interactive
export CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE="$claimcore_config/ca.pem"
export CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE="$claimcore_config/ca.pem"
umask 077
mkdir -m 700 "$claimcore_config/cli"
```

Each new CLI process starts authorization-code/PKCE in the system default browser. If that is
outside the trusted evaluation profile, copy the **full authorization URL** from its address bar
into the already-open trusted Firefox window and finish login there. Do not dismiss TLS warnings,
change the system default browser, or install machine-wide trust. Keep the URL out of terminal
commands/logs. The CLI continues when Firefox reaches its one-use loopback callback; HTTP on
`127.0.0.1` is the PKCE callback, while service and issuer traffic remains verified HTTPS.
Sign in as `owner` if asked and leave the trusted window open for later CLI calls. Tokens remain
process-local. Discovery needs no authentication and uses these literal forms:

```sh
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- describe endpoints authority.execute
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- schema authority.execute request
```

List the fresh installation's sole owner summary (grants are read separately with `authority.grants`). Keep the result in this private configuration directory:

```sh
umask 077
printf '%s\n' '{"endpoint":"authority.list","input":{"limit":50}}' | \
  dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- call > "$claimcore_config/cli/authority.json"
```

Require exit 0. Create one explicit installation grant
frame from the returned human principal. The operation ID is generated once and the file refuses
overwrite; preserve it for exact retries. Use it only for this fresh installation; the script
refuses an unexpected reply or an actor list other than one human:

```sh
node --input-type=module - "$claimcore_config/cli/authority.json" "$claimcore_config/cli/grant-case-handler.json" <<'JS'
import { readFileSync, writeFileSync } from "node:fs";
import { randomUUID } from "node:crypto";
const [source, destination] = process.argv.slice(2);
const reply = JSON.parse(readFileSync(source, "utf8"));
if (reply.endpoint !== "authority.list" || reply.outcome.tag !== "SUCCEEDED") {
  throw new Error("Owner authority listing did not succeed.");
}
const { items, nextCursor } = reply.outcome.data;
if (items.length !== 1 || nextCursor !== null || items[0].principal.kind !== "HUMAN") {
  throw new Error("Expected the sole human owner of a fresh installation.");
}
const frame = {
  endpoint: "authority.execute",
  input: {
    operationId: randomUUID(),
    target: items[0].principal,
    act: { kind: "SET_GRANT", role: "CASE_HANDLER", scope: { kind: "INSTALLATION" }, active: true }
  }
};
writeFileSync(destination, JSON.stringify(frame) + "\n", { mode: 0o600, flag: "wx" });
JS
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- call < "$claimcore_config/cli/grant-case-handler.json"
```

Require exit 0 and `ACCEPTED` (`ALREADY_ACCEPTED` on exact replay). If confirmation is uncertain,
repeat that same command with the same file; do not regenerate its operation ID.
`CASE_HANDLER` permits case registration, decisions and closing. Add a separate
`PAYMENT_RECORDER` grant using the same frame shape and a fresh operation ID when this person
also records or clears payments.

In the trusted Firefox window, return to `https://localhost:7443`.

Reload Cases to obtain current capability advice. Choose Open a case and use synthetic values:
reference `START-0001`, incident `2026-09-01`, notification `2026-09-02`, country `LV`, claimant
`Synthetic claimant`, insurer `Synthetic insurer`, amount `100.00`, currency `EUR`. Review,
confirm and record the change. Expect an accepted receipt at revision 1 and the thirteen case
fields on return to the case. You now have an explicitly granted human actor and one accepted
synthetic case; owner status alone never grants case work.

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
