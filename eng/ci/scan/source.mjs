// Scan the repository's source files for credentials. The files git would list (tracked, plus
// untracked and not ignored) are copied into a private snapshot and scanned there with the
// scanner's own rules; a repository-local scanner configuration is refused rather than honoured.
import {
  chmodSync,
  copyFileSync,
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { runChild, scannerEnvironment } from "./process.mjs";
import { repositoryFiles } from "../repository.mjs";
import { resolveSourceFile } from "../repository-path.mjs";

/**
 * @param {string} root
 * @param {string[]} relativePaths
 * @param {string} snapshot
 */
function copyInventory(root, relativePaths, snapshot) {
  const exact = new Set();
  const portable = new Set();
  for (const relative of relativePaths) {
    if (exact.has(relative) || portable.has(relative.toLowerCase())) {
      throw new Error(
        "The source inventory contains a duplicate or cross-platform path collision.",
      );
    }
    exact.add(relative);
    portable.add(relative.toLowerCase());
    if (relative === ".gitleaks.toml") {
      throw new Error(
        "Repository-local Gitleaks configuration is forbidden by the source-scan policy.",
      );
    }
    const destination = join(snapshot, ...relative.split("/"));
    mkdirSync(dirname(destination), { recursive: true });
    copyFileSync(resolveSourceFile(root, relative), destination);
  }
}

/**
 * @param {{ root: string, gitleaks: string, stdout?: (text: string) => void, stderr?: (text: string) => void }} options
 * @returns {Promise<number>} The scanner's exit status; 0 means clean.
 */
export async function scanSource({
  root,
  gitleaks,
  stdout = (text) => process.stdout.write(text),
  stderr = (text) => process.stderr.write(text),
}) {
  const repository = resolve(root).replace(/[\\/]+$/u, "");
  if (!existsSync(repository) || lstatSync(repository).isSymbolicLink()) {
    throw new Error("The repository root does not exist or is a link.");
  }
  const scratch = mkdtempSync(join(tmpdir(), "claimcore-source-scan-"));
  try {
    chmodSync(scratch, 0o700);
    const snapshot = join(scratch, "source");
    mkdirSync(snapshot);
    const emptyIgnore = join(scratch, "empty.gitleaksignore");
    writeFileSync(emptyIgnore, "");
    const files = repositoryFiles(repository);
    if (files.length === 0) {
      throw new Error("The source inventory is empty.");
    }
    copyInventory(repository, files, snapshot);
    const result = await runChild(
      gitleaks,
      [
        "dir",
        "--redact",
        "--no-banner",
        "--no-color",
        "--exit-code",
        "1",
        "--ignore-gitleaks-allow",
        "--gitleaks-ignore-path",
        emptyIgnore,
        snapshot,
      ],
      { cwd: repository, env: scannerEnvironment() },
    );
    stdout(result.stdout);
    stderr(result.stderr);
    if (result.status === 0 && !result.overflow) {
      stdout(`Source secret scan passed for ${files.length} repository files.\n`);
    }
    return result.overflow ? 2 : result.status;
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}
