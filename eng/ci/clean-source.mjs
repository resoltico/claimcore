import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { executable } from "./executable.mjs";
import { copySource } from "./source-snapshot.mjs";
import { buildSuites } from "./suites/build.mjs";
import { loadSuites } from "./suites/registry.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));

const scratch = mkdtempSync(join(tmpdir(), "claimcore-clean-source-"));
try {
  const fingerprint = copySource(root, scratch);
  process.stdout.write(`Clean-source preflight: ${fingerprint}\n`);
  for (const solution of ["ClaimCore.slnx", "ClaimCore.PostgresQualification.slnf"]) {
    const result = spawnSync(executable("dotnet"), ["restore", solution, "--locked-mode"], {
      cwd: scratch,
      stdio: "inherit",
    });
    if (result.status !== 0) {
      throw new Error("Clean-source locked restore failed.");
    }
  }
  buildSuites(
    scratch,
    loadSuites(scratch).filter((suite) => suite.kind === "dotnet"),
    { restore: false },
  );
  process.stdout.write("Clean-source locked restores and all suite prerequisites passed.\n");
} finally {
  rmSync(scratch, { recursive: true, force: true });
}
