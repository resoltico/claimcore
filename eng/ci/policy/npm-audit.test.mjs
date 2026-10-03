import assert from "node:assert/strict";
import test from "node:test";
import { assess } from "./npm-audit.mjs";

const clean = { metadata: { vulnerabilities: { total: 0 } }, vulnerabilities: {} };

/** @param {Record<string, {via: Array<string | {name: string, url: string}>}>} entries */
const report = (entries) => ({
  metadata: { vulnerabilities: { total: Object.keys(entries).length } },
  vulnerabilities: entries,
});

const approved = report({
  braces: {
    via: [{ name: "braces", url: "https://github.com/advisories/GHSA-vfj7-8cjw-p6xm" }],
  },
  micromatch: { via: ["braces"] },
  "http-cache-semantics": {
    via: [
      {
        name: "http-cache-semantics",
        url: "https://github.com/advisories/GHSA-ch52-4w7c-c8xp",
      },
    ],
  },
  "make-fetch-happen": { via: ["http-cache-semantics"] },
});

test("only the two reviewed development advisory roots pass", () => {
  assert.equal(assess(approved, clean, Date.parse("2026-10-03T00:00:00Z")), 2);
});

test("production exposure and a new indirect advisory fail closed", () => {
  const { braces } = approved.vulnerabilities;
  assert(braces);
  assert.throws(() => assess(approved, report({ braces })), /Production/u);
  const extra = structuredClone(approved);
  const { micromatch } = extra.vulnerabilities;
  assert(micromatch);
  micromatch.via.push({
    name: "micromatch",
    url: "https://github.com/advisories/GHSA-new-unreviewed",
  });
  assert.throws(() => assess(extra, clean), /unreviewed/u);
});

test("missing, malformed, cyclic, and expired exceptions fail closed", () => {
  const stale = structuredClone(approved);
  delete stale.vulnerabilities.braces;
  stale.metadata.vulnerabilities.total -= 1;
  delete stale.vulnerabilities.micromatch;
  stale.metadata.vulnerabilities.total -= 1;
  assert.throws(() => assess(stale, clean), /stale/u);
  assert.throws(() => assess({ error: "unavailable" }, clean), /invalid/u);
  const cyclic = structuredClone(approved);
  const { micromatch } = cyclic.vulnerabilities;
  assert(micromatch);
  micromatch.via = ["micromatch"];
  assert.throws(() => assess(cyclic, clean), /cyclic/u);
  assert.throws(() => assess(approved, clean, Date.parse("2026-11-03T00:00:00Z")), /review/u);
});
