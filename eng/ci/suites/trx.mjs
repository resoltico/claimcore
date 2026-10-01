// Strict verification of a Microsoft Testing Platform TRX report against a suite's committed names.
// A report qualifies only when it is complete (one summary, every other counter zero), every result
// passed and resolves to one definition and entry, and the tests it names are exactly the expected
// ones: nothing skipped, duplicated, renamed or extra.
import { basename } from "node:path";
import { childrenNamed, descendantsNamed, parseXml } from "./xml.mjs";

const namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu;
const zeroCounters = [
  "failed",
  "error",
  "timeout",
  "aborted",
  "inconclusive",
  "passedButRunAborted",
  "notRunnable",
  "notExecuted",
  "disconnected",
  "warning",
  "completed",
  "inProgress",
  "pending",
];

/**
 * @param {import("./xml.mjs").XmlElement} element
 * @param {string} name
 * @returns {string}
 */
function required(element, name) {
  const value = element.attributes[name];
  if (value === undefined) {
    throw new Error(`Missing TRX attribute '${name}'.`);
  }
  return value;
}

/**
 * @param {import("./xml.mjs").XmlElement} element
 * @param {string} name
 */
function identity(element, name) {
  const value = required(element, name);
  if (!uuid.test(value)) {
    throw new Error(`TRX attribute '${name}' is not a UUID.`);
  }
  return value.toLowerCase();
}

/**
 * @param {import("./xml.mjs").XmlElement} root
 * @returns {import("./xml.mjs").XmlElement}
 */
function summaryCounters(root) {
  const summaries = childrenNamed(root, "ResultSummary");
  const [summary] = summaries;
  const sets = summary ? childrenNamed(summary, "Counters") : [];
  const [counters] = sets;
  if (summaries.length !== 1 || sets.length !== 1 || !summary || !counters) {
    throw new Error("TRX must contain exactly one summary and counter set.");
  }
  if (summary.attributes["outcome"] !== "Completed") {
    throw new Error("TRX summary outcome is not Completed.");
  }
  const wanted = ["total", "executed", "passed", ...zeroCounters].sort();
  if (JSON.stringify(Object.keys(counters.attributes).sort()) !== JSON.stringify(wanted)) {
    throw new Error("TRX counters have missing or unexpected attributes.");
  }
  return counters;
}

/**
 * @param {import("./xml.mjs").XmlElement} counters
 * @param {string} name
 * @returns {number}
 */
function counterValue(counters, name) {
  const parsed = Number(counters.attributes[name]);
  if (!Number.isInteger(parsed) || parsed < 0) {
    throw new Error(`TRX counter '${name}' is invalid.`);
  }
  return parsed;
}

/**
 * @param {import("./xml.mjs").XmlElement} root
 * @param {number} expected
 * @returns {number} The total.
 */
function checkCounters(root, expected) {
  const counters = summaryCounters(root);
  for (const name of zeroCounters) {
    const value = counterValue(counters, name);
    if (value !== 0) {
      throw new Error(`TRX counter '${name}' is ${value}; expected zero.`);
    }
  }
  const total = counterValue(counters, "total");
  const [executed, passed] = [counterValue(counters, "executed"), counterValue(counters, "passed")];
  if (total !== expected || executed !== total || passed !== total || total === 0) {
    throw new Error(
      `TRX count mismatch: total=${total}, executed=${executed}, passed=${passed}, expected=${expected}.`,
    );
  }
  return total;
}

/**
 * @param {import("./xml.mjs").XmlElement} root
 * @returns {Map<string, { executionId: string, name: string, assembly: string }>}
 */
function readDefinitions(root) {
  /** @type {Map<string, { executionId: string, name: string, assembly: string }>} */
  const definitions = new Map();
  const executions = new Set();
  for (const test of descendantsNamed(root, "UnitTest")) {
    const [method] = childrenNamed(test, "TestMethod");
    const runs = childrenNamed(test, "Execution");
    const [run] = runs;
    if (!method || !run || runs.length !== 1) {
      throw new Error("TRX test definition is incomplete.");
    }
    const id = identity(test, "id");
    const executionId = identity(run, "id");
    if (definitions.has(id) || executions.has(executionId)) {
      throw new Error("TRX test definitions contain duplicate IDs.");
    }
    executions.add(executionId);
    const codeBase = required(method, "codeBase").replaceAll("\\", "/");
    const file = basename(codeBase);
    definitions.set(id, {
      executionId,
      name: required(test, "name"),
      assembly: file.replace(/\.[^.]*$/u, ""),
    });
  }
  return definitions;
}

