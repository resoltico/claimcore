import { spawn } from "node:child_process";
import { once } from "node:events";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import {
  promoteContracts,
  promotionLock,
  requireContractConsumption,
} from "./contract-promotion.mjs";
import { verifyLock, writeLock } from "./contract-lock.mjs";

/** @param {(options:{directory:string,lockFile:string,generate:(stage:string)=>Promise<void>,validate:(stage:string)=>Promise<void>})=>Promise<void>} body */
async function fixture(body) {
  const root = await mkdtemp(join(tmpdir(), "claimcore-contract-promotion-"));
  const directory = join(root, "contracts");
  const lockFile = join(root, "lock.json");
  await mkdir(directory);
  await writeFile(join(directory, "a.json"), "accepted");
  await writeLock(directory, lockFile);
  try {
    await body({
      directory,
      lockFile,
      generate: (/** @type {string} */ stage) => writeFile(join(stage, "a.json"), "accepted"),
      validate: () => Promise.resolve(),
    });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

for (const cause of ["generator", "validator", "size", "lock"]) {
  test(`${cause} rejection preserves prior output and authoritative lock`, async () => {
    await fixture(async (options) => {
      const original = await readFile(options.lockFile);
      const refused = () => Promise.reject(new Error("Synthetic rejection"));
      const generate =
        cause === "generator"
          ? refused
          : (/** @type {string} */ stage) =>
              writeFile(join(stage, "a.json"), cause === "lock" ? "rejected" : "accepted");
      const validate = ["validator", "size"].includes(cause) ? refused : options.validate;
      await assert.rejects(promoteContracts({ ...options, generate, validate }));
      assert.equal(await readFile(join(options.directory, "a.json"), "utf8"), "accepted");
      assert.deepEqual(await readFile(options.lockFile), original);
      await verifyLock(options.directory, options.lockFile);
      assert.equal(existsSync(promotionLock(options.directory)), false);
    });
  });
}

test("accepted replacement installs verified bytes and intentional lock last", async () => {
  await fixture(async (options) => {
    await promoteContracts({
      ...options,
      acceptLock: true,
      generate: (/** @type {string} */ stage) => writeFile(join(stage, "b.json"), "replacement"),
    });
    await verifyLock(options.directory, options.lockFile);
    assert.equal(existsSync(join(options.directory, "a.json")), false);
    assert.equal(await readFile(join(options.directory, "b.json"), "utf8"), "replacement");
  });
});

test("concurrent writer cannot interleave or take over an unknown active writer", async () => {
  await fixture(async (options) => {
    const start = Promise.withResolvers();
    const pause = Promise.withResolvers();
    const writer = promoteContracts({
      ...options,
      generate: async (stage) => {
        start.resolve(undefined);
        await pause.promise;
        await options.generate(stage);
      },
    });
    await start.promise;
    try {
      await assert.rejects(promoteContracts(options), /writer/u);
    } finally {
      pause.resolve(undefined);
      await writer;
    }
    await verifyLock(options.directory, options.lockFile);
  });
});

test("interrupted promotion refuses consumption and deliberate regeneration preserves interruption evidence", async () => {
  await fixture(async (options) => {
    const originalLock = await readFile(options.lockFile);
    const writer = promotionLock(options.directory);
    // A real child pauses after its native directory rename has completed.
    const moduleUrl = new URL("./contract-promotion.mjs", import.meta.url).href;
    const script = `import fs from 'node:fs/promises'; import {syncBuiltinESMExports} from 'node:module'; const rename=fs.rename; fs.rename=async(from,to)=>{await rename(from,to); if(from===${JSON.stringify(options.directory)}){process.stdout.write('paused'); setInterval(()=>{},1000); await new Promise(()=>{});}}; syncBuiltinESMExports(); const {promoteContracts}=await import(${JSON.stringify(moduleUrl)}); await promoteContracts({directory:${JSON.stringify(options.directory)},lockFile:${JSON.stringify(options.lockFile)},generate:stage=>fs.writeFile(stage+'/a.json','accepted'),validate:async()=>{}});`;
    const child = spawn(process.execPath, ["--input-type=module", "-e", script], {
      stdio: ["ignore", "pipe", "ignore"],
    });
    const closed = once(child, "close");
    try {
      await once(child.stdout, "data", { signal: AbortSignal.timeout(10_000) });
      assert.equal(child.kill("SIGKILL"), true);
      const [code, signal] = await closed;
      assert.equal(code, null);
      assert.equal(signal, "SIGKILL");
      await assert.rejects(requireContractConsumption(options.directory), /interrupted/u);
      assert.equal(existsSync(options.directory), false);
      const metadata = JSON.parse(await readFile(join(writer, "owner.json"), "utf8"));
      assert.equal(await readFile(join(`${metadata.stage}-backup`, "a.json"), "utf8"), "accepted");
      const preserved = `${writer}-preserved`;
      await rename(writer, preserved);
      await writeFile(options.lockFile, originalLock);
      await promoteContracts(options);
      await verifyLock(options.directory, options.lockFile);
      assert.equal(existsSync(preserved), true);
      assert.equal(await readFile(join(`${metadata.stage}-backup`, "a.json"), "utf8"), "accepted");
    } finally {
      if (child.exitCode === null && child.signalCode === null) {
        child.kill("SIGKILL");
        await closed;
      }
    }
  });
});

test("an empty early reservation refuses takeover and can be preserved before explicit regeneration", async () => {
  await fixture(async (options) => {
    const writer = promotionLock(options.directory);
    await mkdir(writer);
    await assert.rejects(promoteContracts(options), /interrupted/u);
    await assert.rejects(requireContractConsumption(options.directory), /interrupted/u);
    await rename(writer, `${writer}-preserved`);
    await promoteContracts(options);
    await verifyLock(options.directory, options.lockFile);
    assert.equal(existsSync(`${writer}-preserved`), true);
  });
});
