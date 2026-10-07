import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join, resolve } from "node:path";
import { verifyPublished } from "../publish/main.mjs";
import { verifyProbe } from "./transport-evidence.mjs";
import { sourceFingerprint } from "../source-snapshot.mjs";
import { producingInputDigest } from "../publish/inputs.mjs";
import { verifyBrowserTrustReport } from "./browser-trust-qualification.mjs";
import { verifyDisabledGuard, verifyRevocationMatrix } from "./revocation-qualification.mjs";

const digest = /^[0-9a-f]{64}$/u;
const properties = [
  "liveness",
  "syntheticReadinessRefused",
  "exactHostRefusal",
  "tlsNameRefusal",
  "nonRoot",
  "privateMountSeparation",
  "stopAndReplacement",
  "startupStop",
  "privateInputRefusals",
  "runtimeAdministrationRefused",
  "installationPreserved",
  "privateBuildInputsExcluded",
];
/** @typedef {{revision: string, sourceSha256: string, producingInputsSha256: string, runId: string, attempt: string}} Identity */
/** @param {string} root @returns {Identity} */
export function evidenceIdentity(root) {
  return {
    revision: execFileSync("git", ["rev-parse", "HEAD"], { cwd: root, encoding: "utf8" }).trim(),
    sourceSha256: sourceFingerprint(root),
    producingInputsSha256: producingInputDigest(root),
    runId: process.env.GITHUB_RUN_ID ?? "local",
    attempt: process.env.GITHUB_RUN_ATTEMPT ?? "1",
  };
}
/** @param {object} value @param {string[]} expected */
function keys(value, expected) {
  assert.ok(value && typeof value === "object" && !Array.isArray(value));
  assert.deepEqual(Object.keys(value).sort(), [...expected].sort());
}
/** @param {string | null} value */
function crlTime(value) {
  assert.ok(typeof value === "string");
  assert.match(
    value,
    /^(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) {1,2}(?:[1-9]|[12][0-9]|3[01]) (?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9] [0-9]{4} GMT$/u,
  );
  const time = Date.parse(value);
  assert.ok(Number.isFinite(time));
  return time;
}
/** @param {import("./revocation-qualification.mjs").Observation} value */
function crlMetadata(value) {
  const unavailable = value.variant === "unavailable";
  assert.ok(unavailable ? value.crl.sha256 === null : digest.test(value.crl.sha256 ?? ""));
  if (unavailable || value.variant === "malformed") {
    assert.equal(value.crl.issuer, null);
    assert.equal(value.crl.thisUpdate, null);
    assert.equal(value.crl.nextUpdate, null);
    return;
  }
  assert.equal(value.crl.issuer, "CN=ClaimCore Local Evaluation CA");
  const first = crlTime(value.crl.thisUpdate);
  const next = crlTime(value.crl.nextUpdate);
  const observed = Date.parse(value.observedAt);
  assert.ok(first < next && first <= observed);
  assert.ok(value.variant === "expired" ? next < observed : next > observed);
}
/** @param {import("./revocation-qualification.mjs").Observation[]} production @param {import("./revocation-qualification.mjs").Observation[]} mutant */
function crlConsistency(production, mutant) {
  const [first] = production;
  assert.ok(first);
  const lists = production.filter((item) => !["unavailable", "restored"].includes(item.variant));
  assert.equal(new Set(lists.map((item) => item.crl.sha256)).size, lists.length);
  assert.deepEqual(production.at(-1)?.crl, first.crl);
  assert.deepEqual(
    production.map((item) => item.crl),
    mutant.map((item) => item.crl),
  );
}
/** @param {string} root */
export function publicationEvidence(root) {
  verifyPublished(root, ["web", "database"]);
  return Object.fromEntries(
    ["web", "database"].map((product) => [
      product,
      JSON.parse(readFileSync(join(root, "manifests", `${product}.json`), "utf8")).treeSha256,
    ]),
  );
}
/** @param {import("./revocation-qualification.mjs").Observation} value */
function observation(value) {
  keys(value, ["variant", "observedAt", "crl", "requests", "primary", "witness"]);
  assert.match(
    value.observedAt,
    /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$/u,
  );
  assert.ok(Number.isFinite(Date.parse(value.observedAt)));
  keys(value.crl, [
    "variant",
    "sha256",
    "issuer",
    "thisUpdate",
    "nextUpdate",
    "primarySerial",
    "witnessSerial",
  ]);
  assert.ok(Number.isSafeInteger(value.requests) && value.requests >= 0);
  for (const field of ["primarySerial", "witnessSerial"]) {
    assert.match(Reflect.get(value.crl, field), /^[0-9A-F]{2,64}$/u);
  }
  crlMetadata(value);
  verifyProbe(value.primary);
  verifyProbe(value.witness);
}
/** @param {Identity} value @param {Identity} identity */
function verifyIdentity(value, identity) {
  keys(value, ["revision", "sourceSha256", "producingInputsSha256", "runId", "attempt"]);
  assert.deepEqual(value, identity);
  assert.match(identity.revision, /^[0-9a-f]{40}$/u);
  assert.match(identity.sourceSha256, digest);
  assert.match(identity.producingInputsSha256, digest);
  assert.match(identity.runId, /^(?:local|[0-9]+)$/u);
  assert.match(identity.attempt, /^[1-9][0-9]*$/u);
}
/** @param {import("../types.mjs").Json} value @param {Identity} identity */
export function verifyDeploymentReport(value, identity) {
  keys(value, [
    "format",
    "formatVersion",
    "run",
    "result",
    "identity",
    "publications",
    "properties",
    "browserTrust",
    "databaseRevocation",
  ]);
  assert.equal(value["format"], "claimcore-deployment-qualification");
  assert.equal(value["formatVersion"], 2);
  assert.equal(value["result"], "passed");
  assert.match(value["run"], /^claimcore-operating-[0-9a-f]{16}$/u);
  verifyIdentity(value["identity"], identity);
  keys(value["publications"], ["web", "database"]);
  assert.match(value["publications"].web, digest);
  assert.match(value["publications"].database, digest);
  keys(value["properties"], properties);
  for (const name of properties) {
    assert.equal(value["properties"][name], true);
  }
  verifyBrowserTrustReport(value["browserTrust"], value["browserTrust"].publicCaSha256);
  assert.match(value["browserTrust"].publicCaSha256, /^(?:[0-9A-F]{2}:){31}[0-9A-F]{2}$/u);
  /** @type {{production: import("./revocation-qualification.mjs").Observation[], mutant: import("./revocation-qualification.mjs").Observation[], disabledGuardDetected: boolean, readStatePreserved: boolean}} */
  const database = value["databaseRevocation"];
  keys(database, ["production", "mutant", "disabledGuardDetected", "readStatePreserved"]);
  assert.equal(database.disabledGuardDetected, true);
  assert.equal(database.readStatePreserved, true);
  assert.ok(Array.isArray(database.production) && Array.isArray(database.mutant));
  database.production.forEach(observation);
  database.mutant.forEach(observation);
  verifyRevocationMatrix(database.production);
  assert.deepEqual(
    database.production.map((item) => item.variant),
    database.mutant.map((item) => item.variant),
  );
  verifyDisabledGuard(database.production, database.mutant);
  crlConsistency(database.production, database.mutant);
  assert.equal(database.production[0]?.primary.publicationSha256, value["publications"].database);
  return value;
}
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    const [path] = process.argv.slice(2);
    assert.ok(path && process.argv.length === 3);
    assert.ok(statSync(path).size <= 64 * 1024);
    const value = verifyDeploymentReport(
      JSON.parse(readFileSync(path, "utf8")),
      evidenceIdentity(resolve(import.meta.dirname, "../../..")),
    );
    if (process.env.CLAIMCORE_PUBLISHED_DIR !== undefined) {
      assert.deepEqual(
        value["publications"],
        publicationEvidence(process.env.CLAIMCORE_PUBLISHED_DIR),
      );
    }
    process.stdout.write("Current-attempt sanitized deployment evidence verified.\n");
  } catch {
    process.stderr.write(
      "Deployment evidence is absent, incomplete, contradictory or from different inputs.\n",
    );
    process.exitCode = 1;
  }
}
