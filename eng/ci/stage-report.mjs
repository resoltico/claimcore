import {
  appendFileSync,
  mkdirSync,
  openSync,
  fstatSync,
  readSync,
  closeSync,
  writeFileSync,
} from "node:fs";
import { spawnSync } from "node:child_process";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { stageDiagnostic } from "./stage-diagnostics.mjs";

function logTail(path) {
  const descriptor = openSync(path, "r");
  try {
    const size = fstatSync(descriptor).size;
    const buffer = Buffer.alloc(Math.min(size, 2 * 1024 * 1024));
    const count = readSync(
      descriptor,
      buffer,
      0,
      buffer.length,
      size - buffer.length,
    );
    return buffer.subarray(0, count).toString("utf8");
  } finally {
    closeSync(descriptor);
  }
}

const root = fileURLToPath(new URL("../..", import.meta.url));
const [producer, stage, exitCode, manifestExit, logPath] =
  process.argv.slice(2);
try {
  if (process.argv.length !== 7)
    throw new Error("Invalid stage report invocation.");
  const inventory = spawnSync("git", ["ls-files", "-z"], {
    cwd: root,
    encoding: "utf8",
    maxBuffer: 4 * 1024 * 1024,
  });
  if (inventory.status !== 0)
    throw new Error("Tracked source inventory unavailable.");
  const report = stageDiagnostic({
    producer,
    stage,
    exitCode: Number(exitCode),
    manifestExit: Number(manifestExit),
    log: logPath === "-" ? "" : logTail(logPath),
    root,
    tracked: new Set(inventory.stdout.split("\0").filter(Boolean)),
  });
  const output = join(root, "artifacts/diagnostics", producer);
  mkdirSync(output, { recursive: true });
  writeFileSync(
    join(output, `${stage}.json`),
    JSON.stringify(report, null, 2) + "\n",
    { flag: "wx" },
  );
  console.log(
    `${stage}: ${report.outcome} (procedure ${report.exitCode}, evidence ${report.manifestExit}). ${report.guidance}`,
  );
  if (process.env.GITHUB_STEP_SUMMARY) {
    const lines = [
      `### ${stage}: ${report.outcome}`,
      `Procedure exit: ${report.exitCode}; evidence exit: ${report.manifestExit}.`,
      report.guidance,
    ];
    for (const item of report.findings)
      lines.push(
        `\`${item.file}${item.line ? `:${item.line}:${item.column}` : ""}\`${item.rule ? ` (${item.rule})` : ""}`,
      );
    appendFileSync(
      process.env.GITHUB_STEP_SUMMARY,
      lines.join("\n\n") + "\n\n",
    );
  }
} catch {
  console.error("Safe stage reporting failed; raw output was not published.");
  process.exitCode = 1;
}
