import {
  cpSync,
  mkdtempSync,
  mkdirSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { resolve, join } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const web = fileURLToPath(new URL("..", import.meta.url));
const checkout = resolve(web, "..");
const temporary = realpathSync(mkdtempSync(join(tmpdir(), "claimcore-boundary-controls-")));
const work = join(temporary, "web");
const schema = "src/protocol/schema.ts";
/** @param {string} file @param {string} text */
const append = (file, text) =>
  writeFileSync(join(work, file), `${readFileSync(join(work, file), "utf8")}\n${text}`);
/** @param {string} file @param {string} text */
const prepend = (file, text) =>
  writeFileSync(join(work, file), `${text}\n${readFileSync(join(work, file), "utf8")}`);
/** @param {string} name */
const reset = (name) => {
  for (const path of ["src", "../contracts"]) {
    rmSync(resolve(work, path), { recursive: true, force: true });
    cpSync(resolve(web, path), resolve(work, path), { recursive: true });
  }
  process.stdout.write(`${name}\n`);
};
const lint = () =>
  spawnSync(
    process.execPath,
    [
      join(web, "node_modules/oxlint/bin/oxlint"),
      "--config",
      ".oxlintrc.json",
      "--tsconfig",
      "tsconfig.app.json",
      resolve(work, "src"),
      resolve(work, "../contracts"),
    ],
    { cwd: work, encoding: "utf8", maxBuffer: 8_388_608 },
  );
try {
  mkdirSync(work);
  cpSync(join(checkout, "config"), join(temporary, "config"), { recursive: true });
  for (const file of ["tsconfig.app.json", "tsconfig.base.json"]) {
    cpSync(join(web, file), join(work, file), { recursive: true });
  }
  mkdirSync(join(temporary, "src/ClaimCore.Contract"), { recursive: true });
  cpSync(
    join(checkout, "src/ClaimCore.Contract/Localization"),
    join(temporary, "src/ClaimCore.Contract/Localization"),
    { recursive: true },
  );
  symlinkSync(join(web, "node_modules"), join(work, "node_modules"), "dir");
  // Ported controls exercise the delivered boundary rules; full type-aware lint is a separate gate.
  const delivered = JSON.parse(readFileSync(join(web, ".oxlintrc.json"), "utf8"));
  const rules = new Set([
    "import/no-cycle",
    "no-restricted-imports",
    "no-restricted-globals",
    "no-restricted-properties",
  ]);
  /** @param {Record<string, unknown>} configured */
  const select = (configured) =>
    Object.fromEntries(Object.entries(configured).filter(([name]) => rules.has(name)));
  writeFileSync(
    join(work, ".oxlintrc.json"),
    JSON.stringify({
      plugins: ["eslint", "import"],
      categories: {
        correctness: "off",
        suspicious: "off",
        pedantic: "off",
        perf: "off",
        style: "off",
      },
      rules: select(delivered.rules),
      overrides: delivered.overrides.map(
        /** @param {{files:string[], excludeFiles?:string[], rules?:Record<string,unknown>}} override */
        (override) => ({
          files: override.files,
          excludeFiles: override.excludeFiles,
          rules: select(override.rules ?? {}),
        }),
      ),
    }),
  );
  reset("Checking delivered boundary rules");
  const baseline = lint();
  if (baseline.status !== 0) {
    throw new Error(`Boundary baseline failed:\n${baseline.stdout}\n${baseline.stderr}`);
  }
  /** @type {[string, string, () => void][]} */
  const controls = [
    [
      "protocol Date.now",
      "no-restricted-properties",
      () => append(schema, "export const clockControl = () => Date.now();"),
    ],
    [
      "protocol fetch",
      "no-restricted-globals",
      () => append(schema, 'export const fetchControl = () => fetch("x");'),
    ],
    [
      "protocol new Date",
      "no-restricted-globals",
      () => append(schema, "export const dateControl = () => new Date();"),
    ],
    [
      "protocol Math.random",
      "no-restricted-properties",
      () => append(schema, "export const randomControl = () => Math.random();"),
    ],
    ["protocol imports react", "no-restricted-imports", () => prepend(schema, 'import "react";')],
    [
      "protocol imports hooks",
      "no-restricted-imports",
      () => prepend(schema, 'import "../hooks/useRead";'),
    ],
    [
      "protocol runtime api import",
      "no-restricted-imports",
      () => prepend(schema, 'import { noticeFor } from "../api/notices"; void noticeFor;'),
    ],
    [
      "api imports views",
      "no-restricted-imports",
      () => prepend("src/api/outcomes.ts", 'import "../views/Dashboard";'),
    ],
    [
      "view bypasses service facade",
      "no-restricted-imports",
      () => prepend("src/views/CaseList.tsx", 'import "../api/transport";'),
    ],
    [
      "module cycle",
      "no-cycle",
      () => {
        writeFileSync(join(work, "src/utils/a1.ts"), 'import "./a2"; export const a1 = 1;');
        writeFileSync(join(work, "src/utils/a2.ts"), 'import "./a1"; export const a2 = 2;');
      },
    ],
    [
      "contracts crypto",
      "no-restricted-globals",
      () =>
        append("../contracts/client.ts", "export const cryptoControl = () => crypto.randomUUID();"),
    ],
  ];
  for (const [name, rule, mutate] of controls) {
    mutate();
    const result = lint();
    if (result.status === 0 || !(result.stdout + result.stderr).includes(rule)) {
      throw new Error(
        `Control ${name} did not detect ${rule}:\n${result.stdout}\n${result.stderr}`,
      );
    }
    reset(`PASS ${name} (${rule})`);
  }
  process.stdout.write("PASS all 11 boundary controls\n");
} finally {
  rmSync(temporary, { recursive: true, force: true });
}
