import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { runToLog } from "./process-support.mjs";

/** @param {(root:string,log:string)=>Promise<void>} body */
async function fixture(body) {
  const root = mkdtempSync(join(tmpdir(), "claimcore-process-log-test-"));
  try {
    await body(root, join(root, "process.log"));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("ordinary multiline stdout and stderr are retained in full with the child's exit", async () => {
  await fixture(async (root, log) => {
    const stdout = "first line\nUnicode: العربية\nlast stdout line\n";
    const stderr = "first stderr line\nlast stderr line\n";
    const script =
      "process.stdout.write(process.argv[1]); process.stderr.write(process.argv[2]); process.exitCode=Number(process.argv[3]);";
    for (const status of [0, 7]) {
      assert.equal(
        await runToLog(process.execPath, ["-e", script, stdout, stderr, String(status)], {
          cwd: root,
          log,
        }),
        status,
      );
      const bytes = readFileSync(log);
      assert.equal(bytes.length, Buffer.byteLength(stdout + stderr));
      assert.ok(bytes.includes(Buffer.from(stdout)) && bytes.includes(Buffer.from(stderr)));
      assert.ok(!bytes.includes(Buffer.from("Log capture exceeded")));
    }
  });
});

/** Both streams honor pipe backpressure; the sentinel follows every flood byte. */
const flood = `
const block=Buffer.alloc(1024*1024,120);
async function pump(stream) {
  for(let index=0;index<20;index+=1) {
    await new Promise(resolve=>stream.write(block,resolve));
  }
}
Promise.all([pump(process.stdout),pump(process.stderr)]).then(()=>{
  process.stdout.write("OMITTED-PAYLOAD-SENTINEL",()=>{process.exitCode=Number(process.argv[1]);});
});`;

test(
  "both-stream overflow is bounded, omits later payload and refuses a successful command",
  { timeout: 30_000 },
  async () => {
    await fixture(async (root, log) => {
      assert.equal(await runToLog(process.execPath, ["-e", flood, "0"], { cwd: root, log }), 1);
      const bytes = readFileSync(log);
      const limit = 16 * 1024 * 1024;
      const observed = 40 * 1024 * 1024 + Buffer.byteLength("OMITTED-PAYLOAD-SENTINEL");
      const marker = `\n[Log capture exceeded ${limit} bytes; observedBytes=${observed}; countSaturated=false; further output omitted; stage refused.]\n`;
      assert.equal(statSync(log).size, limit + Buffer.byteLength(marker));
      assert.deepEqual(bytes.subarray(0, limit), Buffer.alloc(limit, 120));
      assert.equal(bytes.subarray(limit).toString("utf8"), marker);
      assert.ok(!bytes.includes(Buffer.from("OMITTED-PAYLOAD-SENTINEL")));
    });
  },
);

test("overflow preserves the child's nonzero exit", { timeout: 30_000 }, async () => {
  await fixture(async (root, log) => {
    assert.equal(await runToLog(process.execPath, ["-e", flood, "9"], { cwd: root, log }), 9);
    assert.ok(statSync(log).size < 16 * 1024 * 1024 + 256);
  });
});

test("the exact output limit remains complete and one additional byte refuses qualification", async () => {
  await fixture(async (root, log) => {
    const limit = 16 * 1024 * 1024;
    const script = "process.stdout.write(Buffer.alloc(Number(process.argv[1]),120));";
    for (const excess of [0, 1]) {
      assert.equal(
        await runToLog(process.execPath, ["-e", script, String(limit + excess)], {
          cwd: root,
          log,
        }),
        excess,
      );
      const bytes = readFileSync(log);
      assert.deepEqual(bytes.subarray(0, limit), Buffer.alloc(limit, 120));
      assert.equal(bytes.includes(Buffer.from("Log capture exceeded")), excess === 1);
      assert.ok(bytes.length < limit + 256);
    }
  });
});

test("actual termination preserves its platform status and preceding output", async () => {
  await fixture(async (root, log) => {
    const script =
      'process.stdout.write("before signal\\n",()=>process.kill(process.pid,"SIGTERM"));';
    // Windows PID termination reports exit 1; POSIX reports the terminating signal.
    assert.equal(
      await runToLog(process.execPath, ["-e", script], { cwd: root, log }),
      process.platform === "win32" ? 1 : 128,
    );
    assert.equal(readFileSync(log, "utf8"), "before signal\n");
  });
});

test("startup failure settles and closes its log once even when close follows error", async () => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-process-test-"));
  try {
    assert.equal(
      await runToLog(join(root, "absent-executable"), [], {
        cwd: root,
        log: join(root, "process.log"),
      }),
      127,
    );
    // The close event is asynchronous after the error event.
    await new Promise((resolve) => {
      setImmediate(resolve);
    });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
