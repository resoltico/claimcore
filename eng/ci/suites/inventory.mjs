import { commandLine } from "../executable.mjs";
// Generated test inventories: one sorted file of display names per suite, committed so that adding,
// removing or renaming a test is visible in review, and checked against what the build discovers.
//
//   node eng/ci/suites/inventory.mjs --check|--write [--only id,id]
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { option } from "../process-support.mjs";
import { assemblyPath, inventoryPath, loadSuites } from "./registry.mjs";

const maxBuffer = 256 * 1024 * 1024;

/**
 * Ordinal (code unit) comparison, which is what every inventory is sorted by.
 * @param {string} left
 * @param {string} right
 * @returns {number}
 */
export function compareOrdinal(left, right) {
  if (left === right) {
    return 0;
  }
  return left < right ? -1 : 1;
}

/**
 * Sorted, unique, non-empty names.
 * @param {string[]} names
 * @returns {string[]}
 */
export function normalizeNames(names) {
  if (names.length === 0) {
    throw new Error("A suite inventory cannot be empty.");
  }
  if (names.some((name) => name.trim() === "" || name.includes("\n"))) {
    throw new Error("Test names must be non-empty single lines.");
  }
  const sorted = [...names].sort(compareOrdinal);
  const duplicate = sorted.find((name, index) => index > 0 && sorted[index - 1] === name);
  if (duplicate !== undefined) {
    throw new Error(`Test name is not unique: ${duplicate}`);
  }
  return sorted;
}

/** @param {string[]} names */
export const renderInventory = (names) => `${normalizeNames(names).join("\n")}\n`;

/** @param {string} text */
export const parseInventory = (text) =>
  normalizeNames(text.split("\n").filter((line) => line !== ""));

/**
 * What differs between the committed names and the discovered ones.
 * @param {string[]} committed
 * @param {string[]} discovered
 */
export function compareNames(committed, discovered) {
  const known = new Set(committed);
  const found = new Set(discovered);
  return {
    missing: committed.filter((name) => !found.has(name)),
    unexpected: discovered.filter((name) => !known.has(name)),
  };
}

/**
 * @param {string} command
 * @param {string[]} args
 * @param {{ cwd: string, env?: NodeJS.ProcessEnv }} options
 * @returns {string}
 */
