// How one registered stage becomes a command line and where it may run.
import { readdirSync } from "node:fs";
import { join } from "node:path";

/**
 * Files under `directories` whose name ends with `suffix`, sorted.
 * @param {string} root
 * @param {string[]} directories
 * @param {string} suffix
 * @returns {string[]}
 */
function filesEndingWith(root, directories, suffix) {
  /** @type {string[]} */
  const found = [];
  /** @param {string} directory */
  const walk = (directory) => {
    for (const entry of readdirSync(join(root, directory), { withFileTypes: true })) {
      const path = `${directory}/${entry.name}`;
      if (entry.isDirectory()) walk(path);
      else if (entry.name.endsWith(suffix)) found.push(path);
    }
  };
  for (const directory of directories) walk(directory);
  return found.sort();
}

/**
 * The command line of a stage with run id and root substituted and any registered files appended.
 * @param {import("./types.mjs").Stage} stage
 * @param {string} runId
 * @param {string} root
 * @returns {string[]}
 */
export function commandFor(stage, runId, root) {
  const argv = stage.argv.map((part) =>
    part.replaceAll("{runId}", runId).replaceAll("{root}", root),
  );
  if (stage.appendFiles) {
    const { directories, suffix } = stage.appendFiles;
    argv.push(...filesEndingWith(root, directories, suffix));
  }
  return argv;
}

/**
 * The environment a stage runs in: the process environment plus its own registered variables.
 * @param {import("./types.mjs").Stage} stage
 * @param {string} runId
 * @returns {NodeJS.ProcessEnv}
 */
export function environmentFor(stage, runId) {
  return {
    ...process.env,
    ...Object.fromEntries(
      Object.entries(stage.env ?? {}).map(([key, value]) => [
        key,
        value.replaceAll("{runId}", runId),
      ]),
    ),
  };
}
