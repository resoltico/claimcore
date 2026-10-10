import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { executable } from "../executable.mjs";
import { producingInputDigest } from "./inputs.mjs";

const root = resolve(import.meta.dirname, "../../..");

/** @param {string} fixture @returns {number | null} */
const nativeVerify = (fixture) =>
  spawnSync(
    executable("dotnet"),
    [
      "fsi",
      "--exec",
      join(root, "eng/PublicationInputs.fsx"),
      "verify",
      fixture,
      join(fixture, "expected.txt"),
    ],
    { cwd: root, encoding: "utf8", timeout: 30_000 },
  ).status;

test("generated contracts and promotion state leave both producing-input readers unchanged", () => {
  const fixture = mkdtempSync(join(tmpdir(), "claimcore-generated-inputs-"));
  const specBytes = readFileSync(join(root, "config/publication-inputs.json"), "utf8");
  const spec = JSON.parse(specBytes);
  /** @param {string} path @param {string} bytes */
  const write = (path, bytes) => {
    mkdirSync(dirname(join(fixture, path)), { recursive: true });
    writeFileSync(join(fixture, path), bytes);
  };
  try {
    for (const path of spec.files) {
      write(path, "synthetic input\n");
    }
    for (const path of spec.directories) {
      mkdirSync(join(fixture, path), { recursive: true });
    }
    write("config/publication-inputs.json", specBytes);
    write("web/src/synthetic.ts", "export const value = 1;\n");
    const expected = producingInputDigest(fixture);
    write("expected.txt", expected);
    assert.equal(nativeVerify(fixture), 0);
    for (const path of [
      "contracts/endpointCatalog.ts",
      ".contracts-writer/owner.json",
      ".contracts-stage-synthetic/endpointCatalog.ts",
      ".contracts-backup-synthetic/endpointCatalog.ts",
    ]) {
      write(`web/src/generated/${path}`, "synthetic generated state\n");
    }
    assert.equal(producingInputDigest(fixture), expected);
    assert.equal(nativeVerify(fixture), 0);
    write("web/src/synthetic.ts", "export const value = 2;\n");
    assert.notEqual(producingInputDigest(fixture), expected);
    assert.equal(nativeVerify(fixture), 1);
  } finally {
    rmSync(fixture, { recursive: true, force: true });
  }
});