function capture(command, args, { cwd, env }) {
  const result = spawnSync(...commandLine(command, args), {
    cwd,
    encoding: "utf8",
    maxBuffer,
    env: { ...process.env, ...env },
  });
  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(" ")} failed (${result.status ?? "signal"}).`);
  }
  return result.stdout;
}

/**
 * @param {string} root
 * @param {import("./registry.mjs").Suite} suite
 * @param {NodeJS.ProcessEnv} [env]
 * @returns {string[]}
 */
function listDotnet(root, suite, env = {}) {
  const binary = join(root, assemblyPath(suite));
  if (!existsSync(binary)) {
    throw new Error(`Build ${suite.assembly} before discovering its tests.`);
  }
  const listed = JSON.parse(
    capture("dotnet", [binary, "--list-tests", "json"], { cwd: root, env }),
  );
  if (listed.schemaVersion !== 1 || !Array.isArray(listed.tests)) {
    throw new Error(`${suite.assembly} discovery has an unexpected shape.`);
  }
  return listed.tests.map((/** @type {{ displayName: string }} */ test) => test.displayName);
}

/**
 * The tests of a .NET suite and of each registered partition. Every partition selects some tests,
 * none overlap, and together they are exactly the suite.
 * @param {string} root
 * @param {import("./registry.mjs").Suite} suite
 * @returns {{ names: string[], partitions: Record<string, string[]> }}
 */
export function discoverDotnet(root, suite) {
  const names = normalizeNames(listDotnet(root, suite));
  /** @type {Record<string, string[]>} */
  const partitions = {};
  if (suite.partitions === undefined) {
    return { names, partitions };
  }
  const { selector, ids } = suite.partitions;
  /** @type {string[]} */
  const union = [];
  for (const id of ids) {
    const part = listDotnet(root, suite, { [selector]: id });
    if (part.length === 0) {
      throw new Error(`Partition ${id} of ${suite.assembly} selects no tests.`);
    }
    partitions[id] = part;
    union.push(...part);
  }
  const { missing, unexpected } = compareNames(names, normalizeNames(union));
  if (missing.length > 0 || unexpected.length > 0) {
    throw new Error(`Partitions of ${suite.assembly} do not add up to the whole suite.`);
  }
  return { names, partitions };
}

/**
 * The tests a suite's build or tool currently discovers.
 * @param {string} root
 * @param {import("./registry.mjs").Suite} suite
 * @returns {string[]}
 */
export function discover(root, suite) {
  if (suite.kind === "dotnet") {
    return discoverDotnet(root, suite).names;
  }
  const web = join(root, "web");
  if (suite.kind === "vitest") {
    // Vitest expands parametrized titles only while running, so its names come from the run summary.
    const summary = join(root, "artifacts/frontend/vitest-summary.json");
    if (!existsSync(summary)) {
      throw new Error("Run npm --prefix web run test:unit before reading the vitest inventory.");
    }
    const { tests } = JSON.parse(readFileSync(summary, "utf8"));
    return normalizeNames(tests.map((/** @type {{ id: string }} */ test) => test.id));
  }
  const report = JSON.parse(
    capture("npx", ["playwright", "test", "--list", "--reporter=json"], {
      cwd: web,
      env: { CLAIMCORE_TEST_OIDC_CREDENTIALS: "listing-only" },
    }),
  );
  if (report.errors.length > 0) {
    throw new Error("Playwright could not list the browser tests.");
  }
  /** @type {string[]} */
  const titles = [];
  /** @param {{ specs?: { title: string }[], suites?: object[] }} node */
  const walk = (node) => {
    titles.push(...(node.specs ?? []).map((spec) => spec.title));
    for (const child of /** @type {typeof node[]} */ (node.suites ?? [])) {
      walk(child);
    }
  };
  report.suites.forEach(walk);
  return normalizeNames([...new Set(titles)]);
}

/**
 * Compare or rewrite the committed inventories.
 * @param {string} root
 * @param {{ write: boolean, only?: string[] }} options
 * @returns {string[]} Problems found; empty when every inventory is current.
 */
export function syncInventories(root, { write, only = [] }) {
  /** @type {string[]} */
  const errors = [];
  const suites = loadSuites(root);
  if (only.some((id) => !suites.some((suite) => suite.id === id))) {
    throw new Error("Inventory selection names an unknown suite.");
  }
  for (const suite of suites) {
    // A suite that only a run can enumerate is checked by its own report verification.
    const selected = only.length > 0 ? only.includes(suite.id) : suite.kind !== "vitest";
    if (!selected) {
      continue;
    }
    const path = join(root, inventoryPath(suite));
    const discovered = discover(root, suite);
    if (write) {
      mkdirSync(dirname(path), { recursive: true });
      writeFileSync(path, renderInventory(discovered));
      continue;
    }
    const committed = existsSync(path) ? parseInventory(readFileSync(path, "utf8")) : [];
    const { missing, unexpected } = compareNames(committed, discovered);
    if (missing.length > 0 || unexpected.length > 0) {
      errors.push(
        `${inventoryPath(suite)} differs from the build: ${missing.length} missing, ${unexpected.length} unexpected` +
          ` (for example ${[...missing, ...unexpected][0]}). Run inventory.mjs --write.`,
      );
    }
  }
  return errors;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const root = fileURLToPath(new URL("../../..", import.meta.url));
  const write = process.argv.includes("--write");
  if (write === process.argv.includes("--check")) {
    throw new Error("Use exactly one of --check and --write.");
  }
  const only = option(process.argv, "only", "").split(",").filter(Boolean);
  const errors = syncInventories(root, { write, only });
  for (const error of errors) {
    process.stderr.write(`${error}\n`);
  }
  process.stdout.write(errors.length === 0 ? "Test inventories are current.\n" : "");
  process.exitCode = errors.length === 0 ? 0 : 1;
}
