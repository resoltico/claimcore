import assert from "node:assert/strict";
import test from "node:test";
import { parseCoverageXml, parseXml } from "../suites/xml.mjs";
import { reconcileRawMeasurements } from "./measurement.mjs";

/** Independent producer-shaped records include both class lines and method detail.
 * @param {string[]} names */
function coverage(names) {
  const packages = names
    .map(
      (name) =>
        `<package name="${name}"><classes><class name="${name}.Measured"><methods><method name="Measured"><lines><line number="1" hits="1"/></lines></method></methods><lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)"/></lines></class></classes></package>`,
    )
    .join("");
  return `<coverage lines-valid="${names.length}" lines-covered="${names.length}" branches-valid="${names.length * 2}" branches-covered="${names.length * 2}"><packages>${packages}</packages></coverage>`;
}

test("detailed raw coverage beyond the TRX bound retains and reconciles production and tooling records", () => {
  const source = coverage(["ClaimCore.Domain", "ClaimCore.ContractGenerator"]).replace(
    "<packages>",
    `${" ".repeat(17 * 1024 * 1024)}<packages>`,
  );
  assert.throws(() => parseXml(source), /bounded size/u);
  const root = parseCoverageXml(source);
  assert.deepEqual(reconcileRawMeasurements(root).total, {
    lines: 2,
    coveredLines: 2,
    branches: 4,
    coveredBranches: 4,
  });
  assert.equal(root.children[0]?.children.length, 2);
});

test("coverage has its own fixed 64 MiB limit while general XML remains bounded at 16 MiB", () => {
  const limit = 64 * 1024 * 1024;
  const tags = "<coverage></coverage>";
  const source = tags.replace("</coverage>", `${" ".repeat(limit - tags.length)}</coverage>`);
  assert.equal(Buffer.byteLength(source), limit);
  assert.equal(parseCoverageXml(source).name, "coverage");
  assert.throws(() => parseCoverageXml(`${source} `), /bounded size/u);
  assert.throws(() => parseXml(`<test>${" ".repeat(16 * 1024 * 1024)}</test>`), /bounded size/u);
});

test("coverage retains strict XML and document-type refusal without resolving external declarations", () => {
  const source = coverage(["ClaimCore.Domain"]);
  assert.equal(
    parseCoverageXml(
      `<!DOCTYPE coverage SYSTEM "https://example.invalid/never-fetch.dtd">${source}`,
    ).name,
    "coverage",
  );
  for (const malformed of [
    "<TestRun/>",
    "<coverage><packages></coverage></packages>",
    "<coverage/><coverage/>",
    '<!DOCTYPE coverage [<!ENTITY bad "value">]><coverage/>',
    '<?xml-stylesheet href="https://example.invalid/never-fetch"?><coverage/>',
  ]) {
    assert.throws(() => parseCoverageXml(malformed));
  }
});

test("raw coverage refuses unknown, test-only, tooling-only and contradictory measurement records", () => {
  for (const names of [
    ["ClaimCore.Domain", "ClaimCore.Unknown"],
    ["ClaimCore.Domain", "ClaimCore.Tests"],
    ["ClaimCore.Tests"],
    ["ClaimCore.ContractGenerator"],
    ["ClaimCore.Domain", "ClaimCore.Domain"],
  ]) {
    assert.throws(() => reconcileRawMeasurements(parseCoverageXml(coverage(names))));
  }
  const source = coverage(["ClaimCore.Domain"]);
  assert.throws(
    () =>
      reconcileRawMeasurements(
        parseCoverageXml(source.replace('lines-covered="1"', 'lines-covered="2"')),
      ),
    /aggregate counters/u,
  );
  assert.throws(
    () => reconcileRawMeasurements(parseCoverageXml(source.replace("100% (2/2)", "100% (3/2)"))),
    /branch counts/u,
  );
});
