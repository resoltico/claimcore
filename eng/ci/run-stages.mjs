// Runs a registered stage plan (eng/ci/stage-plans/<plan>.json) with bounded concurrency and records,
// for every stage, the same log, evidence manifest and diagnostic that a serial run records.
//
//   node eng/ci/run-stages.mjs <plan> [--parallel N] [--run-id ID] [--attempt N] [--only id,id]
//
// CI and local runs use this one entry point, so a stage cannot pass locally under a different
// command than the one that gates the merge. Without a run id the run is local.
import { spawn, spawnSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import {
  closeSync,
  existsSync,
  mkdirSync,
  openSync,
  readFileSync,
  readdirSync,
} from "node:fs";
import { delimiter } from "node:path";
import { availableParallelism, tmpdir } from "node:os";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { runPlan, validatePlan } from "./stage-plan.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
const docs = join(
  root,
  "artifacts/bin/ClaimCore.Docs/release/ClaimCore.Docs.dll",
);

function option(name, fallback) {
  const at = process.argv.indexOf(`--${name}`);
  return at >= 0 && process.argv[at + 1] !== undefined
    ? process.argv[at + 1]
    : fallback;
}

function trackedMatches(directories, pattern) {
  const found = [];
  const walk = (directory) => {
    for (const entry of readdirSync(join(root, directory), {
      withFileTypes: true,
    })) {
      const path = `${directory}/${entry.name}`;
      if (entry.isDirectory()) walk(path);
      else if (pattern.test(entry.name)) found.push(path);
    }
  };
  for (const directory of directories) walk(directory);
  return found.sort();
}

function onPath(tool) {
  return (process.env.PATH ?? "")
    .split(delimiter)
    .some((directory) => directory !== "" && existsSync(join(directory, tool)));
}

function commandFor(stage, runId) {
  const substitute = (part) =>
    part.replaceAll("{runId}", runId).replaceAll("{root}", root);
  const argv = stage.argv.map(substitute);
  if (stage.appendFiles) {
    const { directories, suffix } = stage.appendFiles;
    argv.push(
      ...trackedMatches(
        directories,
        new RegExp(`${suffix.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`),
      ),
    );
  }
  return argv;
}

// A failing stage may opt in to showing the end of its own log. Only stages whose output is tool
// output about public artifacts opt in; the log is otherwise kept private (see stage-diagnostics).
function echoTail(id, log, lines) {
  const text = readFileSync(log, "utf8")
    .replace(/\u001b\[[0-9;]*m/g, "")
    .split("\n")
    .filter((line) => line.trim() !== "")
    .slice(-lines)
    .map((line) => line.slice(0, 240));
  console.log(`${id}: last ${text.length} log lines`);
  for (const line of text) console.log(`  ${line}`);
}

const timestamp = () => new Date().toISOString().replace("Z", "0000+00:00");

// Evidence manifests are registered for Linux producers; other platforms run and report only.
const recordEvidence = () =>
  option("evidence", process.platform === "linux" ? "yes" : "no") === "yes";

function execute(stage, plan, runId, attempt) {
  const missing = (stage.requires ?? []).filter((tool) => !onPath(tool));
  if (missing.length > 0) {
    // CI must never skip a gate; a developer machine without the tool cannot run it.
    console.log(
      `${stage.id}: ${process.env.CI ? "FAILED" : "skipped"} (needs ${missing.join(", ")} on PATH).`,
    );
    return Promise.resolve({ failed: Boolean(process.env.CI), skipped: true });
  }
  const output = join(root, stage.output ?? `artifacts/stages/${stage.id}`);
  if (existsSync(output))
    return Promise.resolve({ failed: true, note: "output already exists" });
  mkdirSync(output, { recursive: true });
  const log = join(
    process.env.RUNNER_TEMP ?? tmpdir(),
    `claimcore-${stage.id}.log`,
  );
  const descriptor = openSync(log, "w");
  const [command, ...args] = commandFor(stage, runId);
  const started = timestamp();
  return new Promise((resolve) => {
    const child = spawn(command, args, {
      cwd: root,
      stdio: ["ignore", descriptor, descriptor],
      env: {
        ...process.env,
        ...Object.fromEntries(
          Object.entries(stage.env ?? {}).map(([key, value]) => [
            key,
            value.replaceAll("{runId}", runId),
          ]),
        ),
      },
    });
    const settle = (status) => {
      closeSync(descriptor);
      const finished = timestamp();
      const outcome = status === 0 ? "success" : "failure";
      const manifest = recordEvidence()
        ? spawnSync(
            "dotnet",
            [
              docs,
              "stage-manifest",
              stage.id,
              runId,
              attempt,
              outcome,
              started,
              finished,
              relative(root, output),
            ],
            { cwd: root, stdio: "inherit" },
          )
        : { status: 0 };
      if (status !== 0 && stage.echoTail)
        echoTail(stage.id, log, stage.echoTail);
      const report = spawnSync(
        "node",
        [
          join(root, "eng/ci/stage-report.mjs"),
          plan.producer,
          stage.id,
          String(status),
          String(manifest.status ?? 1),
          log,
        ],
        { cwd: root, stdio: "inherit" },
      );
      resolve({
        failed: status !== 0 || manifest.status !== 0 || report.status !== 0,
      });
    };
    child.on("error", () => settle(127));
    child.on("close", (code, signal) => settle(code ?? (signal ? 128 : 1)));
  });
}

async function main() {
  const name = process.argv[2];
  if (!name || !/^[a-z0-9-]+$/.test(name))
    throw new Error("Name a registered stage plan.");
  const plan = validatePlan(
    JSON.parse(
      readFileSync(join(root, `eng/ci/stage-plans/${name}.json`), "utf8"),
    ),
  );
  if (recordEvidence() && !existsSync(docs))
    throw new Error("Build the Release evidence executable first.");
  const runId = option(
    "run-id",
    process.env.GITHUB_RUN_ID ?? `local-${randomUUID().replaceAll("-", "")}`,
  );
  const attempt = option("attempt", process.env.GITHUB_RUN_ATTEMPT ?? "1");
  const parallel = Number(
    option("parallel", String(Math.min(availableParallelism(), 4))),
  );
  const only = option("only", "");
  const selected =
    only === ""
      ? plan
      : {
          ...plan,
          stages: plan.stages.filter((stage) =>
            only.split(",").includes(stage.id),
          ),
        };
  const results = await runPlan(selected, parallel, (stage) =>
    execute(stage, plan, runId, attempt),
  );
  const failed = results
    .filter((result) => result.value.failed)
    .map((result) => result.stage.id);
  console.log(
    `${plan.producer}: ${results.length - failed.length} of ${results.length} stages passed.`,
  );
  if (failed.length > 0) {
    console.log(`Failed: ${failed.join(", ")}`);
    process.exitCode = 1;
  }
}

main().catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
});
