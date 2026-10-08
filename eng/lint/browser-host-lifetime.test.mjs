import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { withTree } from "./test-support.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));

test("published browser host PID remains a waitable child of the per-engine caller", () => {
  withTree({ "credentials.json": '{"webClientSecret":"synthetic"}' }, (fixture) => {
    const script = `set -euo pipefail
      repo_root="$CLAIMCORE_PROBE_ROOT"; web_dll='synthetic.dll'; oidc_ca='synthetic.ca'
      mkdir "$CLAIMCORE_PROBE_FIXTURE/diagnostics"
      source "$repo_root/eng/PublishedBrowserRuntime.sh"
      jq() { :; }
      bash() { [[ "$1" == "$repo_root/eng/Generate-SyntheticWebTls.sh" && "$2" == "$CLAIMCORE_PROBE_FIXTURE" ]]; }
      dotnet() { [[ "$1" == "$web_dll" ]]; sleep 0.2; }
      host_pid=''
      start_browser_host "$CLAIMCORE_PROBE_FIXTURE" host_pid
      [[ "$host_pid" =~ ^[0-9]+$ ]]
      kill -0 "$host_pid"
      wait "$host_pid"
    `;
    const result = spawnSync("bash", ["-c", script], {
      env: {
        ...process.env,
        CLAIMCORE_PROBE_ROOT: root,
        CLAIMCORE_PROBE_FIXTURE: fixture,
        CLAIMCORE_TEST_OIDC_CREDENTIALS: `${fixture}/credentials.json`,
        CLAIMCORE_TEST_OIDC_ISSUER: "https://synthetic.invalid/realms/test",
        CLAIMCORE_TEST_WEB_ORIGIN: "https://synthetic.invalid:443",
      },
      encoding: "utf8",
      timeout: 10_000,
    });
    assert.equal(result.status, 0, result.stderr);
  });
});
