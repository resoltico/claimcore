// Authored explanations stay authored; executable commands and pins are generated from their owners.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { loadTools } from "./tools.mjs";

/** @param {string} value */
const cell = (value) =>
  value.replaceAll("|", "\\|").replaceAll("<", "&lt;").replaceAll(">", "&gt;");

/** @param {string} root @returns {string} */
export function renderStages(root) {
  const lines = ["| Stage | Command | Inputs and ordering |", "| --- | --- | --- |"];
  const plan = JSON.parse(readFileSync(join(root, "eng/ci/stage-plans/quality.json"), "utf8"));
  for (const stage of /** @type {import("./types.mjs").Stage[]} */ (plan.stages)) {
    const env = Object.entries(stage.env ?? {}).map(
      ([key, value]) => `${key}=${JSON.stringify(value)}`,
    );
    const command = [...env, ...stage.argv].join(" ");
    const notes = [
      ...(stage.requires ?? []).map((tool) => `requires ${tool}`),
      ...(stage.after ?? []).map((id) => `after ${id}`),
      ...(stage.group ? [`resource ${stage.group}`] : []),
      ...(stage.exclusive ? ["exclusive"] : []),
      ...(stage.appendFiles
        ? [
            `append ${stage.appendFiles.suffix} source under ${stage.appendFiles.directories.join(", ")}`,
          ]
        : []),
    ];
    lines.push(`| ${stage.id} | ${cell(command)} | ${cell(notes.join("; "))} |`);
  }
  return `${lines.join("\n")}\n`;
}

/** @param {string} root @returns {string} */
export function renderTools(root) {
  const lines = ["| Tool | Version | Pinned platforms |", "| --- | --- | --- |"];
  for (const [name, tool] of Object.entries(loadTools(root)).sort(([left], [right]) =>
    left.localeCompare(right),
  )) {
    lines.push(`| ${name} | ${tool.version} | ${Object.keys(tool.assets).sort().join(", ")} |`);
  }
  return `${lines.join("\n")}\n`;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const root = process.cwd();
  const [, , section] = process.argv;
  if (section !== "tools" && section !== "stages") {
    throw new Error("Choose tools or stages for development documentation.");
  }
  process.stdout.write(section === "tools" ? renderTools(root) : renderStages(root));
}
