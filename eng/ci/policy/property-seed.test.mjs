import assert from "node:assert/strict";
import test from "node:test";
import { selectPropertySeed } from "./property-seed.mjs";

const identity = { repository: "owner/project", workflow: "verification", runId: "1234" };

/** @param {string} runAttempt */
const scheduled = (runAttempt) =>
  selectPropertySeed({ ...identity, eventName: "schedule", requestedSeed: "", runAttempt });

test("scheduled seeds are deterministic and attempt-scoped", () => {
  assert.equal(scheduled("1"), scheduled("1"));
  assert.notEqual(scheduled("1"), scheduled("2"));
  assert.match(scheduled("1"), /^[0-9]+$/u);
});

test("scheduled seeds refuse incomplete or unbounded identity", () => {
  for (const repository of ["", " ", "x".repeat(257)]) {
    assert.throws(
      () => selectPropertySeed({ ...identity, repository, eventName: "schedule", runAttempt: "1" }),
      /identity/u,
    );
  }
});

test("a canonical manual seed is preserved, anything else is refused", () => {
  const manual = (/** @type {string} */ requestedSeed) =>
    selectPropertySeed({
      ...identity,
      eventName: "workflow_dispatch",
      requestedSeed,
      runAttempt: "1",
    });
  assert.equal(manual("18446744073709551615"), "18446744073709551615");
  assert.equal(manual("0"), "0");
  for (const bad of ["01", "", "-1", "1.5", " 1", "18446744073709551616", "0x10"]) {
    assert.throws(() => manual(bad), /canonical/u, bad);
  }
});
