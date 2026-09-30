import test from "node:test";
import { must } from "./test-support.mjs";
import assert from "node:assert/strict";
import { pages } from "./github-api.mjs";
/** @param {number} start @param {number} count */
const rows = (start, count) => Array.from({ length: count }, (_, i) => ({ id: start + i }));
for (const field of ["workflow_runs", "jobs"]) {
  test(`${field} enumeration verifies complete counts across pages`, async () => {
    let page = 0;
    const result = await pages(
      async () => ({
        total_count: 102,
        [field]: ++page === 1 ? rows(1, 100) : rows(101, 2),
      }),
      "items",
      field,
    );
    assert.equal(result.length, 102);
  });
  /** @type {Array<[string, import("./types.mjs").Json[]]>} */
  const refusals = [
    ["short page", [{ total_count: 2, [field]: rows(1, 1) }]],
    ["missing count", [{ [field]: rows(1, 1) }]],
    ["duplicate IDs", [{ total_count: 2, [field]: [{ id: 1 }, { id: 1 }] }]],
    [
      "changing total",
      [
        { total_count: 101, [field]: rows(1, 100) },
        { total_count: 100, [field]: [] },
      ],
    ],
    [
      "cross-page duplicate",
      [
        { total_count: 101, [field]: rows(1, 100) },
        { total_count: 101, [field]: rows(1, 1) },
      ],
    ],
  ];
  for (const [label, documents] of refusals)
    test(`${field} enumeration rejects ${label}`, async () => {
      let index = 0;
      await assert.rejects(pages(async () => must(documents[index++]), "items", field));
    });
}
