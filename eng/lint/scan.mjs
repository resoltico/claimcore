import { basename } from "node:path";
import {
  scanBuildConfig,
  scanPyproject,
  scanShellcheckConfig,
  scanZizmorConfig,
} from "./scanners/config-tools.mjs";
import {
  scanCoverageConfig,
  scanIgnoreFile,
  scanKnipConfig,
  scanOxlintConfig,
  scanStylelintConfig,
  scanTypeScriptConfig,
} from "./scanners/config-web.mjs";
import { scanFSharp } from "./scanners/fsharp.mjs";
import { scanScriptComments, scanStyleComments } from "./scanners/javascript.mjs";
import { scanPython } from "./scanners/python.mjs";
import { scanScripts } from "./scanners/scripts.mjs";

const javascript = /\.(?:ts|tsx|js|mjs|cjs)$/u;
const fsharp = /\.(?:fs|fsi|fsx)$/u;
const buildFiles = /(?:\.fsproj|\.props|\.targets)$|(?:^|\/)\.editorconfig$/u;

/**
 * Which scanner reads which file. Each entry names the files it applies to by path and base name.
 * @type {Array<{ applies: (path: string, name: string) => boolean, scan: (source: import("./files.mjs").SourceFile, name: string) => import("./model.mjs").Occurrence[] }>}
 */
const scanners = [
  {
    applies: (path) => fsharp.test(path),
    scan: ({ path, lines, text }) => scanFSharp(path, lines, text),
  },
  {
    applies: (path) => javascript.test(path),
    scan: ({ path, text, lines }) => scanScriptComments(path, text, lines),
  },
  {
    applies: (path) => path.endsWith(".css"),
    scan: ({ path, lines }) => scanStyleComments(path, lines),
  },
  { applies: (path) => path.endsWith(".py"), scan: ({ path, lines }) => scanPython(path, lines) },
  {
    applies: (path) => /\.(?:sh|yml|yaml)$/u.test(path),
    scan: ({ path, lines }) => scanScripts(path, lines),
  },
  {
    applies: (path) => buildFiles.test(path),
    scan: ({ path, text }) => scanBuildConfig(path, text),
  },
  {
    applies: (_, name) => /^\.oxlintrc\.jsonc?$/u.test(name),
    scan: ({ path, text }) => scanOxlintConfig(path, text),
  },
  {
    applies: (_, name) => /^tsconfig.*\.json$/u.test(name),
    scan: ({ path, text }) => scanTypeScriptConfig(path, text),
  },
  {
    applies: (_, name) => name === "knip.json",
    scan: ({ path, text }) => scanKnipConfig(path, text),
  },
  {
    applies: (_, name) => name === ".prettierignore",
    scan: ({ path, lines }) => scanIgnoreFile(path, lines, "prettier"),
  },
  {
    applies: (_, name) => name === ".stylelintignore",
    scan: ({ path, lines }) => scanIgnoreFile(path, lines, "stylelint"),
  },
  {
    applies: (_, name) => /^stylelint\.config\./u.test(name),
    scan: ({ path, lines }) => scanStylelintConfig(path, lines),
  },
  {
    applies: (path, name) =>
      /^vite\.config\./u.test(name) || (name === "package.json" && path.startsWith("web/")),
    scan: ({ path, lines }) => scanCoverageConfig(path, lines),
  },
  {
    applies: (_, name) => name === "pyproject.toml",
    scan: ({ path, text }) => scanPyproject(path, text),
  },
  {
    applies: (_, name) => /^zizmor\.ya?ml$/u.test(name),
    scan: ({ path, text }) => scanZizmorConfig(path, text),
  },
  {
    applies: (_, name) => name === ".shellcheckrc",
    scan: ({ path, lines }) => scanShellcheckConfig(path, lines),
  },
];

/**
 * Every suppression, inline or in configuration, that one file contains.
 * @param {import("./files.mjs").SourceFile} source
 * @returns {import("./model.mjs").Occurrence[]}
 */
export function scanFile(source) {
  const name = basename(source.path);
  return scanners
    .filter(({ applies }) => applies(source.path, name))
    .flatMap(({ scan }) => scan(source, name));
}
