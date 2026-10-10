// Standalone coordinated commands create the same isolated run used by run-local.
import assert from "node:assert/strict";
import { copyFileSync, mkdirSync, realpathSync } from "node:fs";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { artifactDirectory } from "./artifact-path.mjs";
import { contextEnvironment, createRun, runContext } from "./run-context.mjs";
import { retainPublication } from "./run-publication.mjs";
import { finishRun } from "./run-evidence.mjs";
import { regularFiles, assertNoLinkAbove } from "./scan/files.mjs";
import { option, runToLog } from "./process-support.mjs";
import { verifyPublished } from "./publish/main.mjs";
import { jobContext } from "./job-context.mjs";

/** @param {string} root */
export function participating(root) {
  assert.ok(
    !(process.env["CLAIMCORE_RUN_CONTEXT"] && process.env["CLAIMCORE_JOB_CONTEXT"]),
    "A command cannot join two ownership contexts.",
  );
  if (runContext(root) !== null) {
    return true;
  }
  return jobContext(root) !== null;
}
/** @param {import("./run-context.mjs").RunContext} context @param {string[]} args */
function admitBrowserPublication(context, args) {
  const [web, database] = args;
  assert.ok(web !== undefined && database !== undefined);
  const publication = realpathSync(dirname(web));
  assert.equal(realpathSync(dirname(database)), publication);
  assert.equal(realpathSync(web), join(publication, "web"));
  assert.equal(realpathSync(database), join(publication, "database"));
  assertNoLinkAbove(publication);
  verifyPublished(publication, ["web", "database"]);
  const destination = artifactDirectory(context.source, "artifacts/admitted-publication");
  mkdirSync(destination, { mode: 0o700 });
  for (const file of regularFiles(publication)) {
    const target = join(destination, relative(publication, file));
    mkdirSync(dirname(target), { recursive: true, mode: 0o700 });
    copyFileSync(file, target);
  }
  return [join(destination, "web"), join(destination, "database"), ...args.slice(2)];
}
/** @param {string[]} args */
function setupCommands(args) {
  const [entry] = args;
  const frontend =
    entry === "eng/ci/publish/main.mjs" ||
    entry === "eng/ci/deployment/qualify.mjs" ||
    entry === "eng/Run-PublishedWebE2E.sh" ||
    (entry === "eng/ci/run-stages.mjs" && args[1] === "frontend");
  const commands = [["node", "eng/ci/source-build.mjs"]];
  if (frontend) {
    commands.push(["node", "eng/ci/run-stages.mjs", "frontend-product"]);
  }
  if (entry === "eng/Run-PublishedWebE2E.sh" || entry === "eng/ci/deployment/qualify.mjs") {
    const engine = entry === "eng/ci/deployment/qualify.mjs" ? "chromium" : (args[3] ?? "all");
    assert.ok(["all", "chromium", "firefox", "webkit"].includes(engine));
    commands.push([
      "npm",
      "--prefix",
      "web",
      "exec",
      "--",
      "playwright",
      "install",
      ...(engine === "all" ? ["chromium", "firefox", "webkit"] : [engine]),
    ]);
  }
  return commands;
}
/** @param {string[][]} commands @param {{cwd:string,env:NodeJS.ProcessEnv,groups:number[]}} options @param {string} logs @param {Record<string,string>} stages */
async function setupRun(commands, options, logs, stages) {
  for (const [index, [command, ...args]] of commands.entries()) {
    assert.ok(command);
    const status = await runToLog(command, args, {
      ...options,
      log: join(logs, `setup-${index}.log`),
      onOutcome: ({ exit, captureFailed }) => {
        stages[`setup-${index}`] = `exit ${exit}; captureFailed=${captureFailed}`;
      },
    });
    if (status !== 0) {
      return status;
    }
  }
  return 0;
}
/** @param {string} root @param {string[]} args */
function standalonePublicationOutput(root, args) {
  const publicationOutput =
    args[0] === "eng/ci/publish/main.mjs" ? option(args, "output", "artifacts/publish") : null;
  if (publicationOutput !== null) {
    assert.ok(
      !isAbsolute(publicationOutput),
      "Standalone publication outputs must use a relative artifacts path.",
    );
    artifactDirectory(root, publicationOutput);
  }
  return publicationOutput;
}
/** @param {string} root @param {string} command @param {string[]} args */
export async function coordinate(root, command, args) {
  if (participating(root)) {
    return false;
  }
  assert.ok(
    (command === "node" &&
      [
        "eng/ci/suites/suite.mjs",
        "eng/ci/run-stages.mjs",
        "eng/ci/publish/main.mjs",
        "eng/ci/deployment/qualify.mjs",
      ].includes(args[0] ?? "")) ||
      (command === "bash" &&
        [
          "eng/Run-PublishedCliAcceptance.sh",
          "eng/Run-PublishedWebE2E.sh",
          "eng/Run-LocalBrowserCoverage.sh",
        ].includes(args[0] ?? "")),
    "Only declared coordinated entry points can create a run.",
  );
  const publicationOutput = standalonePublicationOutput(root, args);
  const context = await createRun(root);
  const logs = artifactDirectory(context.source, "artifacts/local-ci");
  mkdirSync(logs, { recursive: true, mode: 0o700 });
  /** @type {number[]} */
  const groups = [];
  const options = { cwd: context.source, env: contextEnvironment(context), groups };
  console.log(`Standalone run ${context.id}; scratch ${context.scratch}.`);
  if (args[0] === "eng/Run-PublishedWebE2E.sh") {
    args = [args[0], ...admitBrowserPublication(context, args.slice(1))];
  }
  await executeInRun(context, command, args, options, { logs, publicationOutput });
  return true;
}
/** @param {import("./run-context.mjs").RunContext} context @param {string} command @param {string[]} args
 * @param {{cwd:string,env:NodeJS.ProcessEnv,groups:number[]}} options @param {{logs:string,publicationOutput:string|null}} paths */
async function executeInRun(context, command, args, options, { logs, publicationOutput }) {
  const { groups } = options;
  let status;
  /** @type {Record<string,string>} */
  const stages = {};
  try {
    status = await setupRun(setupCommands(args), options, logs, stages);
    stages.setup = `exit ${status}`;
    if (status === 0) {
      status = await runToLog(command, args, {
        ...options,
        log: join(logs, "command.log"),
        onOutcome: ({ exit, captureFailed }) => {
          stages["command-execution"] = `exit ${exit}; captureFailed=${captureFailed}`;
        },
      });
      stages.command = `exit ${status}`;
    }
    if (status === 0 && publicationOutput !== null) {
      await retainPublication(context, publicationOutput);
    }
  } catch {
    stages.coordination = "refused";
    status = 1;
  }
  process.exitCode = status;
  try {
    await finishRun(context, { passed: status === 0, groups, stages });
  } catch {
    process.exitCode = 1;
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const root = resolve(import.meta.dirname, "../..");
  const [command, ...args] = process.argv.slice(2);
  assert.ok(command);
  if (command === "check") {
    assert.ok(participating(root), "A valid participating run context is required.");
  } else {
    await coordinate(root, command, args);
  }
}
