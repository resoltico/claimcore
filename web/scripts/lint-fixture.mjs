// Runs the frontend's real oxlint configuration over synthetic sources in a temporary tree, so
// policy tests observe the same rules the lint gate applies.
import { execFile } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, relative, resolve as resolvePath } from "node:path";
import { fileURLToPath } from "node:url";

const web = fileURLToPath(new URL("..", import.meta.url));
const oxlint = join(web, "node_modules/oxlint/bin/oxlint");
const tsgolint = join(web, "node_modules/.bin/tsgolint");

/**
 * The frontend configuration with absolute plugin paths and prefixed overrides for fixture
 * projects beneath one temporary root.
 * @param {boolean} typeAware
 * @returns {Record<string, unknown>}
 */
function detachedConfig(typeAware) {
  const config = JSON.parse(readFileSync(join(web, ".oxlintrc.json"), "utf8"));
  config.extends = [fileURLToPath(new URL("../../config/oxlint.json", import.meta.url))];
  config.jsPlugins = config.jsPlugins.map(
    (/** @type {string | {name: string, specifier: string}} */ plugin) =>
      typeof plugin === "string"
        ? join(web, plugin)
        : { ...plugin, specifier: join(web, "node_modules", plugin.specifier, "index.js") },
  );
  config.options.typeAware = typeAware;
  config.overrides = config.overrides.map(
    (/** @type {{ files: string[], excludeFiles?: string[] }} */ override) => ({
      ...override,
      files: override.files.map((path) => `case-*/${path}`),
      ...(override.excludeFiles
        ? { excludeFiles: override.excludeFiles.map((path) => `case-*/${path}`) }
        : {}),
    }),
  );
  delete config.$schema;
  return config;
}

/**
 * Run oxlint in `directory` and return the rule codes reported for its files.
 * @param {string} directory
 * @param {boolean} typeAware
 * @param {number} expectedFiles
 * @returns {Promise<{ code: string, filename: string }[]>}
 */
function run(directory, typeAware, expectedFiles) {
  return new Promise((resolve, reject) => {
    execFile(
      process.execPath,
      [oxlint, "--format", "json", ...(typeAware ? ["--type-aware"] : []), "."],
      { cwd: directory, env: { ...process.env, OXLINT_TSGOLINT_PATH: tsgolint } },
      (error, stdout, stderr) => {
        // Findings use exit 1; tool failures or omitted fixtures must fail the controls.
        try {
          const report = JSON.parse(stdout);
          if (report.number_of_files !== expectedFiles || (error && error.code !== 1)) {
            throw new Error("oxlint did not inspect every fixture");
          }
          resolve(report.diagnostics);
        } catch {
          reject(
            new Error(
              `oxlint produced no report${error ? `: ${error.message}` : ""}: ${stderr} ${stdout}`,
            ),
          );
        }
      },
    );
  });
}

/**
 * Associate each diagnostic with its fixture; an unknown path must not silently lose a finding.
 * @param {string} directory
 * @param {string[]} files
 * @param {{ code: string, filename: string }[]} diagnostics
 * @returns {string[][]}
 */
function ruleCodes(directory, files, diagnostics) {
  const codes = files.map(() => /** @type {string[]} */ ([]));
  for (const diagnostic of diagnostics) {
    const index = files.indexOf(relative(directory, resolvePath(directory, diagnostic.filename)));
    const fixtureCodes = codes[index];
    if (!fixtureCodes) {
      throw new Error(`oxlint reported an unexpected fixture: ${diagnostic.filename}`);
    }
    fixtureCodes.push(diagnostic.code);
  }
  return codes;
}

/**
 * Lint independent fixture projects in one process. Prefixing every path pattern preserves each
 * relative path rule, and tsconfig discovery keeps type-aware cases isolated.
 * @param {{ path: string, code: string }[]} cases Paths relative to the web project.
 * @param {{ typeAware?: boolean }} [options] Run the type-aware rules against a strict TypeScript project.
 * @returns {Promise<string[][]>} The rule codes that fired, one list per case in order.
 */
export async function lintCases(cases, { typeAware = false } = {}) {
  const directory = realpathSync(mkdtempSync(join(tmpdir(), "claimcore-lint-")));
  try {
    writeFileSync(join(directory, ".oxlintrc.json"), JSON.stringify(detachedConfig(typeAware)));
    const projectFile = JSON.stringify({
      compilerOptions: {
        strict: true,
        target: "ES2022",
        module: "ESNext",
        moduleResolution: "bundler",
        jsx: "react-jsx",
        noEmit: true,
        skipLibCheck: true,
      },
      include: ["src"],
    });
    const files = cases.map(({ path }, index) => join(`case-${index}`, path));
    cases.forEach(({ path, code }, index) => {
      const project = join(directory, `case-${index}`);
      mkdirSync(dirname(join(project, path)), { recursive: true });
      writeFileSync(join(project, "tsconfig.json"), projectFile);
      writeFileSync(join(project, path), code);
    });
    const diagnostics = await run(directory, typeAware, cases.length);
    return ruleCodes(directory, files, diagnostics);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}
