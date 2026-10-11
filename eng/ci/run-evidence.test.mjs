import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import {
  closeSync,
  existsSync,
  ftruncateSync,
  linkSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  openSync,
  readFileSync,
  realpathSync,
  renameSync,
  rmSync,
  symlinkSync,
  writeFileSync,
  writeSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { finishRun, transferEvidence } from "./run-evidence.mjs";
import { createRun } from "./run-context.mjs";
import { admittedReports } from "./run-reports.mjs";
import { requirePassedEvidence } from "./run-expectations.mjs";
import { copySource, sourceFingerprint } from "./source-snapshot.mjs";
import { gitEnvironment } from "./scan/process.mjs";
import { writePrerequisiteReports } from "./frontend-prerequisite-fixture.mjs";
import { prerequisiteStages, finishFrontendPrerequisites } from "./frontend-prerequisites.mjs";
import { fingerprint } from "./scan/files.mjs";
import { evidenceStageOwnership } from "./evidence-stage.mjs";

/** @param {(root:string, staging:string, destination:string)=>void} body */
function fixture(body) {
  const root = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-report-transfer-"));
  const staging = join(root, "qualified");
  mkdirSync(join(staging, "suite"), { recursive: true });
  writeFileSync(join(staging, "suite", "report.trx"), "synthetic qualified report\n");
  try {
    body(root, staging, join(root, "retained"));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("a report writer's held descriptor cannot alter its retained fresh-inode copy", () => {
  fixture((_root, staging, destination) => {
    const source = join(staging, "suite/report.trx");
    const fd = openSync(source, "r+");
    try {
      const qualified = fingerprint(staging);
      transferEvidence(staging, destination, qualified);
      const retained = join(destination, "suite/report.trx");
      const original = readFileSync(retained);
      assert.notEqual(lstatSync(retained).ino, lstatSync(source).ino);
      writeSync(fd, Buffer.from("changed by original writer"), 0, 26, 0);
      ftruncateSync(fd, 26);
      assert.notEqual(fingerprint(staging), qualified);
      assert.deepEqual(readFileSync(retained), original);
      assert.equal(fingerprint(destination), qualified);
    } finally {
      closeSync(fd);
    }
  });
});

test("changed qualified report bytes refuse before creating a retained tree", () => {
  fixture((_root, staging, destination) => {
    const qualified = fingerprint(staging);
    writeFileSync(join(staging, "suite/report.trx"), "different synthetic report");
    assert.throws(() => transferEvidence(staging, destination, qualified));
    assert.equal(existsSync(destination), false);
  });
});

test("creator cleanup unlinks only verified staging while a held writer cannot change retention", () => {
  fixture((_root, staging, destination) => {
    const source = join(staging, "suite/report.trx");
    const fd = openSync(source, "r+");
    try {
      const before = fingerprint(staging);
      const cleanup = transferEvidence(staging, destination, before, ["suite/report.trx"]);
      assert.equal(cleanup(), true);
      assert.equal(existsSync(staging), false);
      writeSync(fd, Buffer.from("unlinked writer"), 0, 15, 0);
      ftruncateSync(fd, 15);
      assert.equal(fingerprint(destination), before);
    } finally {
      closeSync(fd);
    }
  });
});

for (const side of ["stage", "retained"]) {
  for (const change of ["bytes", "inode", "directory", "file", "link", "hardlink"]) {
    test(`duplicate cleanup preserves ${side} after ${change} substitution`, () => {
      fixture((root, staging, destination) => {
        const before = fingerprint(staging);
        const cleanup = transferEvidence(staging, destination, before, ["suite/report.trx"]);
        const changed = side === "stage" ? staging : destination;
        const file = join(changed, "suite/report.trx");
        if (change === "bytes") {
          writeFileSync(file, "preserved changed evidence");
        } else if (change === "inode") {
          const bytes = readFileSync(file);
          renameSync(file, join(root, "old-report.trx"));
          writeFileSync(file, bytes);
        } else if (change === "directory") {
          mkdirSync(join(changed, "unknown-empty"));
        } else if (change === "file") {
          writeFileSync(join(changed, "unknown.txt"), "unowned evidence");
        } else if (change === "link") {
          renameSync(join(changed, "suite"), join(root, "outside"));
          symlinkSync(join(root, "outside"), join(changed, "suite"), "junction");
        } else {
          linkSync(file, join(root, "report-link.trx"));
        }
        assert.equal(cleanup(), false);
        assert.equal(existsSync(join(staging, "suite/report.trx")), true);
        assert.equal(existsSync(join(destination, "suite/report.trx")), true);
      });
    });
  }
}

test("known report paths cannot adopt empty directory leaves or noncanonical entries", () => {
  fixture((_root, staging) => {
    mkdirSync(join(staging, "empty.trx"));
    assert.equal(evidenceStageOwnership(staging, ["suite/report.trx", "empty.trx"]), null);
    for (const paths of [["../outside"], ["suite/../report.trx"], ["suite/report.trx", "suite"]]) {
      assert.equal(evidenceStageOwnership(staging, paths), null);
    }
  });
});

test("relative fingerprints survive relocation but bind the report name", () => {
  fixture((_root, staging, destination) => {
    const qualified = fingerprint(staging);
    transferEvidence(staging, destination, qualified);
    assert.equal(fingerprint(destination), qualified);
    renameSync(join(destination, "suite/report.trx"), join(destination, "suite/other.trx"));
    assert.notEqual(fingerprint(destination), qualified);
  });
});

test("a stale retained destination is preserved and refused", () => {
  fixture((_root, staging, destination) => {
    mkdirSync(destination);
    writeFileSync(join(destination, "prior.txt"), "preserve prior evidence");
    assert.throws(() => transferEvidence(staging, destination, fingerprint(staging)));
    assert.equal(readFileSync(join(destination, "prior.txt"), "utf8"), "preserve prior evidence");
    assert.equal(existsSync(join(destination, "suite/report.trx")), false);
  });
});

test("retained destination links and linked parents never redirect a qualified copy", () => {
  fixture((root, staging, destination) => {
    const outside = join(root, "outside");
    mkdirSync(outside);
    symlinkSync(outside, destination, "junction");
    assert.throws(() => transferEvidence(staging, destination, fingerprint(staging)));
    assert.equal(existsSync(join(outside, "suite/report.trx")), false);
    const parent = join(root, "linked-parent");
    symlinkSync(outside, parent, "junction");
    assert.throws(() => transferEvidence(staging, join(parent, "fresh"), fingerprint(staging)));
    assert.equal(existsSync(join(outside, "fresh")), false);
  });
});

/** @param {(run:import("./run-context.mjs").RunContext)=>Promise<void>} body */
async function frontendEvidenceFixture(body) {
  const origin = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-frontend-retention-"));
  let scratch;
  try {
    copySource(resolve(import.meta.dirname, "../.."), origin);
    for (const args of [
      ["init", "-b", "main"],
      ["config", "commit.gpgsign", "false"],
      ["config", "core.hooksPath", join(origin, ".git/owned-empty-hooks")],
      ["add", "-A"],
      [
        "-c",
        "user.name=Synthetic",
        "-c",
        "user.email=synthetic@example.invalid",
        "commit",
        "-m",
        "synthetic",
      ],
    ]) {
      execFileSync("git", args, { cwd: origin, env: gitEnvironment(), stdio: "pipe" });
    }
    const run = await createRun(origin);
    ({ scratch } = run);
    mkdirSync(join(run.source, "artifacts"), { recursive: true, mode: 0o700 });
    await body(run);
  } finally {
    if (scratch) {
      rmSync(scratch, { recursive: true, force: true });
    }
    rmSync(origin, { recursive: true, force: true });
  }
}

test("passed frontend gates require a valid receipt retained with owner reports and unchanged source", async () => {
  await frontendEvidenceFixture(async (run) => {
    writePrerequisiteReports(run.source);
    const stages = { "frontend-gates": "passed in 1s" };
    assert.throws(() => requirePassedEvidence(run, stages));
    finishFrontendPrerequisites(
      run,
      prerequisiteStages.map((id) => ({
        stage: { id, argv: ["synthetic"] },
        value: { status: "passed" },
      })),
    );
    const path = join(run.source, "artifacts/frontend/prerequisites.json");
    const bytes = readFileSync(path);
    const value = JSON.parse(bytes.toString("utf8"));
    writeFileSync(path, JSON.stringify({ ...value, runId: "0".repeat(36) }));
    assert.throws(() => admittedReports(run));
    writeFileSync(path, bytes);
    assert.ok(admittedReports(run).includes(path));
    requirePassedEvidence(run, stages);
    assert.equal(sourceFingerprint(run.source), run.sourceSha256);
    await finishRun(run, { passed: true, groups: [], stages });
    assert.equal(existsSync(join(run.source, "artifacts/retained-evidence")), false);
    assert.deepEqual(
      readFileSync(join(run.results, "artifacts/frontend/prerequisites.json")),
      bytes,
    );
    for (const leaf of [
      "artifacts/frontend/vitest-summary.json",
      "artifacts/frontend/coverage/coverage-summary.json",
      "web/artifacts/stryker/domain-mutation.json",
    ]) {
      assert.ok(existsSync(join(run.results, leaf)));
    }
    assert.equal(sourceFingerprint(join(dirname(run.results), "inputs")), run.sourceSha256);
  });
});

test("failed execution preserves its staging even when evidence can be admitted", async () => {
  await frontendEvidenceFixture(async (run) => {
    await finishRun(run, { passed: false, groups: [], stages: { command: "FAILED" } });
    assert.equal(existsSync(join(run.source, "artifacts/retained-evidence")), true);
    assert.equal(
      JSON.parse(readFileSync(join(dirname(run.results), "outcome.json"), "utf8")).passed,
      false,
    );
  });
});

test("unknown stage entries and refused cleanup diagnostics cannot mask successful admission", async () => {
  await frontendEvidenceFixture(async (run) => {
    const output = process.stdout.write.bind(process.stdout);
    const errors = process.stderr.write.bind(process.stderr);
    const unknown = join(run.source, "artifacts/retained-evidence/unknown-empty");
    let injected = false;
    let refused = false;
    try {
      process.stdout.write = (...args) => {
        if (String(args[0]).includes("Artifact secret scan passed") && !injected) {
          mkdirSync(unknown);
          injected = true;
        }
        return Boolean(Reflect.apply(output, process.stdout, args));
      };
      process.stderr.write = (...args) => {
        if (String(args[0]).includes("staged duplicate cleanup refused")) {
          refused = true;
          throw new Error("synthetic closed diagnostic sink");
        }
        return Boolean(Reflect.apply(errors, process.stderr, args));
      };
      await finishRun(run, { passed: true, groups: [123], stages: {} });
    } finally {
      process.stdout.write = output;
      process.stderr.write = errors;
    }
    assert.equal(injected && refused, true);
    assert.equal(existsSync(unknown), true);
    assert.equal(
      JSON.parse(readFileSync(join(run.results, "admission.json"), "utf8")).qualified,
      true,
    );
    assert.equal(
      JSON.parse(readFileSync(join(dirname(run.results), "outcome.json"), "utf8")).passed,
      true,
    );
  });
});
