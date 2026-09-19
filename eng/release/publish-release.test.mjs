import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { repository } from "./test-support.mjs";

test("the CLI rejects a non-main GitHub dispatch before network access", () => {
  const result = spawnSync(
    process.execPath,
    [fileURLToPath(new URL("./publish-release.mjs", import.meta.url)), "--publish"],
    {
      env: {
        ...process.env,
        GITHUB_ACTIONS: "true",
        GITHUB_EVENT_NAME: "workflow_dispatch",
        GITHUB_REF: "refs/heads/other",
        GITHUB_REPOSITORY: repository,
        GH_TOKEN: "not-a-real-token",
      },
      encoding: "utf8",
    },
  );
  assert.equal(result.status, 1);
  assert.match(result.stderr, /manually dispatched from main/u);
  assert(!result.stderr.includes("not-a-real-token"));
});
