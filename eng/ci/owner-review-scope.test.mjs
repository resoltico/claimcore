/** @typedef {import("./types.mjs").Json} Json */
import test from "node:test";
import assert from "node:assert/strict";
import { changedFiles, treeFiles, reviewScopes, digest } from "./owner-review-scope.mjs";
const sha = "a".repeat(40);
/** @param {string} path @param {string} mode @param {string} type */
const row = (path, mode = "100644", type = "blob") => ({
  path,
  mode,
  type,
  sha,
});
/** @param {...Json} rows */
const tree = (...rows) => ({ sha, truncated: false, tree: rows });

test("scope retains additions, deletions, renames-as-two-paths and executable-mode changes", () => {
  const before = treeFiles(tree(row("docs/architecture.md"), row("utility.sh")), sha);
  const after = treeFiles(tree(row("renamed.md"), row("utility.sh", "100755")), sha);
  const changes = changedFiles(before, after);
  assert.deepEqual(
    changes.map((entry) => [entry.path, entry.status]),
    [
      ["docs/architecture.md", "removed"],
      ["renamed.md", "added"],
      ["utility.sh", "modified"],
    ],
  );
  assert(changes.every((entry) => entry.ownerReviewRequired));
  assert(changes[0]?.["scopes"].includes("architecture"));
});
for (const path of [
  ".github/workflows/gate.yaml",
  "eng/ci/new-reporter.mjs",
  "eng/ClaimCore.Docs/contract-reviews.json",
  "tests/new-suite.fs",
  "web/scripts/check-bundle-size.mjs",
  "config/lint-exceptions.json",
]) {
  test(`review scope highlights policy change ${path}`, () => {
    assert(reviewScopes(path).includes("contract-policy"));
  });
}
test("unknown paths still require owner review and object kind changes remain visible", () => {
  assert.deepEqual(reviewScopes("new-area/unknown.txt"), ["general"]);
  const changes = changedFiles(
    treeFiles(tree(row("link")), sha),
    treeFiles(tree(row("link", "120000")), sha),
  );
  assert.equal(changes.length, 1);
  assert.equal(changes[0]?.["ownerReviewRequired"], true);
  assert(changes[0]?.["scopes"].includes("security"));
  const submodule = treeFiles(tree(row("vendor", "160000", "commit")), sha);
  assert.equal(submodule.get("vendor")?.type, "commit");
});
/** @type {Array<[string, (document: Json) => void]>} */
const refusals = [
  [
    "truncated",
    (/** @type {Json} */ d) => {
      d.truncated = true;
    },
  ],
  [
    "unattested completeness",
    (/** @type {Json} */ d) => {
      delete d.truncated;
    },
  ],
  [
    "wrong tree",
    (/** @type {Json} */ d) => {
      d.sha = "b".repeat(40);
    },
  ],
  [
    "duplicate paths",
    (/** @type {Json} */ d) => {
      d.tree.push({ ...d.tree[0] });
    },
  ],
  [
    "traversal",
    (/** @type {Json} */ d) => {
      d.tree[0].path = "../outside";
    },
  ],
  [
    "absolute path",
    (/** @type {Json} */ d) => {
      d.tree[0].path = "/outside";
    },
  ],
  [
    "control characters",
    (/** @type {Json} */ d) => {
      d.tree[0].path = "name\nlog";
    },
  ],
  [
    "bidirectional path",
    (/** @type {Json} */ d) => {
      d.tree[0].path = "name\u202Etxt";
    },
  ],
  [
    "invalid object",
    (/** @type {Json} */ d) => {
      d.tree[0].sha = "not-sha";
    },
  ],
  [
    "unknown kind",
    (/** @type {Json} */ d) => {
      d.tree[0].type = "toString";
    },
  ],
  [
    "mismatched mode",
    (/** @type {Json} */ d) => {
      d.tree[0].mode = "160000";
    },
  ],
];
for (const [label, change] of refusals) {
  test(`tree scope refuses ${label}`, () => {
    const value = tree(row("source"));
    change(value);
    assert.throws(() => treeFiles(value, sha));
  });
}
test("scope digest binds revisions and all changed modes", () => {
  assert.notEqual(digest({ base: sha, mode: "100644" }), digest({ base: sha, mode: "100755" }));
});
