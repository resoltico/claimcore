import assert from "node:assert/strict";
import { createHash } from "node:crypto";

export const isSha = (value) =>
  typeof value === "string" && /^[0-9a-f]{40}$/u.test(value);
export const digest = (value) =>
  createHash("sha256").update(JSON.stringify(value)).digest("hex");
const pathValid = (path) =>
  typeof path === "string" &&
  path.length > 0 &&
  path.length <= 4096 &&
  !/[\x00-\x1f\x7f\u202a-\u202e\u2066-\u2069\\]/u.test(path) &&
  path.split("/").every((part) => part && part !== "." && part !== "..");

export function treeFiles(document, expectedSha) {
  assert(
    isSha(expectedSha) && document.sha === expectedSha,
    "Review tree identity differs.",
  );
  assert(
    document.truncated === false && Array.isArray(document.tree),
    "Review tree is incomplete.",
  );
  assert(
    document.tree.length <= 100000,
    "Review tree exceeds the supported bound.",
  );
  const seen = new Set();
  const files = new Map();
  for (const entry of document.tree) {
    assert(
      pathValid(entry.path) && !seen.has(entry.path),
      "Invalid or duplicate review path.",
    );
    seen.add(entry.path);
    assert(isSha(entry.sha), "Missing review object identity.");
    const modes = {
      tree: ["040000"],
      blob: ["100644", "100755", "120000"],
      commit: ["160000"],
    };
    assert(
      Object.hasOwn(modes, entry.type) &&
        modes[entry.type].includes(entry.mode),
      "Unknown review object mode.",
    );
    if (entry.type !== "tree")
      files.set(entry.path, {
        type: entry.type,
        mode: entry.mode,
        sha: entry.sha,
      });
  }
  return files;
}

export function reviewScopes(path) {
  const scopes = ["general"];
  if (
    /^(src\/|architecture\.json$|Directory\.|ClaimCore\.slnx$)/u.test(path) ||
    /\.(?:fsproj|props|targets)$/u.test(path) ||
    path === "docs/architecture.md"
  )
    scopes.push("architecture");
  if (
    !path.includes("/") ||
    /^(?:\.github\/|eng\/|db\/|src\/|web\/src\/(?:api|hooks)\/|SECURITY\.md$)/u.test(
      path,
    ) ||
    /(?:package|packages|lock|vulnerability|dependenc|suppression|NuGet|Docker|compose)/iu.test(
      path,
    )
  )
    scopes.push("security");
  if (
    !path.includes("/") ||
    /^(?:\.github\/|eng\/|tests\/|db\/|docs\/|src\/|web\/(?:tests|e2e|scripts|src\/generated)\/)/u.test(
      path,
    ) ||
    /(?:AGENTS|CONTRIBUTING|architecture|baseline|manifest|suppression|vulnerability|dependenc|\.config|lint|coverage|vitest|playwright|tsconfig|package)/iu.test(
      path,
    )
  )
    scopes.push("contract-policy");
  return scopes;
}

export function changedFiles(before, after) {
  const changes = [];
  for (const path of [...new Set([...before.keys(), ...after.keys()])].sort()) {
    const old = before.get(path) ?? null;
    const current = after.get(path) ?? null;
    if (JSON.stringify(old) !== JSON.stringify(current))
      changes.push({
        path,
        status:
          old === null ? "added" : current === null ? "removed" : "modified",
        before: old,
        after: current,
        scopes: [
          ...new Set([
            ...reviewScopes(path),
            ...([old, current].some(
              (entry) => entry?.type === "commit" || entry?.mode === "120000",
            )
              ? ["security", "contract-policy"]
              : []),
          ]),
        ],
        ownerReviewRequired: true,
      });
  }
  return changes;
}

export const reviewQuestions = {
  general:
    "Review the full diff and intended behavior; unknown paths are not exemptions.",
  architecture:
    "Verify ownership, dependency edges and invariants against implementation, not only the manifest.",
  security:
    "Review credentials, admission, privacy, durable authority, dependencies and publication permissions.",
  "contract-policy":
    "Review semantics, encodings, assertions, scenarios, exclusions, thresholds, gates and the review tooling itself.",
};
