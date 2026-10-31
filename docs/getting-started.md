# Getting started

This walkthrough creates a separate local evaluation installation, grants its owner case rights, records
one synthetic case and finds its worklist row. Use fictional data with the supplied evaluation certificates
and identity configuration. Read [Operations](operating.md) before using real data.

## Prerequisites

Use macOS or Linux with Git, Bash, OpenSSL, Docker Engine with Compose, and the Node/npm versions declared
by [`.node-version`](../.node-version) and [`web/package.json`](../web/package.json). Downloads need network
access. When the NSS utility `certutil` is installed, configuration creates the isolated trusted Firefox
profile automatically. Otherwise it prints the exact commands to run after installing NSS tools. The optional
CLI section also needs the .NET SDK pinned in [`global.json`](../global.json).

Choose a new absolute checkout path and install the locked browser tools:

```sh
claimcore_source="$HOME/claimcore-first-run"
git clone https://github.com/resoltico/claimcore.git "$claimcore_source"
cd "$claimcore_source"
npm --prefix web ci
```

## Start a fresh installation

Choose an absolute configuration path that does not exist, an IANA business zone and two unused loopback
ports. This example uses 7443 and 7444. The zone becomes immutable at initialization. Do not reuse an
existing database, configuration directory or volume.

```sh
claimcore_config="$HOME/claimcore-first-run-config"
bash deployment/configure-local.sh "$claimcore_config" Etc/UTC 7443 7444
docker compose --env-file "$claimcore_config/compose.env" -f deployment/compose.yaml up --build -d
docker compose --env-file "$claimcore_config/compose.env" -f deployment/compose.yaml ps -a
```

Require a successful initializer exit and healthy Web service; repeat `ps -a` while startup is in progress.
Compose waits for the identity provider before Web. PostgreSQL has no host port; the identity management
listener is container-loopback only. The service uses a read-only root/configuration, bounded temporary
storage and a protected persistent key ring. Tickets live in PostgreSQL.

