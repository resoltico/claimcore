import {
  readFileSync,
  readdirSync,
  mkdirSync,
  writeFileSync,
  appendFileSync,
} from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";
import { jsonProcess } from "./dependency-process.mjs";
import {
  packageKey,
  packageRows,
  safeFinding,
  validateHolds,
  classifyUpdates,
  newer,
} from "./dependency-policy.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
const mode = process.argv[2];
if (!["security", "health"].includes(mode) || process.argv.length > 4)
  throw new Error("Expected security or health and optional registry path.");
const output =
  mode === "health"
    ? "artifacts/dependency-health"
    : "artifacts/diagnostics/quality";
const file =
  mode === "health" ? "report.json" : "dependency-security.details.json";
const installed = new Map();
const add = (ecosystem, name, resolved) => {
  const key = packageKey(ecosystem, name);
  if (!installed.has(key)) installed.set(key, new Set());
  installed.get(key).add(resolved);
};
function readLocks(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (
      entry.isDirectory() &&
      !["bin", "obj", "node_modules", "artifacts", ".git"].includes(entry.name)
    )
      readLocks(join(directory, entry.name));
    if (entry.isFile() && entry.name === "packages.lock.json") {
      const lock = JSON.parse(
        readFileSync(join(directory, entry.name), "utf8"),
      );
      for (const framework of Object.values(lock.dependencies)) {
        for (const [name, value] of Object.entries(framework))
          if (value.resolved) add("nuget", name, value.resolved);
      }
    }
  }
}
const common = [
  "package",
  "list",
  "--project",
  join(root, "ClaimCore.slnx"),
  "--format",
  "json",
  "--output-version",
  "1",
  "--no-restore",
];
const nuget = (flag, transitive = true) =>
  jsonProcess(
    "dotnet",
    [...common, flag, ...(transitive ? ["--include-transitive"] : [])],
    root,
  );
const result = {
  schemaVersion: 1,
  mode,
  outcome: "metadata-error",
  findings: [],
  checkedUtc: new Date().toISOString(),
};
try {
  readLocks(join(root, "src"));
  readLocks(join(root, "tests"));
  readLocks(join(root, "eng"));
  const bytes = readFileSync(join(root, "web/package-lock.json"));
  const lock = JSON.parse(bytes);
  for (const [path, value] of Object.entries(lock.packages))
    if (path && value.version)
      add("npm", path.split("node_modules/").at(-1), value.version);
  result.npmLockSha256 = createHash("sha256").update(bytes).digest("hex");
  const holds = validateHolds(
    JSON.parse(
      readFileSync(
        process.argv[3] ?? join(root, "dependency-holds.json"),
        "utf8",
      ),
    ),
    installed,
  );
  if (mode === "security") {
    for (const flag of ["--vulnerable", "--deprecated"]) {
      for (const item of packageRows(nuget(flag), [
        "topLevelPackages",
        "transitivePackages",
      ])) {
        result.findings.push(
          safeFinding(
            "nuget",
            item.id,
            item.resolvedVersion,
            undefined,
            installed,
            flag.slice(2),
          ),
        );
      }
    }
    result.outcome = result.findings.length ? "security-refused" : "passed";
  } else {
    for (const item of packageRows(nuget("--outdated"), [
      "topLevelPackages",
      "transitivePackages",
    ])) {
      result.findings.push(
        safeFinding(
          "nuget",
          item.id,
          item.resolvedVersion,
          item.latestVersion,
          installed,
          "update",
        ),
      );
    }
    const npm = jsonProcess(
      "npm",
      ["outdated", "--json"],
      join(root, "web"),
      [0, 1],
    );
    if (!npm || typeof npm !== "object" || Array.isArray(npm))
      throw new Error("DEPENDENCY_METADATA_INVALID");
    for (const [name, value] of Object.entries(npm)) {
      const candidate = safeFinding(
        "npm",
        name,
        value.current,
        value.latest,
        installed,
        "update",
      );
      if (newer(value.latest, value.current)) result.findings.push(candidate);
      else if (newer(value.current, value.latest)) {
        const published = jsonProcess(
          "npm",
          [
            "view",
            `${name}@${value.current.split(".")[0]}`,
            "version",
            "--json",
          ],
          join(root, "web"),
        );
        const versions = Array.isArray(published) ? published : [published];
        if (!versions.length) throw new Error("DEPENDENCY_METADATA_EMPTY");
        const latest = versions.reduce((a, b) => (newer(a, b) ? a : b));
        if (newer(latest, value.current))
          result.findings.push(
            safeFinding(
              "npm",
              name,
              value.current,
              latest,
              installed,
              "update",
            ),
          );
      }
    }
    result.findings = classifyUpdates(result.findings, holds);
    result.outcome = result.findings.some((item) => !item.held)
      ? "updates-available"
      : "passed";
  }
} catch (error) {
  // No registry payload, credential, request URI or provider exception is published.
  result.outcome = "metadata-error";
  result.error = /^DEPENDENCY_[A-Z_]+$/u.test(error.message)
    ? error.message
    : "DEPENDENCY_POLICY_OR_METADATA_INVALID";
}
result.findings = [
  ...new Map(
    result.findings.map((item) => [JSON.stringify(item), item]),
  ).values(),
];
result.findingCount = result.findings.length;
result.findings = result.findings.slice(0, 100);
mkdirSync(join(root, output), { recursive: true });
writeFileSync(
  join(root, output, file),
  JSON.stringify(result, null, 2) + "\n",
  { flag: "wx" },
);
console.log(
  `Dependency ${mode}: ${result.outcome}; ${result.findingCount} findings. Report: ${output}/${file}`,
);
if (process.env.GITHUB_STEP_SUMMARY) {
  const lines = [
    `### Dependency ${mode}: ${result.outcome}`,
    "",
    "| Package | Current | Alternative | State |",
    "|---|---|---|---|",
  ];
  for (const item of result.findings)
    lines.push(
      `| ${item.ecosystem}/${item.package} | ${item.current} | ${item.latest ?? "—"} | ${item.held ? "reviewed hold" : item.kind} |`,
    );
  if (result.error)
    lines.push(
      "",
      `Metadata/policy failure: ${result.error}. Inspect the pinned graph and approved hold dates; no automatic updates or retries of security findings.`,
    );
  appendFileSync(process.env.GITHUB_STEP_SUMMARY, lines.join("\n") + "\n");
}
process.exitCode =
  result.outcome === "passed" ? 0 : result.outcome === "metadata-error" ? 3 : 2;
