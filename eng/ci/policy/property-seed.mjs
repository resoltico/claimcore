// The base seed for the extended property exploration. A scheduled run derives it from the run's
// identity (deterministic, and different for each attempt); a manual run supplies one canonical
// unsigned 64-bit integer. Either way the seed is printed so a failure can be reproduced exactly.
import { createHash } from "node:crypto";
import { fileURLToPath } from "node:url";

const maximumPart = 256;
const maximumSeed = 2n ** 64n - 1n;

/**
 * @param {{ eventName: string, requestedSeed?: string, repository: string, workflow: string, runId: string, runAttempt: string }} run
 * @returns {string} The seed in canonical decimal form.
 */
export function selectPropertySeed({
  eventName,
  requestedSeed = "",
  repository,
  workflow,
  runId,
  runAttempt,
}) {
  if (eventName === "schedule") {
    const parts = [repository, workflow, runId, runAttempt];
    if (parts.some((part) => part.trim() === "" || part.length > maximumPart)) {
      throw new Error("Scheduled property seed identity is incomplete or unbounded.");
    }
    const hash = createHash("sha256").update(parts.join("\0"), "utf8").digest();
    return hash.readBigUInt64BE(0).toString();
  }
  if (!/^(0|[1-9][0-9]*)$/u.test(requestedSeed) || BigInt(requestedSeed) > maximumSeed) {
    throw new Error("The requested property seed must be one canonical unsigned 64-bit integer.");
  }
  return requestedSeed;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const { EVENT_NAME, REQUESTED_SEED, REPOSITORY, WORKFLOW, RUN_ID, RUN_ATTEMPT } = process.env;
  try {
    const seed = selectPropertySeed({
      eventName: EVENT_NAME ?? "",
      requestedSeed: REQUESTED_SEED ?? "",
      repository: REPOSITORY ?? "",
      workflow: WORKFLOW ?? "",
      runId: RUN_ID ?? "",
      runAttempt: RUN_ATTEMPT ?? "",
    });
    process.stdout.write(`${seed}\n`);
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  }
}
