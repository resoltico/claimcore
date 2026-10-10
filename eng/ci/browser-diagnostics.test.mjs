import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import {
  existsSync,
  linkSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  realpathSync,
  rmSync,
  statSync,
  symlinkSync,
  unlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { boundCompletedCliLog, retainBrowserFailure } from "./retain-browser-failure.mjs";
import { assertNoSensitiveOutput } from "./policy/sensitive-output.mjs";
import {
  BrowserStepDiagnostic,
  startupDiagnostic,
} from "../../web/scripts/playwright-diagnostics.mjs";

const sources = new Map([
  ["/repo/web/e2e/check.spec.ts", { file: "web/e2e/check.spec.ts", lines: 100 }],
]);
const step = {
  category: "pw:api",
  title: "PRIVATE-SELECTOR-SECRET",
  location: { file: "/repo/web/e2e/check.spec.ts", line: 12, column: 3 },
};
test("browser diagnostics preserve safe first-failure location without raw step content", () => {
  const diagnostic = new BrowserStepDiagnostic(sources);
  diagnostic.begin(step);
  diagnostic.end({ ...step, error: { message: "PRIVATE-PROVIDER-SECRET" } });
  diagnostic.begin({ ...step, location: { ...step.location, line: 60 } });
  assert.deepEqual(diagnostic.snapshot(), {
    category: "pw:api",
    file: "web/e2e/check.spec.ts",
    line: 12,
    column: 3,
    errorKind: "unknown",
  });
  assert(!JSON.stringify(diagnostic.snapshot()).includes("PRIVATE"));
});
test("browser diagnostics reject unknown source paths categories and impossible locations", () => {
  for (const changed of [
    { ...step, category: "PRIVATE" },
    { ...step, location: { ...step.location, file: "/private/key" } },
    { ...step, location: { ...step.location, line: 101 } },
    { ...step, location: { ...step.location, line: -1 } },
    { ...step, location: { ...step.location, column: NaN } },
  ]) {
    const diagnostic = new BrowserStepDiagnostic(sources);
    diagnostic.begin(changed);
    diagnostic.end({ ...changed, error: new Error("PRIVATE") });
    assert.equal(diagnostic.snapshot(), null);
  }
});
test("browser diagnostics keep unfinished safe location without inheriting another test", () => {
  const first = new BrowserStepDiagnostic(sources);
  first.begin(step);
  first.end(step);
  assert.equal(first.snapshot()?.line, 12);
  const second = new BrowserStepDiagnostic(sources);
  assert.equal(second.snapshot(), null);
});

const startup = {
  truncated: false,
  authenticated: true,
  pageErrorSeen: false,
  readiness: { readyState: "interactive", view: "loading", alert: false },
  requests: [{ kind: "session", status: 200, phase: "headers", elapsedMs: 5000 }],
};
/** @param {Record<string, unknown>} value */
const encoded = (value) => Buffer.from(JSON.stringify(value));

/** @param {(root:string,source:string,destination:string,secret:string)=>void} body */
function diagnosticFixture(body) {
  const root = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-retained-diagnostics-"));
  const source = join(root, "diagnostics");
  const destination = join(root, "retained");
  const secret = join(root, "fixture.secret");
  mkdirSync(source, { mode: 0o700 });
  writeFileSync(secret, "SYNTHETIC-PRIVATE-DIAGNOSTIC-CANARY", { mode: 0o600 });
  try {
    body(root, source, destination, secret);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("scanned native CLI startup output survives privately with exact bytes and existing browser logs", () => {
  diagnosticFixture((_root, source, destination, secret) => {
    const names = ["cli-acceptance.log", "playwright.log", "web-host.log"];
    for (const name of names) {
      writeFileSync(join(source, name), `synthetic completed ${name}\n`, { mode: 0o600 });
    }
    writeFileSync(join(source, "unknown-output.txt"), "excluded fixture output");
    assert.equal(boundCompletedCliLog(source), false);
    assert.equal(assertNoSensitiveOutput([source], [secret]), 4);
    retainBrowserFailure(source, destination);
    assert.deepEqual(readdirSync(destination).sort(), names.sort());
    for (const name of names) {
      assert.deepEqual(readFileSync(join(destination, name)), readFileSync(join(source, name)));
      if (process.platform !== "win32") {
        assert.equal(statSync(join(destination, name)).mode & 0o777, 0o600);
      }
    }
    if (process.platform !== "win32") {
      assert.equal(statSync(destination).mode & 0o777, 0o700);
    }
  });
});

test("completed CLI diagnostics retain a bounded tail and count marker before exact sensitive scanning", () => {
  diagnosticFixture((_root, source, destination, secret) => {
    const path = join(source, "cli-acceptance.log");
    const original = Buffer.concat([
      Buffer.from("OMITTED-PREFIX-SENTINEL\n"),
      Buffer.alloc(17 * 1024 * 1024, 120),
      Buffer.from("\nnative startup final line\n"),
    ]);
    writeFileSync(path, original, { mode: 0o600 });
    assert.throws(() => assertNoSensitiveOutput([source], [secret]), /oversized/u);
    assert.equal(boundCompletedCliLog(source), true);
    const bytes = readFileSync(path);
    assert.equal(bytes.length, 16 * 1024 * 1024);
    assert.ok(bytes.toString("utf8", 0, 200).includes(`originalBytes=${original.length}`));
    assert.ok(!bytes.includes(Buffer.from("OMITTED-PREFIX-SENTINEL")));
    assert.ok(bytes.subarray(-27).toString("utf8").endsWith("native startup final line\n"));
    assert.equal(assertNoSensitiveOutput([source], [secret]), 1);
    retainBrowserFailure(source, destination);
    assert.deepEqual(readFileSync(join(destination, "cli-acceptance.log")), bytes);
  });
});

test("private CLI payload refuses the production scan-before-retention sequence", () => {
  diagnosticFixture((_root, source, destination, secret) => {
    writeFileSync(
      join(source, "cli-acceptance.log"),
      `native failure ${readFileSync(secret, "utf8")}\n`,
    );
    assert.throws(() => {
      boundCompletedCliLog(source);
      assertNoSensitiveOutput([source], [secret]);
      retainBrowserFailure(source, destination);
    }, /rejected/u);
    assert.equal(existsSync(destination), false);
  });
});

test("CLI diagnostic links, hard links, oversized unscreened files and occupied destinations refuse", () => {
  diagnosticFixture((root, source, destination, _secret) => {
    const target = join(root, "outside.log");
    const log = join(source, "cli-acceptance.log");
    writeFileSync(target, "outside material preserved");
    for (const linked of [target, join(root, "absent.log")]) {
      symlinkSync(linked, log);
      assert.throws(() => boundCompletedCliLog(source));
      assert.throws(() => retainBrowserFailure(source, destination));
      unlinkSync(log);
    }
    linkSync(target, log);
    assert.throws(() => boundCompletedCliLog(source));
    assert.throws(() => retainBrowserFailure(source, destination));
    unlinkSync(log);
    writeFileSync(log, Buffer.alloc(16 * 1024 * 1024 + 1));
    assert.throws(() => retainBrowserFailure(source, destination), /bounded size/u);
    writeFileSync(log, "safe native startup output");
    symlinkSync(root, join(root, "linked-parent"), "junction");
    assert.throws(() => retainBrowserFailure(source, join(root, "linked-parent/fresh")));
    mkdirSync(destination);
    writeFileSync(join(destination, "prior.txt"), "prior evidence preserved");
    assert.throws(() => retainBrowserFailure(source, destination));
    assert.equal(readFileSync(target, "utf8"), "outside material preserved");
    assert.equal(readFileSync(join(destination, "prior.txt"), "utf8"), "prior evidence preserved");
    assert.deepEqual(readdirSync(destination), ["prior.txt"]);
  });
});

test("failed private diagnostic admission prints only a fixed refusal without submitted path or payload", () => {
  diagnosticFixture((root, source, _destination, _secret) => {
    const occupied = join(root, "PRIVATE-PATH-SENTINEL");
    mkdirSync(occupied);
    writeFileSync(join(source, "cli-acceptance.log"), "PRIVATE-LOG-PAYLOAD-SENTINEL");
    const result = spawnSync(
      process.execPath,
      [fileURLToPath(new URL("./retain-browser-failure.mjs", import.meta.url)), source, occupied],
      { encoding: "utf8" },
    );
    assert.equal(result.status, 1);
    assert.equal(result.stdout, "");
    assert.equal(result.stderr, "Private browser/CLI failure diagnostics were refused.\n");
    assert.ok(!(result.stdout + result.stderr).includes("PRIVATE-PATH-SENTINEL"));
    assert.ok(!(result.stdout + result.stderr).includes("PRIVATE-LOG-PAYLOAD-SENTINEL"));
  });
});

test("native macOS inherited ACL grants refuse diagnostic retention before copying any payload", () => {
  if (process.platform !== "darwin") {
    return;
  }
  diagnosticFixture((root, source, destination, _secret) => {
    writeFileSync(join(source, "cli-acceptance.log"), "private startup evidence", { mode: 0o600 });
    const changed = spawnSync("/bin/chmod", [
      "+a",
      "everyone allow list,search,readattr,readextattr,readsecurity,file_inherit,directory_inherit",
      root,
    ]);
    assert.equal(changed.status, 0);
    assert.throws(() => retainBrowserFailure(source, destination), /ACL/u);
    assert.deepEqual(readdirSync(destination), []);
    assert.equal(
      readFileSync(join(source, "cli-acceptance.log"), "utf8"),
      "private startup evidence",
    );
  });
});

test("startup evidence admits only bounded status and readiness observations", () => {
  assert.deepEqual(startupDiagnostic(encoded(startup)), startup);
  const unavailable = { ...startup, authenticated: null, readiness: null, truncated: true };
  assert.deepEqual(startupDiagnostic(encoded(unavailable)), unavailable);
  assert.equal(startupDiagnostic(undefined), null);
});

test("startup evidence refuses arbitrary attachment fields and unbounded observations", () => {
  const [request] = startup.requests;
  for (const value of [
    { ...startup, url: "PRIVATE-URL-SECRET" },
    { ...startup, authenticated: "PRIVATE-TOKEN-SECRET" },
    { ...startup, pageErrorSeen: "PRIVATE-ERROR-SECRET" },
    { ...startup, readiness: { ...startup.readiness, view: "PRIVATE-DOM-SECRET" } },
    { ...startup, readiness: { ...startup.readiness, text: "PRIVATE-DOM-SECRET" } },
    { ...startup, requests: [{ ...request, body: "PRIVATE-PAYLOAD-SECRET" }] },
    { ...startup, requests: [{ ...request, status: 700 }] },
    { ...startup, requests: [{ ...request, elapsedMs: 60001 }] },
    { ...startup, requests: [{ ...request, phase: "PRIVATE-ERROR-SECRET" }] },
    { ...startup, requests: Array(41).fill(request) },
  ]) {
    assert.equal(startupDiagnostic(encoded(value)), null);
  }
  assert.equal(startupDiagnostic(Buffer.from("not JSON")), null);
  assert.equal(startupDiagnostic(Buffer.alloc(16385)), null);
});
