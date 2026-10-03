import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import test from "node:test";
import { verifyPartitions, verifyTrx } from "./trx.mjs";
import { parseXml } from "./xml.mjs";

const namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
const assembly = "Example.Tests";
const counterNames = [
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

/** @param {string} value */
const escape = (value) =>
  value.replaceAll("&", "&amp;").replaceAll('"', "&quot;").replaceAll("<", "&lt;");

/**
 * @param {string[]} names
 * @param {{ outcome?: string, assemblyFile?: string, counters?: Record<string, number> }} [change]
 */
function trx(names, { outcome = "Passed", assemblyFile = `${assembly}.dll`, counters = {} } = {}) {
  const tests = names.map((name) => ({ name, id: randomUUID(), execution: randomUUID() }));
  const counts = {
    total: names.length,
    executed: names.length,
    passed: names.length,
    ...Object.fromEntries(counterNames.map((name) => [name, 0])),
    ...counters,
  };
  const attributes = Object.entries(counts)
    .map(([key, value]) => `${key}="${value}"`)
    .join(" ");
  return `\uFEFF<?xml version="1.0" encoding="utf-8"?>
<TestRun id="${randomUUID()}" xmlns="${namespace}">
  <Results>
${tests.map((t) => `    <UnitTestResult executionId="${t.execution}" testId="${t.id}" testName="${escape(t.name)}" outcome="${outcome}" />`).join("\n")}
  </Results>
  <TestDefinitions>
${tests.map((t) => `    <UnitTest name="${escape(t.name)}" id="${t.id}"><Execution id="${t.execution}" /><TestMethod codeBase="C:\\work\\${assemblyFile}" name="m" /></UnitTest>`).join("\n")}
  </TestDefinitions>
  <TestEntries>
${tests.map((t) => `    <TestEntry testId="${t.id}" executionId="${t.execution}" />`).join("\n")}
  </TestEntries>
  <ResultSummary outcome="Completed"><Counters ${attributes} /></ResultSummary>
</TestRun>`;
}

const names = ["a > b", 'quote "x"', "plain"];

test("a complete passing report with the exact names is accepted, entities included", () => {
  assert.deepEqual(verifyTrx(trx(names), { assembly, names }).names.sort(), [...names].sort());
});

/** @type {Array<[string, () => string, RegExp]>} */
const refusals = [
  ["a failed counter", () => trx(names, { counters: { failed: 1 } }), /'failed' is 1/u],
  ["a mismatched total", () => trx(names, { counters: { total: 4 } }), /count mismatch/u],
  ["a non-passing outcome", () => trx(names, { outcome: "Failed" }), /is not Passed/u],
  ["the wrong assembly", () => trx(names, { assemblyFile: "Other.dll" }), /wrong test assembly/u],
  ["a renamed test", () => trx(["a > b", "quote", "plain"]), /differ from the committed/u],
  [
    "a document type declaration",
    () => trx(names).replace("<TestRun", '<!DOCTYPE x [<!ENTITY y "z">]><TestRun'),
    /declarations/u,
  ],
  ["a foreign namespace", () => trx(names).replace(namespace, "urn:other"), /namespace/u],
  [
    "an extra counter attribute",
    () => trx(names).replace('total="3"', 'total="3" bonus="0"'),
    /unexpected attributes/u,
  ],
  [
    "a missing entry set",
    () => trx(names).replace(/<TestEntries>[\s\S]*<\/TestEntries>/u, ""),
    /TestEntries/u,
  ],
];
for (const [name, build, message] of refusals) {
  test(`${name} is refused`, () => {
    assert.throws(() => verifyTrx(build(), { assembly, names }), message);
  });
}

test("partitions must be disjoint and cover the inventory", () => {
  const whole = ["a", "b", "c"];
  const parts = [
    { text: trx(["a", "b"]), names: ["a", "b"] },
    { text: trx(["c"]), names: ["c"] },
  ];
  verifyPartitions(parts, { assembly, names: whole });
  assert.throws(
    () => verifyPartitions([parts[0] ?? { text: "", names: [] }], { assembly, names: whole }),
    /do not cover/u,
  );
  assert.throws(
    () =>
      verifyPartitions(
        [
          { text: trx(["a", "b"]), names: ["a", "b"] },
          { text: trx(["b", "c"]), names: ["b", "c"] },
        ],
        { assembly, names: whole },
      ),
    /overlap/u,
  );
});

test("the XML reader keeps greater-than signs inside attribute values and rejects bad nesting", () => {
  assert.equal(parseXml('<a x="1 > 0"><b/></a>').attributes["x"], "1 > 0");
  assert.throws(() => parseXml("<a><b></a></b>"), /closing tag/u);
  assert.throws(() => parseXml('<a x="1" x="2"/>'), /Duplicate/u);
  assert.throws(() => parseXml("<a/><b/>"), /more than one/u);
  assert.throws(() => parseXml("<a>"), /incomplete/u);
});

test("duplicate names cannot replace an expected test or partition", () => {
  assert.throws(
    () => verifyTrx(trx(["a > b", "a > b", "plain"]), { assembly, names }),
    /committed inventory/u,
  );
  assert.throws(
    () =>
      verifyPartitions(
        [
          { text: trx(["a"]), names: ["a"] },
          { text: trx(["a", "c"]), names: ["a", "c"] },
        ],
        { assembly, names: ["a", "b", "c"] },
      ),
    /overlap/u,
  );
});

test("contradictory duplicate or misplaced report records are refused", () => {
  const source = trx(names);
  for (const section of ["Results", "TestDefinitions", "TestEntries"]) {
    const extra = source.replace(`</${section}>`, `</${section}><${section}/>`);
    assert.throws(() => verifyTrx(extra, { assembly, names }), /exactly one/u);
  }
  for (const record of ["UnitTestResult", "UnitTest", "TestEntry"]) {
    const extra = source.replace("</TestRun>", `<Unexpected><${record}/></Unexpected></TestRun>`);
    assert.throws(() => verifyTrx(extra, { assembly, names }), /outside/u);
  }
});

test("XML version declarations cannot loop or masquerade as other instructions", () => {
  for (const source of [
    '<?xml version="1.0"',
    "<a/><?xml",
    '<a/><?xml version="1.0"?>',
    '<?xml-stylesheet href="x"?><a/>',
  ]) {
    assert.throws(() => parseXml(source), /declaration/u);
  }
  assert.equal(parseXml('<?xml version="1.0"?><a/>').name, "a");
});

test("prototype-named XML attributes remain visible and duplicates refuse", () => {
  const root = parseXml('<a __proto__="value" constructor="method"/>');
  assert.equal(Object.hasOwn(root.attributes, "__proto__"), true);
  assert.equal(root.attributes["__proto__"], "value");
  assert.equal(root.attributes["constructor"], "method");
  assert.throws(() => parseXml('<a __proto__="one" __proto__="two"/>'), /Duplicate/u);
  assert.throws(
    () =>
      verifyTrx(trx(names).replace('total="3"', 'total="3" __proto__="0"'), { assembly, names }),
    /unexpected attributes/u,
  );
});
