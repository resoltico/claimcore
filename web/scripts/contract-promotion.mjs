// Portable promotion coordinates a directory and lock file; interrupted writes require recovery.
import { mkdir, mkdtemp, readFile, rename, rm, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { verifyLock, writeLock } from "./contract-lock.mjs";

/** @typedef {{directory:string,lockFile:string,writer:string,stage:string,backup:string,stagedLock:string,priorLock:string}} Paths */
/** @typedef {{directory:string,lockFile:string,generate:(stage:string)=>Promise<void>,validate:(stage:string)=>Promise<void>,acceptLock?:boolean}} Promotion */

/** @param {string} directory */
export const promotionLock = (directory) => join(dirname(directory), ".contracts-writer");

/** @param {string} directory */
export async function requireContractConsumption(directory) {
  if (existsSync(promotionLock(directory))) {
    throw new Error(
      "Contract generation/promotion is active or interrupted. Stop all generators and consumers. Preserve the reservation, stages, backups, output and reviewed lock; follow web/README.md before moving an abandoned reservation and regenerating.",
    );
  }
  await verifyLock(directory);
  if (existsSync(promotionLock(directory))) {
    throw new Error(
      "Contract promotion started during admission; retry after its writer completes.",
    );
  }
}

/** @param {Paths} paths @param {Promotion} options */
async function validateStage(paths, { generate, validate, acceptLock }) {
  await generate(paths.stage);
  await validate(paths.stage);
  if (acceptLock) {
    await writeLock(paths.stage, paths.stagedLock);
    await verifyLock(paths.stage, paths.stagedLock);
    await writeFile(paths.priorLock, await readFile(paths.lockFile), { flag: "wx", mode: 0o600 });
  } else {
    await verifyLock(paths.stage, paths.lockFile);
  }
}

/** @param {Paths} paths @param {{movedPrior:boolean,promoted:boolean,lockReplaced:boolean}} state */
async function rollbackPair(paths, state) {
  if (state.lockReplaced) {
    await rename(paths.priorLock, paths.lockFile);
  }
  if (state.promoted) {
    await rm(paths.directory, { recursive: true });
  }
  if (state.movedPrior) {
    await rename(paths.backup, paths.directory);
  }
}

/** @param {Paths} paths @param {boolean} acceptLock @param {{safeToClean:boolean}} lifetime */
async function commitPair(paths, acceptLock, lifetime) {
  const state = { movedPrior: false, promoted: false, lockReplaced: false };
  lifetime.safeToClean = false;
  try {
    if (existsSync(paths.directory)) {
      await rename(paths.directory, paths.backup);
      state.movedPrior = true;
    }
    await rename(paths.stage, paths.directory);
    state.promoted = true;
    if (acceptLock) {
      await rename(paths.stagedLock, paths.lockFile);
      state.lockReplaced = true;
    }
    lifetime.safeToClean = true;
  } catch (error) {
    await rollbackPair(paths, state);
    lifetime.safeToClean = true;
    throw error;
  }
}

/** @param {Promotion} options */
export async function promoteContracts(options) {
  const { directory, lockFile } = options;
  await mkdir(dirname(directory), { recursive: true });
  const writer = promotionLock(directory);
  await mkdir(writer, { mode: 0o700 }).catch(() => {
    throw new Error(
      "A contract writer is active or interrupted; ordinary generation refuses takeover.",
    );
  });
  const lifetime = { safeToClean: true };
  /** @type {Paths | undefined} */
  let paths;
  try {
    const stage = await mkdtemp(join(dirname(directory), ".contracts-stage-"));
    paths = {
      directory,
      lockFile,
      writer,
      stage,
      backup: `${stage}-backup`,
      stagedLock: join(writer, "replacement-lock.json"),
      priorLock: join(writer, "prior-lock.json"),
    };
    await writeFile(
      join(writer, "owner.json"),
      JSON.stringify({ format: 1, pid: process.pid, stage }),
      { mode: 0o600, flag: "wx" },
    );
    await validateStage(paths, options);
    await commitPair(paths, options.acceptLock === true, lifetime);
  } finally {
    if (lifetime.safeToClean) {
      if (paths) {
        await rm(paths.stage, { recursive: true, force: true });
        await rm(paths.backup, { recursive: true, force: true });
      }
      await rm(writer, { recursive: true });
    }
  }
}
