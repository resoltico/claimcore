import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { installTool, loadTools } from "./tools.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
const scratch = mkdtempSync(join(tmpdir(), "claimcore-tools-smoke-"));
const versionArguments = /** @type {Record<string, string[]>} */ ({
  actionlint: ["-version"],
  gitleaks: ["version"],
  shfmt: ["--version"],
  uv: ["--version"],
});
try {
  const tools = loadTools(root);
  for (const name of Object.keys(tools)) {
    const path = await installTool(scratch, name, { tools });
    const result = spawnSync(path, versionArguments[name] ?? ["--version"], { stdio: "inherit" });
    if (result.status !== 0) {
      throw new Error("A verified pinned tool did not start successfully.");
    }
  }
  const name = Object.keys(tools)[0] ?? "";
  const changed = structuredClone(tools);
  const tool = changed[name];
  if (!tool) {
    throw new Error("The smoke test requires a pinned tool.");
  }
  for (const asset of Object.values(tool.assets)) {
    asset.sha256 = "0".repeat(64);
  }
  let rejected = false;
  try {
    await installTool(scratch, name, { tools: changed });
  } catch (error) {
    rejected = error instanceof Error && error.message.includes("integrity verification");
  }
  if (!rejected) {
    throw new Error("The tool hash-mismatch negative control did not reject its asset.");
  }
  process.stdout.write("Pinned tool execution and hash-mismatch control passed.\n");
} finally {
  rmSync(scratch, { recursive: true, force: true });
}
