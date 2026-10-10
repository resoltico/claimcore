import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { chmodSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { installTool, installedTool } from "./tools.mjs";

const bytes = Buffer.from("Synthetic installed tool bytes");
/** @type {Record<string, import("./tools.mjs").Tool>} */
const tools = {
  probe: {
    version: "1.2.3",
    assets: {
      fixture: {
        url: "https://example.invalid/probe",
        archive: "file",
        sha256: createHash("sha256").update(bytes).digest("hex"),
      },
    },
  },
};

test("read-only tool admission refuses absent, malformed, wrong-pin, changed, linked and unsupported caches", async () => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-installed-tool-"));
  const options = { tools, platform: "fixture" };
  try {
    assert.equal(installedTool(root, "probe", options), null);
    const path = await installTool(root, "probe", { ...options, acquire: async () => bytes });
    const marker = join(root, "artifacts/tools/bin/.probe.json");
    const original = readFileSync(marker, "utf8");
    assert.equal(installedTool(root, "probe", options), path);
    assert.equal(installedTool(root, "probe", { ...options, platform: "unsupported" }), null);
    for (const value of [
      "{",
      JSON.stringify({ ...JSON.parse(original), version: "1.2.30" }),
      JSON.stringify({ ...JSON.parse(original), extra: true }),
    ]) {
      writeFileSync(marker, value);
      assert.equal(installedTool(root, "probe", options), null);
    }
    writeFileSync(marker, original);
    writeFileSync(path, "Changed executable");
    assert.equal(installedTool(root, "probe", options), null);
    writeFileSync(path, bytes);
    if (process.platform !== "win32") {
      chmodSync(path, 0o644);
      assert.equal(installedTool(root, "probe", options), null);
      chmodSync(path, 0o755);
    }
    rmSync(path);
    const outside = join(root, "outside");
    writeFileSync(outside, bytes);
    symlinkSync(outside, path);
    assert.equal(installedTool(root, "probe", options), null);
    assert.equal(
      readFileSync(marker, "utf8"),
      original,
      "Admission never repairs or writes the cache",
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
