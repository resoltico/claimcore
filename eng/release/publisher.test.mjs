import assert from "node:assert/strict";
import { test } from "node:test";
import { releaseClaimCore } from "./publisher.mjs";
import { body, createFixture, sha, tag, title } from "./test-support.mjs";

test("preview validates the exact release plan without GitHub writes", async () => {
  const fixture = createFixture();
  const result = await releaseClaimCore(fixture.options);
  assert.equal(result.status, "validated");
  assert.equal(result.body, body);
  assert.deepEqual(fixture.writes(), []);
});

test("publication creates a draft then publishes without rewriting release text", async () => {
  const fixture = createFixture();
  const result = await releaseClaimCore({ ...fixture.options, publish: true });
  assert.equal(result.status, "published");
  assert.equal(fixture.state.release.body, body);
  assert.deepEqual(fixture.writes().map((call) => call.method), ["POST", "PATCH"]);
  assert.deepEqual(fixture.writes()[1].json, { draft: false, make_latest: "legacy" });
  assert.equal(fixture.writes()[0].json.generate_release_notes, false);
  assert.equal(fixture.writes()[0].json.target_commitish, sha);
});

test("a matching published release is a no-op even after CI retention expires", async () => {
  const fixture = createFixture();
  fixture.state.release = fixture.release(false);
  fixture.state.runs = [];
  const result = await releaseClaimCore({ ...fixture.options, publish: true });
  assert.equal(result.status, "already-published");
  assert.equal(fixture.writes().length, 0);
});

test("a matching draft on a later release page resumes without a second draft", async () => {
  const fixture = createFixture();
  fixture.state.otherReleases = Array.from({ length: 100 }, (_, index) => ({ tag_name: `other-${index}` }));
  fixture.state.release = fixture.release(true);
  await releaseClaimCore({ ...fixture.options, publish: true });
  assert.deepEqual(fixture.writes().map((call) => call.method), ["PATCH"]);
  assert(fixture.state.calls.some((call) => call.path === "releases?per_page=100&page=2"));
});

for (const [name, change] of [
  ["body", (release) => { release.body += "\nExtra prose."; }],
  ["title", (release) => { release.name = "Another title"; }],
  ["prerelease", (release) => { release.prerelease = true; }],
  ["assets", (release) => { release.assets = [{ id: 123 }]; }],
]) {
  test(`never overwrites a mismatched ${name}`, async () => {
    const fixture = createFixture();
    fixture.state.release = fixture.release(true);
    change(fixture.state.release);
    await assert.rejects(releaseClaimCore({ ...fixture.options, publish: true }));
    assert.equal(fixture.writes().length, 0);
  });
}

for (const [name, change] of [
  ["a lightweight tag", (state) => { state.annotated = false; }],
  ["a tag at another commit", (state) => { state.target = "4".repeat(40); }],
  ["a release commit outside main", (state) => { state.mergeBase = "4".repeat(40); }],
  ["a version mismatch", (state) => { state.props = state.props.replace("0.3.0", "0.4.0"); }],
  ["missing tag CI", (state) => { state.runs = []; }],
  ["a failed Gate", (state) => { state.jobs[0].conclusion = "failure"; }],
  ["a skipped Gate", (state) => { state.jobs[0].conclusion = "skipped"; }],
]) {
  test(`blocks publication for ${name}`, async () => {
    const fixture = createFixture();
    change(fixture.state);
    await assert.rejects(releaseClaimCore({ ...fixture.options, publish: true }));
    assert.equal(fixture.writes().length, 0);
  });
}

test("refuses a newest failed tag run rather than selecting an older success", async () => {
  const fixture = createFixture();
  fixture.state.runs.push({ ...fixture.state.runs[0], id: 8, created_at: "2026-09-19T01:00:00Z", conclusion: "failure" });
  await assert.rejects(releaseClaimCore({ ...fixture.options, publish: true }));
  assert.equal(fixture.writes().length, 0);
});

test("uses one Gate from the current workflow attempt", async () => {
  const fixture = createFixture();
  fixture.state.runs[0].run_attempt = 2;
  fixture.state.jobs.unshift(...Array.from({ length: 100 }, (_, index) => ({ name: `Other ${index}` })));
  await releaseClaimCore(fixture.options);
  assert(fixture.state.calls.some((call) => call.path.includes("/attempts/2/jobs?per_page=100&page=2")));
});

test("retains the standard title rather than accepting invented wording", async () => {
  const fixture = createFixture();
  const result = await releaseClaimCore(fixture.options);
  assert.equal(result.title, title);
  assert.equal(result.tag, tag);
});