/**
 * @param {import("./xml.mjs").XmlElement} root
 * @returns {Set<string>} `testId:executionId` pairs.
 */
function readEntries(root) {
  const containers = childrenNamed(root, "TestEntries");
  const [container] = containers;
  if (containers.length !== 1 || !container) {
    throw new Error("TRX must contain exactly one test-entry set.");
  }
  const pairs = childrenNamed(container, "TestEntry").map(
    (entry) => `${identity(entry, "testId")}:${identity(entry, "executionId")}`,
  );
  if (pairs.length === 0 || new Set(pairs).size !== pairs.length) {
    throw new Error("TRX test entries are missing, invalid or duplicated.");
  }
  return new Set(pairs);
}

/**
 * @param {import("./xml.mjs").XmlElement} result
 * @param {Map<string, { executionId: string, name: string, assembly: string }>} definitions
 * @param {string} assembly
 * @returns {{ id: string, pair: string, name: string }}
 */
function checkResult(result, definitions, assembly) {
  const id = identity(result, "testId");
  const executionId = identity(result, "executionId");
  const name = required(result, "testName");
  const definition = definitions.get(id);
  if (!definition) {
    throw new Error("TRX result does not resolve to one definition.");
  }
  if (definition.name !== name) {
    throw new Error("TRX result/definition names disagree.");
  }
  if (definition.executionId !== executionId) {
    throw new Error("TRX result/definition execution IDs disagree.");
  }
  if (definition.assembly !== assembly) {
    throw new Error("TRX codeBase names the wrong test assembly.");
  }
  if (required(result, "outcome") !== "Passed") {
    throw new Error(`TRX result '${name}' is not Passed.`);
  }
  return { id, pair: `${id}:${executionId}`, name };
}

/**
 * Verify one TRX report.
 * @param {string} text
 * @param {{ assembly: string, names: string[] }} expected
 * @returns {{ runId: string, names: string[] }} The tests the report names.
 */
export function verifyTrx(text, { assembly, names }) {
  const root = parseXml(text);
  if (root.name !== "TestRun" || root.attributes["xmlns"] !== namespace) {
    throw new Error("TRX root or namespace is invalid.");
  }
  const runId = identity(root, "id");
  const total = checkCounters(root, names.length);
  const definitions = readDefinitions(root);
  const entries = readEntries(root);
  const [results] = childrenNamed(root, "Results");
  const executed = results ? childrenNamed(results, "UnitTestResult") : [];
  if (executed.length !== total) {
    throw new Error("TRX result count differs from its summary.");
  }
  const checked = executed.map((result) => checkResult(result, definitions, assembly));
  const ids = new Set(checked.map((item) => item.id));
  const pairs = new Set(checked.map((item) => item.pair));
  if (ids.size !== checked.length) {
    throw new Error("TRX contains duplicate test results.");
  }
  if (ids.size !== definitions.size) {
    throw new Error("TRX definitions and results do not reconcile.");
  }
  if (pairs.size !== entries.size || [...pairs].some((pair) => !entries.has(pair))) {
    throw new Error("TRX results and test entries do not reconcile.");
  }
  const found = checked.map((item) => item.name);
  const wanted = new Set(names);
  if (found.length !== wanted.size || found.some((name) => !wanted.has(name))) {
    throw new Error("TRX test names differ from the committed inventory.");
  }
  return { runId, names: found };
}

/**
 * Verify the reports of one suite's partitions: each is complete on its own, none overlap and
 * together they are exactly the suite's inventory.
 * @param {{ text: string, names: string[] }[]} partitions Each report with the names it must hold.
 * @param {{ assembly: string, names: string[] }} whole
 */
export function verifyPartitions(partitions, { assembly, names }) {
  /** @type {string[]} */
  const union = [];
  for (const part of partitions) {
    union.push(...verifyTrx(part.text, { assembly, names: part.names }).names);
  }
  const wanted = new Set(names);
  if (union.length !== wanted.size || union.some((name) => !wanted.has(name))) {
    throw new Error("Partition reports overlap or do not cover the whole inventory.");
  }
}
