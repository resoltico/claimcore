import assert from "node:assert/strict";
import test from "node:test";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { byteCapture, consoleMaximumBytes } from "./byte-capture.mjs";
import { runChild } from "./scan/process.mjs";

test("raw console capture counts both streams and copies only admitted bytes", () => {
  const capture = byteCapture(4);
  const giant = Buffer.alloc(100, 65);
  capture.stdout(giant);
  giant.fill(66);
  capture.stderr(Buffer.from("x"));
  assert.deepEqual(capture.result(), { stdout: "AAAA", stderr: "", retained: 4, overflow: true });
});

test("split UTF-8 decodes after byte retention", () => {
  const capture = byteCapture(3);
  const bytes = Buffer.from("€");
  capture.stdout(bytes.subarray(0, 1));
  capture.stdout(bytes.subarray(1));
  assert.equal(capture.result().stdout, "€");
  assert.equal(capture.result().overflow, false);
});

for (const excess of [0, 1]) {
  test(`real child drains both streams at console boundary + ${excess}`, async () => {
    const half = consoleMaximumBytes / 2;
    const result = await runChild(
      process.execPath,
      [
        "-e",
        `process.stdout.write(Buffer.alloc(${half})); process.stderr.write(Buffer.alloc(${half + excess}));`,
      ],
      {
        cwd: process.cwd(),
        env: process.env,
      },
    );
    assert.equal(result.status, 0);
    assert.equal(result.overflow, excess === 1);
    assert.equal(
      Buffer.byteLength(result.stdout) + Buffer.byteLength(result.stderr),
      consoleMaximumBytes,
    );
  });
}

test("overflow preserves natural child failure and startup failure refuses", async () => {
  const result = await runChild(
    process.execPath,
    ["-e", `process.stdout.write(Buffer.alloc(${consoleMaximumBytes + 1})); process.exitCode=7;`],
    {
      cwd: process.cwd(),
      env: process.env,
    },
  );
  assert.equal(result.status, 7);
  assert.equal(result.overflow, true);
  await assert.rejects(
    runChild("claimcore-absent-synthetic-child", [], { cwd: process.cwd(), env: process.env }),
  );
});

test("stalled child is terminated by its deadline", async () => {
  await assert.rejects(
    runChild(process.execPath, ["-e", "setInterval(()=>{},1000)"], {
      cwd: process.cwd(),
      env: process.env,
      timeoutMs: 100,
    }),
    /timed out/u,
  );
});

/** Observe fixture-owned process absence without signalling a possibly reused PID.
 * @param {number} pid */
function waitForFixtureExit(pid) {
  const deadline = Date.now() + 5000;
  return new Promise((resolve, reject) => {
    const check = () => {
      try {
        process.kill(pid, 0);
        if (Date.now() >= deadline) {
          reject(new Error("Synthetic descendant did not settle."));
        } else {
          setTimeout(check, 20);
        }
      } catch (error) {
        if (/** @type {NodeJS.ErrnoException} */ (error).code === "ESRCH") {
          resolve(undefined);
        } else {
          reject(error);
        }
      }
    };
    check();
  });
}

test("an exited child cannot remove the deadline by leaving inherited pipes in a descendant", async () => {
  const directory = mkdtempSync(join(tmpdir(), "claimcore-inherited-pipes-"));
  const identity = join(directory, "pid");
  const stop = join(directory, "stop");
  const descendant = `const fs=require('node:fs'); fs.writeFileSync(${JSON.stringify(identity)},String(process.pid),{mode:0o600,flag:'wx'}); const keep=setInterval(()=>{if(fs.existsSync(${JSON.stringify(stop)})){clearInterval(keep);}},20); setTimeout(()=>clearInterval(keep),5000).unref();`;
  const script = `const {spawn}=require('node:child_process'); spawn(process.execPath,['-e',${JSON.stringify(descendant)}],{stdio:'inherit'}).unref();`;
  const started = Date.now();
  try {
    await assert.rejects(
      runChild(process.execPath, ["-e", script], {
        cwd: process.cwd(),
        env: process.env,
        timeoutMs: 1000,
      }),
      /child exit 0; descendant or pipe settlement remains unknown/u,
    );
    assert.ok(
      Date.now() - started < 4000,
      "Refusal must settle before the descendant's pipes close",
    );
  } finally {
    writeFileSync(stop, "", { mode: 0o600 });
    if (existsSync(identity)) {
      const pid = Number(readFileSync(identity, "utf8"));
      assert.ok(Number.isSafeInteger(pid) && pid > 0);
      await waitForFixtureExit(pid);
    }
    rmSync(directory, { recursive: true, force: true });
  }
});
