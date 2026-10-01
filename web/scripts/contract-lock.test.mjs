import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { describeDirectory, verifyLock, writeLock } from "./contract-lock.mjs";

const withDirectory = async (files, body) => {
  const scratch = await mkdtemp(join(tmpdir(), "claimcore-contract-lock-"));
  try {
    const directory = join(scratch, "contracts");
    await mkdir(directory);
    for (const [name, content] of Object.entries(files)) {
      await writeFile(join(directory, name), content);
    }
    await body(directory, join(scratch, "lock.json"));
  } finally {
    await rm(scratch, { force: true, recursive: true });
  }
};

test("a lock records every artifact in ordinal order and verifies its own directory", async () => {
  await withDirectory({ "b.json": "{}", "A.json": "[]", "a.ts": "x" }, async (directory, lock) => {
    await writeLock(directory, lock);
    assert.deepEqual(
      (await describeDirectory(directory)).map((file) => file.path),
      ["A.json", "a.ts", "b.json"],
    );
    await verifyLock(directory, lock);
  });
});

test("a changed, missing or unexpected artifact is refused", async () => {
  const mutations = [
    (directory) => writeFile(join(directory, "a.json"), "changed"),
    (directory) => rm(join(directory, "a.json")),
    (directory) => writeFile(join(directory, "extra.json"), "extra"),
  ];
  for (const mutate of mutations) {
    await withDirectory({ "a.json": "{}" }, async (directory, lock) => {
      await writeLock(directory, lock);
      await mutate(directory);
      await assert.rejects(
        () => verifyLock(directory, lock),
        /differ from config\/contracts\.lock\.json/u,
      );
    });
  }
});

test("an absent output directory points at the generator and a nested directory is refused", async () => {
  await withDirectory({ "a.json": "{}" }, async (directory, lock) => {
    await writeLock(directory, lock);
    await assert.rejects(() => verifyLock(join(directory, "missing"), lock), /contract:generate/u);
    await mkdir(join(directory, "nested"));
    await assert.rejects(() => describeDirectory(directory), /regular files only/u);
  });
});
