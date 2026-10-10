import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { assertNoLinkAbove } from "./files.mjs";
import { gitEnvironment, runChild, scannerEnvironment } from "./process.mjs";

/** @param {string} root @param {string[]} args */
function git(root, args) {
  const result = spawnSync("git", ["--no-replace-objects", ...args], {
    cwd: root,
    env: gitEnvironment(),
    encoding: "utf8",
    maxBuffer: 64 * 1024 * 1024,
  });
  if (result.status !== 0) {
    throw new Error("Complete Git history could not be admitted.");
  }
  return result.stdout.trim();
}

/** @param {string} text @param {boolean} [paths] */
function objectIds(text, paths = false) {
  const ids = text
    .split("\n")
    .filter(Boolean)
    .map((line) => (paths ? (line.split(" ", 1)[0] ?? "") : line));
  if (ids.some((id) => !id || !/^(?:[0-9a-f]{40}|[0-9a-f]{64})$/u.test(id))) {
    throw new Error("Git history contains an invalid object identity.");
  }
  return [...new Set(ids)].sort();
}

/** Mutable alias labels are diagnostic metadata, not qualification identity.
 * @param {ReturnType<typeof observeHistory>} history */
export function historyIdentity(history) {
  const { refsSha256: diagnostic, ...identity } = history;
  void diagnostic;
  return identity;
}

/** @param {string} directory */
export function observeHistory(directory) {
  const root = realpathSync(resolve(directory));
  assertNoLinkAbove(root);
  const top = git(root, ["rev-parse", "--show-toplevel"]);
  if (realpathSync(top) !== root) {
    throw new Error("History requires its exact originating Git worktree.");
  }
  if (git(root, ["rev-parse", "--is-shallow-repository"]) !== "false") {
    throw new Error("Complete history refuses a shallow Git repository.");
  }
  const common = realpathSync(
    git(root, ["rev-parse", "--path-format=absolute", "--git-common-dir"]),
  );
  if (existsSync(join(common, "info/grafts")) || existsSync(join(common, "shallow"))) {
    throw new Error("Complete history refuses grafted or incomplete Git views.");
  }
  const partial = spawnSync("git", ["config", "--local", "--get", "extensions.partialclone"], {
    cwd: root,
    env: gitEnvironment(),
    encoding: "utf8",
    maxBuffer: 64 * 1024 * 1024,
  });
  if (partial.status !== 1) {
    throw new Error("Complete history refuses partial clone views.");
  }
  const head = git(root, ["rev-parse", "--verify", "HEAD^{commit}"]);
  const symbolic = spawnSync("git", ["symbolic-ref", "--quiet", "HEAD"], {
    cwd: root,
    env: gitEnvironment(),
    encoding: "utf8",
    maxBuffer: 64 * 1024 * 1024,
  });
  if (symbolic.status !== 0 && symbolic.status !== 1) {
    throw new Error("Git reference observation failed.");
  }
  const refs = git(root, ["for-each-ref", "--format=%(refname) %(objectname)"]);
  const { objects, commits, roots } = reachableGraph(root);
  return {
    format: 2,
    root,
    common,
    head,
    ref: symbolic.status === 0 ? symbolic.stdout.trim() : null,
    roots,
    commitsSha256: createHash("sha256").update(commits.join("\n")).digest("hex"),
    refsSha256: createHash("sha256").update(refs).digest("hex"),
    objectsSha256: createHash("sha256").update(objects.join("\n")).digest("hex"),
    commits: commits.length,
  };
}

/** @param {string} root */
function reachableGraph(root) {
  const objects = objectIds(
    git(root, ["rev-list", "--all", "HEAD", "--objects", "--missing=error"]),
    true,
  );
  const commits = objectIds(git(root, ["rev-list", "--all", "HEAD"]));
  const ancestry = git(root, ["rev-list", "--all", "HEAD", "--parents"]);
  const parents = new Set(
    ancestry
      .split("\n")
      .flatMap((line) =>
        objectIds(line.replaceAll(" ", "\n")).filter((id) => id !== line.split(" ", 1)[0]),
      ),
  );
  const roots = commits.filter((id) => !parents.has(id));
  return { objects, commits, roots };
}

/** @param {string} configuration @param {string} ignore @param {string} root @param {string[]} roots */
function historyArguments(configuration, ignore, root, roots) {
  return [
    "git",
    "--redact=100",
    "--no-banner",
    "--no-color",
    "--exit-code",
    "1",
    "--ignore-gitleaks-allow",
    "--config",
    configuration,
    "--gitleaks-ignore-path",
    ignore,
    `--log-opts=${roots.join(" ")} --full-history --diff-merges=separate --text --no-ext-diff --no-textconv`,
    root,
  ];
}
/** @param {{root:string,gitleaks:string,stdout?:(text:string)=>void,stderr?:(text:string)=>void}} options */
export async function scanHistory({
  root,
  gitleaks,
  stdout = (s) => process.stdout.write(s),
  stderr = (s) => process.stderr.write(s),
}) {
  const scratch = realpathSync(
    mkdtempSync(join(realpathSync(tmpdir()), "claimcore-history-scan-")),
  );
  try {
    const before = await observeHistory(root);
    const configuration = join(scratch, "default-rules.toml");
    const ignore = join(scratch, "empty.gitleaksignore");
    writeFileSync(configuration, "[extend]\nuseDefault = true\n", { mode: 0o600 });
    writeFileSync(ignore, "", { mode: 0o600 });
    const result = await runChild(
      gitleaks,
      historyArguments(configuration, ignore, before.root, before.roots),
      {
        cwd: scratch,
        env: scannerEnvironment(gitEnvironment()),
      },
    );
    const after = await observeHistory(root);
    if (JSON.stringify(historyIdentity(before)) !== JSON.stringify(historyIdentity(after))) {
      throw new Error("Git history changed during qualification.");
    }
    if (result.status !== 0 || result.overflow) {
      stderr("Complete history secret scan refused its inputs.\n");
      return 1;
    }
    stdout(`Complete history secret scan passed for ${before.commits} reachable commits.\n`);
    return 0;
  } catch {
    stderr("Complete history secret scan unavailable or Git admission changed.\n");
    return 2;
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}
