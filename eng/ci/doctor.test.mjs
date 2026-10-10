import assert from "node:assert/strict";
import test from "node:test";
import { reportedVersion } from "./doctor.mjs";

test("Git availability recognizes complete native distribution versions", () => {
  /** @type {[string,string][]} */
  const cases = [
    ["git version 2.51.1.windows.1\r\n", "2.51.1.windows.1"],
    ["git version 2.51.0 (Apple Git-181)\n", "2.51.0"],
    ["git version 2.51.1\n", "2.51.1"],
  ];
  for (const [output, expected] of cases) {
    assert.equal(reportedVersion("git", output), expected);
  }
  assert.equal(reportedVersion("git", "unrecognized 2.51.1"), undefined);
  assert.equal(reportedVersion("git", "git version 2.51.1unknown"), undefined);
});

test("pinned version probes reject substrings and retain complete numeric tokens", () => {
  assert.equal(reportedVersion("node", "v26.10.0\n"), "26.10.0");
  assert.equal(reportedVersion("docker", "Docker version 29.8.2, build synthetic"), "29.8.2");
  assert.equal(reportedVersion("node", "v26.10.0unknown"), undefined);
});
