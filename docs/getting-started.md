# Getting started

This walkthrough takes a fresh checkout to a signed-in browser and one accepted synthetic case. It creates
a separate PostgreSQL installation, HTTPS identity provider and Web service. Use fictional data: the included
certificates and identity configuration are for evaluation. Read [Operations](operating.md) before using real data.

## Prerequisites

Use macOS or Linux with Git, Bash, OpenSSL, Docker Engine with Compose, and the .NET SDK in
[`global.json`](../global.json), and Node/npm versions declared by [`.node-version`](../.node-version) and
[`web/package.json`](../web/package.json). Use your intended local Docker engine. Downloads require network
access. The local CLI build uses the pinned .NET SDK. For isolated browser trust, also install the NSS
certificate utility `certutil` before starting.

Choose a new absolute checkout location, then prepare the CLI and locked browser tools. Compose builds the Web
and administration images. The example uses `$HOME/claimcore-first-run`; use the same chosen path in both
terminals if you select another location:

```sh
claimcore_source="$HOME/claimcore-first-run"
git clone https://github.com/resoltico/claimcore.git "$claimcore_source"
cd "$claimcore_source"
dotnet restore ClaimCore.slnx --locked-mode
dotnet build ClaimCore.slnx --configuration Release --no-restore
npm install --global --ignore-scripts "npm@$(node -p "require('./web/package.json').engines.npm")"
npm --prefix web ci
```

The build output lists the directory containing `claimcore.dll`. Run the `claimcore` apphost beside it,
or add that directory to `PATH`. The commands below assume it is on `PATH`.

Before signing in, explore offline `help configuration`, `help call` and `help delivery`.
`describe case open` shows inputs, and `case open ... --dry-run` prints a request without credentials.

The [CLI reference](cli.md) lists every supported verb and flag. Dry-run checks protocol shape and byte
limits; the service still checks domain rules, authority and current revision.

## Start a fresh installation

Choose an absolute configuration path that does not exist, an IANA business time zone, and two unused loopback
ports. The example uses 7443 and 7444. The time zone becomes immutable at initialization. Paths containing
carriage returns or line feeds are refused before creation; other path characters remain literal in the
generated Compose inputs and printed Bash command. Do not reuse an existing database, configuration directory
or volume.

```sh
claimcore_config="$HOME/claimcore-first-run-config"
bash deployment/configure-local.sh "$claimcore_config" Etc/UTC 7443 7444
docker compose --env-file "$claimcore_config/compose.env" -f deployment/compose.yaml up --build -d
docker compose --env-file "$claimcore_config/compose.env" -f deployment/compose.yaml ps -a
```

Compose waits for IdP readiness before Web; its management listener is container-loopback only. Require
successful initializer exit and healthy Web; repeat `ps -a` during startup. PostgreSQL has no host port. Web
has a read-only root/configuration, bounded ephemeral `/tmp`, and a protected persistent key-ring
directory (protected session tickets are in PostgreSQL). The producer configures your UID/GID for ClaimCore; PostgreSQL/Keycloak retain required ownership.
Preserve/protect keys across restarts under the [session policy](operating.md#access-review-and-sessions).
Initialization refuses unsupported schemas untouched; configuration generation refuses existing paths.

## Trust the certificate and sign in

Use a separate Firefox profile for evaluation trust. Import only this installation's CA, then install the
locked Firefox engine into this new configuration directory. On Linux, the installer also installs required
browser system libraries and may require administrator access:

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

Require SSL trust `C`; never bypass certificate/hostname warnings. Leaves expire after 30 days, the CA after a
year. This profile changes no system trust. Keep Firefox open for CLI login in a second terminal, then close
it after the walkthrough.

Open `https://localhost:7443`. Choose sign in. The generated identity provider is
`https://identity.localhost:7444/realms/claimcore`. Sign in as `owner`; obtain its password locally from
`owner-login.password` in your configuration directory. Do not copy credentials into terminal commands, logs
or public reports. After authentication, installation context selects the views that current grants allow. An
owner can administer authority; owner status alone grants no case viewing or editing. Cases become available
after a case role is granted.

## Configure the human owner CLI and grant case work

Keep the trusted evaluation Firefox open. In a second terminal, select the checkout and the same configuration
directory. The following URLs match the example ports; substitute your chosen ports if different. The CLI
needs its own HTTPS trust and interactive OIDC configuration; browser trust alone is insufficient. Use the
generated public CA, never its private key or a client secret:

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

Each CLI process opens PKCE in the system browser. If outside the trusted profile, copy the full authorization
URL into trusted Firefox and finish login as owner there. Keep URLs out of logs; never bypass TLS or install
machine-wide trust. The one-use 127.0.0.1 callback alone uses HTTP; service/issuer traffic stays HTTPS and
tokens remain process-local.

List the fresh installation's sole owner and use its human issuer/subject to grant case work:

```sh
claimcore authority list
claimcore authority grant \
  --principal-kind HUMAN --issuer "$CLAIMCORE_OIDC_ISSUER" --subject '<owner subject from listing>' \
  --role CASE_HANDLER --preview
```

Review the target and installation grant, then run the same verb without `--preview` to execute. The CLI
prepares a fresh operation ID; use explicit `--operation-id` when automation needs a chosen stable ID.
Inspect the typed result. For interrupted delivery, follow [delivery recovery](integrating.md#delivery-recovery).
`CASE_HANDLER` permits
registration, decisions and closing. Grant PAYMENT_RECORDER separately when the human also records/clears
payments. OWNER alone never grants case work. Use `--dry-run` to inspect a frame without authentication;
[examples](../examples/README.md) show case verbs and equivalent frames. These are synthetic evaluation
operations only.

In the trusted Firefox window, return to `https://localhost:7443`.

Reload Cases to obtain current capability advice. Choose Open a case and use synthetic values: reference
`START-0001`, incident `2026-09-01`, notification `2026-09-02`, country `LV`, claimant `Synthetic claimant`,
insurer `Synthetic insurer`, amount `100.00`, currency `EUR`. Review, confirm and record the change. Expect an
accepted receipt at revision 1 and the case fields on return to the case. You now have an explicitly granted human actor and one accepted synthetic case.

## Stop and retain data

```sh
docker compose --env-file "$claimcore_config/compose.env" -f deployment/compose.yaml down
```

This removes this project's containers and network while retaining its PostgreSQL, identity and Web key-ring
volumes. The key-ring volume binds to `key-ring` inside the configuration directory. Preserve that directory
and the database and identity volumes together if you intend to start the installation again. Never use
host-wide prune commands to clean up an installation. To discard a wholly disposable installation, explicitly
remove only its own volumes after confirming their project labels, and remove its configuration only when
the retained keys and private inputs are no longer needed.

If startup or login fails, inspect only this project's status and private diagnostics. Check ports,
certificate trust and expiry, private-file permissions and the pinned tools. See [Support](../SUPPORT.md) for
redacted reporting. See [Operations](operating.md) for backups, trusted checkpoints and the limits of
live-database erasure.

See the [glossary](glossary.md) for project terminology.
