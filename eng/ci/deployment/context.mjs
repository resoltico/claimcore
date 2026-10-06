import assert from "node:assert/strict";
import { existsSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";

/** @param {string} root @param {string} state
 * @param {(args: string[], expected?: number, input?: string) => string} docker
 */
export function contextChecks(root, state, docker) {
  const name = `operating-context-${process.pid}`;
  const privateDirectory = join(root, ".local", name);
  const privateFile = join(root, "eng", `${name}.pfx`);
  const exported = join(state, "source-context");
  const generatedFile = join(root, "web", "artifacts", `${name}.canary`);
  mkdirSync(privateDirectory, { recursive: true, mode: 0o700 });
  writeFileSync(join(privateDirectory, "excluded"), "synthetic exclusion canary", {
    flag: "wx",
    mode: 0o600,
  });
  writeFileSync(privateFile, "synthetic exclusion canary", { flag: "wx", mode: 0o600 });
  mkdirSync(join(root, "web", "artifacts"), { recursive: true });
  writeFileSync(generatedFile, "synthetic generated-output exclusion canary", {
    flag: "wx",
    mode: 0o600,
  });
  try {
    docker(
      ["build", "--file", "-", "--output", `type=local,dest=${exported}`, "."],
      0,
      "FROM scratch\nCOPY . /context\n",
    );
    assert.equal(existsSync(join(exported, "context", ".local")), false);
    assert.equal(existsSync(join(exported, "context", ".git")), false);
    assert.equal(existsSync(join(exported, "context", "artifacts")), false);
    assert.equal(existsSync(join(exported, "context", "eng", `${name}.pfx`)), false);
    assert.equal(existsSync(join(exported, "context", "web", "artifacts")), false);
    assert.equal(existsSync(join(exported, "context", "LICENSE")), true);
    assert.equal(existsSync(join(exported, "context", "src", "ClaimCore.Web", "Program.fs")), true);
  } finally {
    rmSync(privateDirectory, { recursive: true, force: true });
    rmSync(privateFile, { force: true });
    rmSync(generatedFile, { force: true });
    rmSync(exported, { recursive: true, force: true });
  }
}
