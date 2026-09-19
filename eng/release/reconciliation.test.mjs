import assert from "node:assert/strict";
import { test } from "node:test";
import { releaseClaimCore } from "./publisher.mjs";
import { createFixture, sha, tag } from "./test-support.mjs";

test("blocks when CI is rerun during Gate verification", async () => {
  const fixture = createFixture();
  const api = async (path, options) => {
    const result = await fixture.api(path, options);
    if (path.includes("/jobs?")) fixture.state.runs[0].run_attempt += 1;
    return result;
  };
  await assert.rejects(releaseClaimCore({ ...fixture.options, api, publish: true }));
  assert.equal(fixture.writes().length, 0);
});

test("leaves a draft alone when the tag changes after draft creation", async () => {
  const fixture = createFixture();
  const api = async (path, options) => {
    const result = await fixture.api(path, options);
    if (options?.method === "POST") fixture.state.tagSha = "4".repeat(40);
    return result;
  };
  await assert.rejects(releaseClaimCore({ ...fixture.options, api, publish: true }));
  assert.equal(fixture.state.release.draft, true);
  assert.deepEqual(fixture.writes().map((call) => call.method), ["POST"]);
});

for (const operation of ["Post", "Patch"]) {
  test(`reconciles a lost ${operation.toUpperCase()} response on a later invocation`, async () => {
    const fixture = createFixture();
    fixture.state[`lose${operation}`] = true;
    await assert.rejects(releaseClaimCore({ ...fixture.options, publish: true }), /Lost/u);
    await releaseClaimCore({ ...fixture.options, publish: true });
    assert.equal(fixture.state.release.draft, false);
    assert.deepEqual(fixture.writes().map((call) => call.method), ["POST", "PATCH"]);
  });
}

test("reports a post-publication edit without rollback or silent repair", async () => {
  const fixture = createFixture();
  const api = async (path, options) => {
    const result = await fixture.api(path, options);
    if (options?.method === "PATCH") fixture.state.release.body += "\nUnexpected change.";
    return result;
  };
  await assert.rejects(releaseClaimCore({ ...fixture.options, api, publish: true }), /body differs/u);
  assert.equal(fixture.state.release.draft, false);
});

test("refuses a workflow result set that GitHub would truncate", async () => {
  const fixture = createFixture();
  fixture.state.runs = Array.from({ length: 1001 }, (_, index) => ({ ...fixture.state.runs[0], id: index + 1 }));
  await assert.rejects(releaseClaimCore({ ...fixture.options, publish: true }), /result limit/u);
  assert.equal(fixture.writes().length, 0);
});

test("does not accept a branch masquerading as the release tag", async () => {
  const fixture = createFixture();
  fixture.state.runs[0].referenced_workflows[0].ref = `refs/heads/${tag}`;
  await assert.rejects(releaseClaimCore({ ...fixture.options, publish: true }));
  assert.equal(fixture.writes().length, 0);
});

test("rejects malformed publisher inputs before making an API call", async () => {
  const fixture = createFixture();
  for (const input of [
    { tag: "v0.3.0-rc.1" },
    { tag: "v0.3.0\n" },
    { expectedSha: `${sha}\n` },
    { expectedSha: "A".repeat(40) },
    { repository: "owner/.." },
  ]) {
    await assert.rejects(releaseClaimCore({ ...fixture.options, ...input, publish: true }));
  }
  assert.equal(fixture.state.calls.length, 0);
});
