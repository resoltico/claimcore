import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, existsSync, readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { withTree } from "./test-support.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));

test("F# source discovery failure cannot become an empty successful lint or format run", () => {
  for (const name of ["Check-FSharpLint.sh", "Check-Fantomas.sh"]) {
    withTree(
      {
        [`eng/${name}`]: readFileSync(`${root}/eng/${name}`, "utf8"),
        "src/a.fs": "module Synthetic\n",
        "tests/a.fs": "module Synthetic\n",
        "bin/find": "#!/bin/sh\nprintf '%s\\n' 'SYNTHETIC_DISCOVERY_REFUSAL' >&2\nexit 23\n",
        "scratch/.keep": "",
      },
      (fixture) => {
        chmodSync(`${fixture}/bin/find`, 0o755);
        const result = spawnSync("bash", [`eng/${name}`], {
          cwd: fixture,
          env: {
            ...process.env,
            PATH: `${fixture}/bin:${process.env["PATH"]}`,
            TMPDIR: `${fixture}/scratch`,
          },
          encoding: "utf8",
          timeout: 10_000,
        });
        assert.notEqual(result.status, 0);
        assert.match(result.stderr, /SYNTHETIC_DISCOVERY_REFUSAL/u);
        if (name === "Check-Fantomas.sh") {
          assert.deepEqual(readdirSync(`${fixture}/scratch`), [".keep"]);
        }
      },
    );
  }
});

const docker = `#!${process.execPath}
import {appendFileSync, readFileSync, writeFileSync} from "node:fs";
const args = process.argv.slice(2);
const directory = process.env.CLAIMCORE_PROBE_STATE;
if (args[0] === "run") {
  if (process.env.CLAIMCORE_PROBE_RUN_REFUSAL === "true") { console.error("SYNTHETIC_RUN_REFUSAL"); process.exit(73); }
  const label = args.find(value => value.startsWith("org.claimcore."));
  writeFileSync(directory + "/label", label.split("=")[1]);
  console.log("a".repeat(64));
} else if (args[0] === "exec" && (args.includes("pg_isready") || (args.includes("-c") && args[args.indexOf("-c") + 1] === "SELECT 1"))) {
  process.exit(0);
} else if (args[0] === "exec") {
  console.error("SYNTHETIC_SQL_REFUSAL");
  process.exit(73);
} else if (args[0] === "inspect") {
  if (process.env.CLAIMCORE_PROBE_OWNER === "unavailable") process.exit(23);
  console.log(process.env.CLAIMCORE_PROBE_OWNER === "matching" ? readFileSync(directory + "/label", "utf8") : "foreign");
} else if (args[0] === "stop") {
  appendFileSync(directory + "/stopped", "owned\\n");
} else {
  process.exit(24);
}
`;

test("failed catalog producers stop only independently read-back owned containers and retain their output", () => {
  for (const name of ["Generate-CatalogManifest.sh", "Generate-WitnessCatalogManifest.sh"]) {
    for (const owner of ["matching", "foreign", "unavailable"]) {
      withTree(
        {
          [`eng/${name}`]: readFileSync(`${root}/eng/${name}`, "utf8"),
          "db/postgresql-baseline.json":
            '{"containerImage":"synthetic","major":18,"minimumMinor":6}',
          "bin/docker": docker,
          "state/.keep": "",
        },
        (fixture) => {
          chmodSync(`${fixture}/bin/docker`, 0o755);
          const result = spawnSync("bash", [`eng/${name}`], {
            cwd: fixture,
            encoding: "utf8",
            timeout: 10_000,
            env: {
              ...process.env,
              PATH: `${fixture}/bin:${process.env["PATH"]}`,
              CLAIMCORE_PROBE_STATE: `${fixture}/state`,
              CLAIMCORE_PROBE_OWNER: owner,
            },
          });
          assert.notEqual(result.status, 0);
          assert.match(result.stderr, /SYNTHETIC_SQL_REFUSAL/u);
          assert.equal(existsSync(`${fixture}/state/stopped`), owner === "matching");
          assert.equal(readdirSync(`${fixture}/artifacts`).length, 1);
          assert.equal(existsSync(`${fixture}/db/catalog-manifest.pg18.6.json`), false);
          assert.equal(existsSync(`${fixture}/db/witness-catalog.pg18.6.json`), false);
        },
      );
    }
  }
});

test("a failed synthetic backup drill retains its own diagnostic scratch", () => {
  withTree(
    {
      "eng/backup/Test-ManagedBackup.sh": readFileSync(
        `${root}/eng/backup/Test-ManagedBackup.sh`,
        "utf8",
      ),
      "db/postgresql-baseline.json": '{"containerImage":"synthetic"}',
      "bin/docker": docker,
      "bin/python3": "#!/bin/sh\nexit 0\n",
      "state/.keep": "",
    },
    (fixture) => {
      chmodSync(`${fixture}/bin/docker`, 0o755);
      chmodSync(`${fixture}/bin/python3`, 0o755);
      const result = spawnSync("bash", ["eng/backup/Test-ManagedBackup.sh"], {
        cwd: fixture,
        encoding: "utf8",
        timeout: 10_000,
        env: {
          ...process.env,
          PATH: `${fixture}/bin:${process.env["PATH"]}`,
          TMPDIR: `${fixture}/state`,
          CLAIMCORE_PG_BIN: `${fixture}/bin`,
          CLAIMCORE_PROBE_STATE: `${fixture}/state`,
          CLAIMCORE_PROBE_RUN_REFUSAL: "true",
        },
      });
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, /SYNTHETIC_RUN_REFUSAL/u);
      assert.match(result.stderr, /Failed backup drill retained private scratch/u);
      assert.equal(
        readdirSync(`${fixture}/state`).filter((name) => name.startsWith("claimcore-backup-test."))
          .length,
        1,
      );
    },
  );
});
