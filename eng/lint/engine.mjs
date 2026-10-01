import { existsSync } from "node:fs";
import { join } from "node:path";
import { readSource, repositoryFiles } from "./files.mjs";
import { reconcile } from "./match.mjs";
import { Report } from "./model.mjs";
import { checkFSharpLint, checkOxlintLimits, checkPythonPolicy } from "./policies/limits.mjs";
import { checkSource } from "./policies/sources.mjs";
import { loadRegistry } from "./registry.mjs";
import { scanFile } from "./scan.mjs";

/**
 * Check one repository tree against its central exception registry.
 * @param {string} root Repository root.
 * @param {string} [registryPath] Defaults to config/lint-exceptions.json under the root.
 * @returns {{ report: Report, registered: number, active: number }}
 */
export function checkRepository(root, registryPath = join(root, "config/lint-exceptions.json")) {
  const report = new Report();
  const registry = loadRegistry(root, registryPath, report);
  const generated = registry.generated.map((entry) => entry.path);
  /** @type {import("./model.mjs").Occurrence[]} */
  const occurrences = [];
  for (const path of repositoryFiles(root, generated)) {
    if (
      !/\.(?:fs|fsi|fsx|fsproj|props|targets|ts|tsx|js|mjs|cjs|css|json|jsonc|toml|py|sh|yml|yaml|editorconfig)$|(?:^|\/)(?:\.[a-z]+ignore|\.shellcheckrc|\.editorconfig)$/u.test(
        path,
      )
    ) {
      continue;
    }
    const source = readSource(root, path);
    checkSource(source, report);
    occurrences.push(...scanFile(source));
  }
  checkFSharpLint(root, generated, report);
  for (const path of ["web/.oxlintrc.json", "eng/.oxlintrc.json"]) {
    if (existsSync(join(root, path))) {
      checkOxlintLimits(root, path, report);
    }
  }
  checkPythonPolicy(root, report);
  reconcile(registry, occurrences, report);
  return { report, registered: registry.exceptions.length, active: occurrences.length };
}
