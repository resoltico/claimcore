// Runs the frontend's real oxlint configuration over synthetic sources in a temporary tree, so
// policy tests observe the same rules the lint gate applies.
import { execFile } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const web = fileURLToPath(new URL("..", import.meta.url));
const oxlint = join(web, "node_modules/oxlint/bin/oxlint");
const tsgolint = join(web, "node_modules/.bin/tsgolint");

/**
 * The frontend configuration with plugin paths made absolute and type information switched off,
 * for sources that live outside the project.
 * @param {boolean} typeAware
 * @returns {Record<string, unknown>}
 */
function detachedConfig(typeAware) {
  const config = JSON.parse(readFileSync(join(web, ".oxlintrc.json"), "utf8"));
  config.jsPlugins = config.jsPlugins.map(
    (/** @type {string | {name: string, specifier: string}} */ plugin) =>
      typeof plugin === "string"
        ? join(web, plugin)
        : { ...plugin, specifier: join(web, "node_modules", plugin.specifier, "index.js") },
  );
  config.options.typeAware = typeAware;
  delete config.$schema;
  return config;
}

/**
 * Run oxlint in `directory` and return the rule codes reported for its files.
 * @param {string} directory
 * @param {boolean} typeAware
 * @returns {Promise<string[]>}
 */
function run(directory, typeAware) {
  return new Promise((resolve, reject) => {
    execFile(
      process.execPath,
      [
        oxlint,
        "--config",
        ".oxlintrc.json",
        "--format",
        "json",
        ...(typeAware ? ["--type-aware", "--tsconfig", "tsconfig.json"] : []),
        ".",
      ],
      { cwd: directory, env: { ...process.env, OXLINT_TSGOLINT_PATH: tsgolint } },
      (error, stdout) => {
        // oxlint exits non-zero when it reports findings; only unparsable output is a failure.
        try {
          resolve(
            JSON.parse(stdout).diagnostics.map((/** @type {{ code: string }} */ item) => item.code),
          );
        } catch {
          reject(new Error(`oxlint produced no report${error ? `: ${error.message}` : ""}`));
        }
      },
    );
  });
}

/**
 * Lint each case as a project of its own, with the real configuration beside it, so the path under
 * test keeps its exact name and location.
 * @param {{ path: string, code: string }[]} cases Paths relative to the web project.
 * @param {{ typeAware?: boolean }} [options] Run the type-aware rules against a strict TypeScript project.
 * @returns {Promise<string[][]>} The rule codes that fired, one list per case in order.
 */
export async function lintCases(cases, { typeAware = false } = {}) {
  const directory = mkdtempSync(join(tmpdir(), "claimcore-lint-"));
  try {
    const config = JSON.stringify(detachedConfig(typeAware));
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
    const projects = cases.map(({ path, code }, index) => {
      const project = join(directory, `case-${index}`);
      mkdirSync(dirname(join(project, path)), { recursive: true });
      writeFileSync(join(project, ".oxlintrc.json"), config);
      writeFileSync(join(project, "tsconfig.json"), projectFile);
      writeFileSync(join(project, path), code);
      return project;
    });
    return await Promise.all(projects.map((project) => run(project, typeAware)));
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}
