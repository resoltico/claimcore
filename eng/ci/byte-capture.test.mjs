import assert from "node:assert/strict";
import test from "node:test";
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
