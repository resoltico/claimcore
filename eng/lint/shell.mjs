// The pinned shfmt parser supplies real Bash syntax, including nested definitions and literals.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { table } from "./model.mjs";

/** @param {unknown} value @param {boolean} [excludeFunctions] */
function nodes(value, excludeFunctions = false) {
  /** @type {Record<string,unknown>[]} */
  const result = [];
  const pending = [value];
  for (let current = pending.pop(); current !== undefined; current = pending.pop()) {
    if (Array.isArray(current)) {
      pending.push(...current);
    } else if (current !== null && typeof current === "object") {
      const node = table(current);
      if (!excludeFunctions || node["Type"] !== "FuncDecl") {
        result.push(node);
        pending.push(...Object.values(node));
      }
    }
  }
  return result;
}

/** @param {Record<string,unknown>} node */
function decision(node) {
  if (["ForClause", "WhileClause"].includes(String(node["Type"]))) {
    return 1;
  }
  if (node["Type"] === "CaseClause" && Array.isArray(node["Items"])) {
    return node["Items"].length;
  }
  if (node["Type"] === "IfClause") {
    return Number(Array.isArray(node["Cond"]) && node["Cond"].length > 0);
  }
  if (node["Type"] === "BinaryArithm" && node["Op"] === "?") {
    return 1;
  }
  return Number(
    ["BinaryCmd", "BinaryTest", "BinaryArithm"].includes(String(node["Type"])) &&
      ["&&", "||"].includes(String(node["Op"])),
  );
}

/** @typedef {{lines:number, complexity:number, parameters:number}} ShellLimits */
/** @param {string} text @param {string} path @param {ShellLimits} limits @param {string} [parser] */
export function shellFindings(text, path, limits, parser = "shfmt") {
  const result = spawnSync(parser, ["-tojson"], {
    input: text,
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024,
    timeout: 30_000,
  });
  assert.ok(!result.error && result.status === 0, `Native shell parsing refused ${path}.`);
  const tree = JSON.parse(result.stdout);
  const findings = [];
  for (const unit of nodes(tree).filter((node) => node["Type"] === "FuncDecl")) {
    const first = Number(table(unit["Pos"])["Line"]);
    const last = Number(table(unit["End"])["Line"]);
    assert.ok(Number.isInteger(first) && Number.isInteger(last) && first > 0 && last >= first);
    const body = nodes(unit["Body"], true);
    const complexity = 1 + body.reduce((sum, node) => sum + decision(node), 0);
    const parameters = body
      .filter((node) => node["Type"] === "ParamExp")
      .map((node) => String(table(node["Param"])["Value"]))
      .filter((value) => /^\d+$/u.test(value))
      .map(Number);
    const name = String(table(unit["Name"])["Value"]);
    if (
      last - first + 1 > limits.lines ||
      complexity > limits.complexity ||
      parameters.some((value) => value > limits.parameters)
    ) {
      findings.push(
        `${path}:${first} ${name}: ${last - first + 1} lines, ${complexity} decisions, highest explicit positional parameter ${Math.max(0, ...parameters)}; limits ${limits.lines}/${limits.complexity}/${limits.parameters}.`,
      );
    }
  }
  return findings;
}
