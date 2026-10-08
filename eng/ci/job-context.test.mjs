import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  unlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { createJobContext, jobContext } from "./job-context.mjs";
import { participating } from "./run-command.mjs";
import { gitEnvironment } from "./scan/process.mjs";
import { parseWorkflow } from "./yaml.mjs";

/** @param {(root:string,git:(args:string[])=>string)=>void} body */
function fixture(body) {
  const root = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-job-context-"));
  /** @param {string[]} args */
  const git = (args) =>
    execFileSync("git", args, {
      cwd: root,
      env: gitEnvironment(),
      encoding: "utf8",
      stdio: "pipe",
    });
  try {
    mkdirSync(join(root, "config"));
    writeFileSync(join(root, ".gitignore"), "artifacts/\n");
    writeFileSync(join(root, "source.txt"), "synthetic source\n");
    writeFileSync(
      join(root, "config/publication-inputs.json"),
      JSON.stringify({
        files: ["source.txt"],
        directories: [],
        excludedDirectories: [],
        extensions: [],
      }),
    );
    git(["init", "-b", "main"]);
    git(["config", "commit.gpgsign", "false"]);
    git(["config", "core.hooksPath", join(root, ".git/owned-empty-hooks")]);
    git(["add", "."]);
    git([
      "-c",
      "user.name=Synthetic",
      "-c",
      "user.email=synthetic@example.invalid",
      "commit",
      "-m",
      "synthetic",
    ]);
    body(root, git);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("explicit job ownership binds exact source and complete live Git graph", () => {
  fixture((root, git) => {
    const path = createJobContext(root);
    assert.equal(jobContext(root, path)?.history.head, git(["rev-parse", "HEAD"]).trim());
    assert.throws(() => createJobContext(root));
    writeFileSync(join(root, "source.txt"), "changed\n");
    assert.throws(() => jobContext(root, path));
    writeFileSync(join(root, "source.txt"), "synthetic source\n");
    git(["branch", "changed-ref"]);
    assert.throws(() => jobContext(root, path));
    git(["branch", "-D", "changed-ref"]);
    assert.ok(jobContext(root, path));
    mkdirSync(join(root, "nested"));
    assert.throws(() => createJobContext(join(root, "nested")));
    writeFileSync(join(root, ".git/shallow"), `${git(["rev-parse", "HEAD"]).trim()}\n`);
    assert.throws(() => jobContext(root, path));
  });
});

test("forged environment does not admit local or Gitless source; explicit records refuse links and forgery", () => {
  const names = [
    "GITHUB_ACTIONS",
    "GITHUB_WORKSPACE",
    "CLAIMCORE_RUN_CONTEXT",
    "CLAIMCORE_JOB_CONTEXT",
  ];
  const original = names.map((name) => process.env[name]);
  try {
    delete process.env.CLAIMCORE_RUN_CONTEXT;
    delete process.env.CLAIMCORE_JOB_CONTEXT;
    fixture((root) => {
      process.env.GITHUB_ACTIONS = "true";
      process.env.GITHUB_WORKSPACE = root;
      assert.equal(participating(root), false);
      const path = createJobContext(root);
      process.env.CLAIMCORE_JOB_CONTEXT = path;
      assert.equal(participating(root), true);
      process.env.CLAIMCORE_RUN_CONTEXT = "forged";
      assert.throws(() => participating(root));
      delete process.env.CLAIMCORE_RUN_CONTEXT;
      const bytes = readFileSync(path, "utf8");
      writeFileSync(
        path,
        JSON.stringify({ ...JSON.parse(bytes), sourceSha256: "private sentinel" }),
      );
      assert.throws(
        () => jobContext(root, path),
        (error) => error instanceof Error && !error.message.includes("private sentinel"),
      );
      unlinkSync(path);
      writeFileSync(join(root, "artifacts/other.json"), bytes, { mode: 0o600 });
      symlinkSync(join(root, "artifacts/other.json"), path);
      assert.throws(() => jobContext(root, path));
      delete process.env.CLAIMCORE_JOB_CONTEXT;
      rmSync(join(root, ".git"), { recursive: true });
      assert.equal(participating(root), false);
      assert.throws(() => createJobContext(root));
    });
  } finally {
    names.forEach((name, index) => {
      if (original[index] === undefined) {
        delete process.env[name];
      } else {
        process.env[name] = original[index];
      }
    });
  }
});

test("coordinated verification jobs explicitly admit and recheck full-history source before upload", () => {
  const workflows = [
    "acceptance",
    "browser",
    "coverage",
    "deployment",
    "documentation",
    "frontend",
    "frontend-product",
    "integration",
    "properties",
    "publish",
    "quality",
    "suites",
  ];
  for (const name of workflows) {
    const workflow = parseWorkflow(
      readFileSync(new URL(`../../.github/workflows/verify-${name}.yml`, import.meta.url), "utf8"),
    ).value;
    for (const [id, job] of Object.entries(workflow.jobs)) {
      if (name === "suites" && id !== "suites") {
        continue;
      }
      /** @type {{steps:import("./types.mjs").Json[]}} */
      const { steps } = job;
      const create = steps.findIndex(
        (step) => step.run === 'node eng/ci/job-context.mjs create >> "$GITHUB_ENV"',
      );
      assert.ok(create >= 0, `${name}/${id}: explicit admission`);
      assert.equal(steps[create]?.shell, "bash");
      assert.equal(steps.filter((step) => step.run?.includes("job-context.mjs create")).length, 1);
      const checkout = steps.findIndex((step) => step.uses?.startsWith("actions/checkout@"));
      assert.ok(checkout < create);
      const checkoutStep = steps[checkout];
      assert.ok(checkoutStep);
      assert.equal(checkoutStep.with["fetch-depth"], 0);
      const check = steps.findIndex((step) => step.run === "node eng/ci/job-context.mjs check");
      assert.ok(check > create);
      const checkStep = steps[check];
      assert.ok(checkStep);
      assert.equal(checkStep.if, "always()");
      for (const step of steps.filter((candidate) =>
        candidate.uses?.startsWith("actions/upload-artifact@"),
      )) {
        assert.ok(step.if?.includes("steps.job_checkout_current.outcome == 'success'"));
        assert.ok(check < steps.indexOf(step));
      }
    }
  }
});