Configuration generation refuses existing paths and paths containing carriage returns or line feeds.
Initialization refuses unsupported schemas unchanged. Keep the database and key ring together under the
[session policy](operating.md#access-review-and-sessions).

## Trust the evaluation certificate

Configuration creates `$claimcore_config/browser-profile` and imports only this installation's public
CA when `certutil` is available. Check its isolated SSL trust:

```sh
certutil -L -d "sql:$claimcore_config/browser-profile"
```

Require `C,,` for `ClaimCore evaluation CA`. If setup reported missing `certutil`, install NSS tools and
run the exact quoted commands it printed first. No system trust store is changed. Evaluation leaves
expire after 30 days and the CA after a year; retain hostname and certificate checks.

Use an installed Firefox. On Linux, for example:

```sh
firefox --no-remote --profile "$claimcore_config/browser-profile" https://localhost:7443/
```

On macOS, the usual installed application is:

```sh
/Applications/Firefox.app/Contents/MacOS/firefox --no-remote \
  --profile "$claimcore_config/browser-profile" https://localhost:7443/
```

If Firefox is not installed, the locked Playwright Firefox is the fallback. It uses the same isolated
profile; on Linux its dependency installation may require administrator access:

```sh
export PLAYWRIGHT_BROWSERS_PATH="$claimcore_config/browser-runtime"
if [ "$(uname -s)" = Linux ]; then
  (cd web && npm exec -- playwright install --with-deps firefox)
else
  (cd web && npm exec -- playwright install firefox)
fi
claimcore_firefox="$(cd web && node --input-type=module -e 'import { firefox } from "playwright"; console.log(firefox.executablePath());')"
"$claimcore_firefox" --no-remote --profile "$claimcore_config/browser-profile" https://localhost:7443/
```

## Sign in and grant case rights

Open `https://localhost:7443/authority` in that Firefox profile and sign in as `owner`. Read its generated
password locally from `owner-login.password` in the configuration directory. Keep credentials out of
commands, logs and public reports. The identity provider is
`https://identity.localhost:7444/realms/claimcore` for these example ports.

Select the fresh installation's sole human owner, then add an installation-scope CASE_HANDLER grant.
Review the target, role and scope and confirm. OWNER administers authority but grants no case work by
itself. CASE_HANDLER permits opening, registration, decisions and closing; grant PAYMENT_RECORDER
separately when that person also records or clears payment facts.

Reload the browser page to obtain current rights, then open Cases. Authority and case grants remain separate
from membership in the identity provider.

## Record a case and find its row

Choose OPEN and enter synthetic values. Use dates no later than the current business date, for example:

| Input | Value |
| --- | --- |
| Reference | START-0001 |
| Incident | 1 September 2026 |
| First notification | 2 September 2026 |
| Country | LV |
| Claimant | Synthetic claimant |
| Insurer | Synthetic insurer |
| Claimed amount | 100.00 |
| Currency | EUR |

Review the canonical proposal and confirm. Expect a success notice naming revision 1 and a case page
showing the registration facts. Return to Cases and search for `START-0001`; its row shows the claimant,
claimed amount, Undecided progress and last-change time. The Open and undecided view includes it.

You now have one accepted case and a worklist that can find it. [Case work](case-work.md) explains decisions,
payments, correction and stewardship; [Integrating](integrating.md#delivery-recovery) explains recovery
from interrupted submission.

## Optional CLI setup

Keep the complete publish directory together. In a terminal using the same checkout and configuration
paths, publish the CLI with the pinned SDK and select the ready public profile generated by setup:

```sh
dotnet restore src/ClaimCore.Cli/ClaimCore.Cli.fsproj --locked-mode
dotnet publish src/ClaimCore.Cli/ClaimCore.Cli.fsproj --configuration Release --no-restore \
  --output "$claimcore_source/artifacts/local-cli"
export PATH="$claimcore_source/artifacts/local-cli:$PATH"
export XDG_CONFIG_HOME="$claimcore_config/cli"
export XDG_STATE_HOME="$claimcore_config/cli-state"
claimcore configure --show
claimcore case list --text START-0001
```

Use the generated public CA, not its private key or a client secret. CLI PKCE opens the system browser;
if it opens outside the trusted profile, copy the complete authorization URL into trusted Firefox and
finish login there. The one-use 127.0.0.1 callback uses HTTP; service and issuer traffic stays HTTPS.
Tokens remain process-local. Mutation recovery identities stay in the isolated state directory selected
above. Keep authorization URLs out of logs. Use `claimcore shell` for multiple
commands with one sign-in; type `case list --text START-0001`, then `exit`. The shell keeps history and
access/refresh tokens in memory, discards tokens after 15 idle minutes and writes no history file.

Offline `help`, `help configuration`, `help call`, `help delivery` and `describe case open` need no
credentials. `--dry-run` prints the prepared frame; the service still checks business rules and rights.
Use the [generated CLI reference](cli.md) for flags and choices, and
[examples](../examples/README.md) for synthetic exact frames. Existing-case text verbs can fetch the
revision and show an interactive review in a terminal; automation supplies `--expected-revision`.

## Stop and retain data

```sh
docker compose --env-file "$claimcore_config/compose.env" -f deployment/compose.yaml down
```

This removes this project's containers and network while retaining its database, identity and Web key-ring
volumes. The key-ring volume binds to `key-ring` in the configuration directory. Preserve that directory
and the retained volumes together if you intend to restart. To discard an entirely disposable installation,
remove only its own labelled volumes after confirming they are no longer needed. Host-wide prune commands
can remove unrelated work.

For startup or login problems, inspect this project's status and private diagnostics. Check ports,
certificate trust/expiry, private-file permissions and the pinned tools. See [Support](../SUPPORT.md) for
redacted reporting and [Operations](operating.md) for backups and erasure limits.
