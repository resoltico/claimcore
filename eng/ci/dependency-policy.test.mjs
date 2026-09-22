import test from "node:test";
import assert from "node:assert/strict";
import {
  version,
  newer,
  packageRows,
  safeFinding,
  validateHolds,
  classifyUpdates,
} from "./dependency-policy.mjs";
import { jsonProcess } from "./dependency-process.mjs";

const graph = new Map([
  ["nuget|example", new Set(["1.2.3"])],
  ["npm|@example/tool", new Set(["4.0.0"])],
]);
const hold = {
  ecosystem: "nuget",
  package: "Example",
  current: "1.2.3",
  latest: "2.0.0",
  owner: "maintainer",
  rationale: "The upstream transitive version is intentionally held.",
  reviewOn: "2026-12-01",
};
const registry = (entry = hold) => ({ version: 1, holds: [entry] });

test("compares exact stable versions without numeric truncation", () => {
  assert(newer("10.0.0", "9.999.99"));
  assert(!newer("1.2.3", "1.2.3"));
  assert.throws(() => version("1.2.x"));
  assert.throws(() => version("1.2.3; echo secret"));
});
test("security retains top-level and transitive findings", () => {
  const item = { id: "Example", resolvedVersion: "1.2.3" };
  const rows = packageRows(
    {
      projects: [
        {
          frameworks: [
            { topLevelPackages: [item], transitivePackages: [item] },
          ],
        },
      ],
    },
    ["topLevelPackages", "transitivePackages"],
  );
  assert.equal(rows.length, 2);
  assert.equal(
    safeFinding("nuget", "Example", "1.2.3", undefined, graph, "vulnerable")
      .kind,
    "vulnerable",
  );
});
test("safe dependency reports reject unknown package or graph versions", () => {
  assert.throws(() =>
    safeFinding("nuget", "PRIVATE-canary", "1.2.3", "2.0.0", graph, "update"),
  );
  assert.throws(() =>
    safeFinding("nuget", "Example", "0.0.0", "2.0.0", graph, "update"),
  );
  assert.throws(() => packageRows({}, []));
});
test("approved current holds remain valid without querying upstream latest", () => {
  const holds = validateHolds(registry(), graph, "2026-09-22");
  const finding = safeFinding(
    "nuget",
    "Example",
    "1.2.3",
    "2.0.0",
    graph,
    "update",
  );
  assert.equal(classifyUpdates([finding], holds)[0].held, true);
  assert.equal(
    classifyUpdates([{ ...finding, latest: "2.0.1" }], holds)[0].held,
    false,
  );
});
for (const [label, change] of [
  ["expired hold", { reviewOn: "2026-09-21" }],
  ["invalid calendar date", { reviewOn: "2026-02-30" }],
  ["absent current dependency", { current: "1.0.0" }],
  ["missing rationale", { rationale: "short" }],
])
  test(`refuses ${label}`, () =>
    assert.throws(() =>
      validateHolds(registry({ ...hold, ...change }), graph, "2026-09-22"),
    ));

test("metadata retries only transient failures with bounded attempt count", () => {
  let calls = 0;
  const pauses = [];
  const execute = () =>
    ++calls < 3
      ? { status: 1, stderr: "EAI_AGAIN private-host" }
      : { status: 0, stdout: "{}" };
  assert.deepEqual(
    jsonProcess("npm", [], ".", [0], execute, (ms) => pauses.push(ms)),
    {},
  );
  assert.equal(calls, 3);
  assert.deepEqual(pauses, [1000, 2000]);
});
test("metadata exhaustion and nontransient failures never disclose provider output", () => {
  let calls = 0;
  assert.throws(
    () =>
      jsonProcess("npm", [], ".", [0], () => {
        calls += 1;
        return { status: 1, stderr: "credentials PRIVATE" };
      }),
    /^Error: DEPENDENCY_COMMAND_FAILED$/u,
  );
  assert.equal(calls, 1);
  assert.throws(
    () =>
      jsonProcess(
        "npm",
        [],
        ".",
        [0],
        () => ({ status: 1, stderr: "EAI_AGAIN PRIVATE" }),
        () => {},
      ),
    /DEPENDENCY_METADATA_UNAVAILABLE/u,
  );
});
test("successful but malformed or empty metadata is never treated as current", () => {
  for (const stdout of ["", "not-json"])
    assert.throws(() =>
      jsonProcess("npm", [], ".", [0], () => ({ status: 0, stdout })),
    );
  assert.deepEqual(
    jsonProcess("npm", [], ".", [0, 1], () => ({ status: 1, stdout: "{}" })),
    {},
  );
});
