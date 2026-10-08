import assert from "node:assert/strict";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { copySource } from "../source-snapshot.mjs";
import { producingInputFiles } from "../publish/inputs.mjs";

/** @param {string} source @returns {string[]} */
function exclusionProbes(source) {
  const policy = JSON.parse(readFileSync(join(source, "config/git-ignore-policy.json"), "utf8"));
  assert.equal(policy.schemaVersion, 1);
  assert.ok(Array.isArray(policy.mustBeIgnored) && policy.mustBeIgnored.length > 0);
  return policy.mustBeIgnored;
}

/** @param {string} source @param {string[]} probes @returns {string[]} */
function writeCanaries(source, probes) {
  const paths = probes.flatMap((probe) => [
    probe,
    `eng/context-probes/${probe}`,
    `web/context-probes/${probe}`,
  ]);
  for (const path of paths) {
    const target = join(source, path);
    assert.equal(existsSync(target), false, "Synthetic context probe must start absent.");
    mkdirSync(dirname(target), { recursive: true, mode: 0o700 });
    writeFileSync(target, "synthetic context exclusion canary", { flag: "wx", mode: 0o600 });
  }
  return paths;
}

/** @param {string} source @param {string} exported @param {string[]} denied @param {string[]} required */
function verifyExport(source, exported, denied, required) {
  for (const path of denied) {
    assert.equal(
      existsSync(join(exported, "context", path)),
      false,
      `Synthetic Docker exclusion control failed: ${path}.`,
    );
  }
  for (const path of required) {
    const actual = join(exported, "context", path);
    assert.equal(
      existsSync(actual),
      true,
      "A producing input was omitted from the Docker context.",
    );
    assert.ok(
      readFileSync(actual).equals(readFileSync(join(source, path))),
      "Docker source input bytes changed.",
    );
  }
  for (const path of ["eng/.npmrc", "web/.npmrc", "LICENSE", "ClaimCore.slnx"]) {
    assert.ok(
      readFileSync(join(exported, "context", path)).equals(readFileSync(join(source, path))),
    );
  }
}

/** @param {string} root @param {string} state
 * @param {(args: string[], expected?: number, input?: string) => string} docker
 */
export function contextChecks(root, state, docker) {
  const scratch = realpathSync(mkdtempSync(join(realpathSync(tmpdir()), "claimcore-context-")));
  const source = join(scratch, "source");
  const exported = join(state, "source-context");
  let passed = false;
  try {
    copySource(root, source);
    const required = producingInputFiles(source);
    const denied = writeCanaries(source, exclusionProbes(source));
    docker(
      ["build", "--file", "-", "--output", `type=local,dest=${exported}`, source],
      0,
      "FROM scratch\nCOPY . /context\n",
    );
    verifyExport(source, exported, denied, required);
    assert.equal(existsSync(join(exported, "context", ".git")), false);
    passed = true;
  } finally {
    if (passed) {
      rmSync(scratch, { recursive: true });
      rmSync(exported, { recursive: true });
    } else {
      process.stderr.write(`Synthetic Docker failure retained: ${scratch}; ${exported}.\n`);
    }
  }
}
