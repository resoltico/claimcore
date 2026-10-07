import assert from "node:assert/strict";
import test from "node:test";
import { shellFindings } from "./shell.mjs";

const limits = { lines: 50, complexity: 12, parameters: 5 };
const findings = (/** @type {string} */ source) => shellFindings(source, "synthetic.sh", limits);

test("native Bash syntax refuses oversized functions and counts nested definitions within their parent", () => {
  assert.deepEqual(findings(`bounded() {\n${"  :\n".repeat(48)}}\n`), []);
  assert.match(findings(`oversized() {\n${"  :\n".repeat(49)}}\n`).join("\n"), /51 lines/u);
  assert.match(
    findings(`outer() {\n inner() {\n${"  :\n".repeat(48)} }\n}\n`).join("\n"),
    /outer/u,
  );
});

test("native Bash decision boundaries include logical tests and command branches", () => {
  assert.deepEqual(findings(`bounded() {\n${"  if true; then :; fi\n".repeat(11)}}\n`), []);
  assert.match(
    findings(`complex() {\n${"  if true; then :; fi\n".repeat(12)}}\n`).join("\n"),
    /13 decisions/u,
  );
  assert.match(
    findings(`logical() {\n${"  [[ -n a && -n b ]] && :\n".repeat(6)}}\n`).join("\n"),
    /13 decisions/u,
  );
});

test("literal argument-looking text does not disguise real positional reads or missing parsing", () => {
  assert.deepEqual(findings("bounded() { echo '$6'; echo \"$5\"; }\n"), []);
  assert.match(findings('wide() { echo "$6"; }\n').join("\n"), /positional parameter 6/u);
  assert.throws(() => findings("broken() {"), /Native shell parsing refused/u);
  assert.throws(
    () => shellFindings("bounded() { :; }", "synthetic.sh", limits, "/absent/parser"),
    /Native shell parsing refused/u,
  );
});

test("native arithmetic ternaries, loops and case branches cannot escape complexity", () => {
  assert.match(
    findings(`arithmetic() {\n${"  echo $((x ? 1 : 2));\n".repeat(12)}}\n`).join("\n"),
    /13 decisions/u,
  );
  assert.match(
    findings(`loops() {\n${"  for ((i=0;i<1;i++)); do :; done\n".repeat(12)}}\n`).join("\n"),
    /13 decisions/u,
  );
  const cases = Array.from({ length: 12 }, (_, index) => `${index}) :;;`).join("\n");
  assert.match(findings(`cases() { case "$x" in\n${cases}\nesac; }\n`).join("\n"), /13 decisions/u);
});
