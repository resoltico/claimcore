import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { installTool } from "./tools.mjs";

/** @param {Buffer} bytes @returns {Record<string, import("./tools.mjs").Tool>} */
const toolsFor = (bytes) => ({
  probe: {
    version: "1",
    assets: {
      fixture: {
        url: "https://example.invalid/probe",
        archive: "file",
        sha256: createHash("sha256").update(bytes).digest("hex"),
      },
    },
  },
});

test("hash rejection never publishes executable bytes", async () => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-tool-test-"));
  try {
    await assert.rejects(
      installTool(root, "probe", {
        tools: toolsFor(Buffer.from("expected")),
        platform: "fixture",
        acquire: async () => Buffer.from("wrong"),
      }),
      /integrity verification/u,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("tampered executable bytes and malformed markers require verified reinstallation", async () => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-tool-test-"));
  const bytes = Buffer.from("verified synthetic executable");
  let acquisitions = 0;
  const options = {
    tools: toolsFor(bytes),
    platform: "fixture",
    acquire: async () => {
      acquisitions += 1;
      return bytes;
    },
  };
  try {
    const path = await installTool(root, "probe", options);
    await installTool(root, "probe", options);
    assert.equal(acquisitions, 1);
    writeFileSync(path, "tampered");
    await installTool(root, "probe", options);
    assert.deepEqual(readFileSync(path), bytes);
    assert.equal(acquisitions, 2);
    writeFileSync(join(root, "artifacts/tools/bin/.probe.json"), "broken JSON");
    await installTool(root, "probe", options);
    assert.equal(acquisitions, 3);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("concurrent callers publish complete independently verified bytes", async () => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-tool-test-"));
  const bytes = Buffer.from("verified concurrent executable");
  try {
    const options = { tools: toolsFor(bytes), platform: "fixture", acquire: async () => bytes };
    const paths = await Promise.all([
      installTool(root, "probe", options),
      installTool(root, "probe", options),
    ]);
    assert.equal(paths[0], paths[1]);
    assert.deepEqual(readFileSync(paths[0] ?? ""), bytes);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
