import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const script = fileURLToPath(new URL("../Check-FSharpLint.sh", import.meta.url));
const roots = ["src", "tests", "eng"];
const fake = `#!${process.execPath}
import {appendFileSync, existsSync, writeFileSync} from "node:fs";
import {setTimeout} from "node:timers/promises";
const args=process.argv.slice(2);
const root=args[4].split("/")[0];
appendFileSync(process.env.TRACE, JSON.stringify(args)+"\\n");
if(args[3]==="wildcard") {
  writeFileSync(root+"/started", "");
  const bound=Date.now()+5000;
  while(!["src","tests","eng"].every(value=>existsSync(value+"/started"))) {
    if(Date.now()>bound) process.exit(2);
    await setTimeout(10);
  }
}
if(process.env.FAIL_ROOT===root) {
  console.error("Synthetic lint refusal: "+root);
  process.exit(1);
}
`;

/** @param {string} failure */
function qualify(failure) {
  const root = mkdtempSync(join(tmpdir(), "claimcore-lint-processes-"));
  try {
    for (const part of roots) {
      mkdirSync(join(root, part));
      for (const extension of ["fs", "fsi", "fsx"]) {
        writeFileSync(join(root, part, `source with spaces.${extension}`), "module Synthetic\n");
      }
    }
    mkdirSync(join(root, "bin"));
    writeFileSync(join(root, "bin", "dotnet"), fake, { mode: 0o755 });
    copyFileSync(script, join(root, "eng", "Check-FSharpLint.sh"));
    const trace = join(root, "trace");
    const result = spawnSync("bash", ["eng/Check-FSharpLint.sh"], {
      cwd: root,
      encoding: "utf8",
      timeout: 15_000,
      env: {
        ...process.env,
        PATH: `${join(root, "bin")}:${process.env["PATH"]}`,
        TRACE: trace,
        FAIL_ROOT: failure,
      },
    });
    assert.equal(result.error, undefined);
    assert.equal(result.status, failure === "" ? 0 : 1);
    const requests = readFileSync(trace, "utf8")
      .trim()
      .split("\n")
      .map((line) => JSON.parse(line));
    for (const part of roots) {
      const paths = requests
        .filter((args) => args[4].startsWith(`${part}/`))
        .map((args) => args[4]);
      const expected = [`${part}/**/*.fs`];
      if (part !== failure) {
        expected.push(`${part}/source with spaces.fsi`, `${part}/source with spaces.fsx`);
      }
      assert.deepEqual(paths.sort(), expected.sort());
    }
    if (failure !== "") {
      assert.match(result.stderr, new RegExp(`Synthetic lint refusal: ${failure}`, "u"));
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("FSharpLint starts all roots and retains exact wildcard, signature and script inputs", () => {
  qualify("");
});

test("a failure in any lint root survives waiting for every other root", () => {
  for (const root of roots) {
    qualify(root);
  }
});
