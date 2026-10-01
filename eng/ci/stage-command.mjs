import { repositoryFiles } from "./repository.mjs";

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
    const files = repositoryFiles(root).filter(
      (path) =>
        path.endsWith(suffix) && directories.some((directory) => path.startsWith(`${directory}/`)),
    );
    if (files.length === 0) {
      throw new Error("A stage source selector matched no source files.");
    }
    argv.push(...files);
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
