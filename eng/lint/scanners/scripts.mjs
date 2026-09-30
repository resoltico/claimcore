import { inline, inlineRules } from "./comments.mjs";

const shellcheck = /#\s*shellcheck\s+disable=(?<rules>[A-Za-z0-9,]+)/i;
const powershell = /SuppressMessage(?:Attribute)?\s*\(\s*(?:'|")(?<rule>[^'"]+)(?:'|")/i;
const yamllint = /#\s*yamllint\s+disable(?:-line)?(?<rules>[^\r\n]*)/i;

/**
 * Shell, PowerShell and YAML suppression comments.
 * @param {string} file
 * @param {string[]} lines
 * @returns {import("../model.mjs").Occurrence[]}
 */
export function scanScripts(file, lines) {
  /** @type {import("../model.mjs").Occurrence[]} */
  const found = [];
  const shell = file.endsWith(".sh");
  const pwsh = file.endsWith(".ps1") || file.endsWith(".psm1") || file.endsWith(".psd1");
  const yaml = file.endsWith(".yml") || file.endsWith(".yaml");
  lines.forEach((line, index) => {
    const check = shell ? shellcheck.exec(line) : null;
    if (check)
      found.push(
        ...inlineRules(
          file,
          "shellcheck",
          (check.groups?.["rules"] ?? "").replaceAll(",", " "),
          lines,
          index,
        ),
      );
    const attribute = pwsh ? powershell.exec(line) : null;
    if (attribute)
      found.push(inline(file, "psscriptanalyzer", attribute.groups?.["rule"] ?? "", lines, index));
    const lint = yaml ? yamllint.exec(line) : null;
    if (lint) found.push(...inlineRules(file, "yamllint", lint.groups?.["rules"], lines, index));
  });
  return found;
}
