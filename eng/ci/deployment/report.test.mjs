import assert from "node:assert/strict";
import test from "node:test";
import { runtimeFiles } from "./transport-evidence.mjs";
import { verifyDeploymentReport } from "./report.mjs";

const identity = {
  revision: "a".repeat(40),
  sourceSha256: "b".repeat(64),
  producingInputsSha256: "8".repeat(64),
  runId: "123",
  attempt: "2",
};
const fingerprint = `${"AB:".repeat(31)}AB`;
const states = { primary: "1".repeat(64), witness: "2".repeat(64) };
const outcomes = [
  ["valid", true, true],
  ["unavailable", false, false],
  ["malformed", false, false],
  ["wrong-signature", false, false],
  ["expired", false, false],
  ["primary-revoked", false, true],
  ["witness-revoked", true, false],
  ["restored", true, true],
];
/** @param {string} target @param {boolean} connected @param {boolean} mutant */
const probe = (target, connected, mutant) => ({
  outcome: connected ? "CONNECTED_READ_VERIFIED" : "TLS_AUTHENTICATION_REFUSED",
  phase: connected ? "READ" : "OPEN",
  seededWork: connected,
  stateSha256: connected ? Reflect.get(states, target) : "",
  publicationSha256: (mutant ? "d" : "c").repeat(64),
  transportSha256: (mutant ? "f" : "e").repeat(64),
  runtimeVersion: "10.0.12",
  runtimeFiles: Object.fromEntries(runtimeFiles.map((file) => [file, "3".repeat(64)])),
});
/** @param {string | boolean | undefined} variant @param {boolean} next */
const update = (variant, next) => {
  if (["unavailable", "malformed"].includes(String(variant))) {
    return null;
  }
  if (variant === "expired") {
    return next ? "Jan 2 00:00:00 2000 GMT" : "Jan 1 00:00:00 2000 GMT";
  }
  return next ? "Oct 6 00:00:00 2027 GMT" : "Oct 6 00:00:00 2026 GMT";
};
/** @param {boolean} mutant */
const matrix = (mutant) =>
  outcomes.map(([variant, primary, witness], index) => ({
    variant,
    observedAt: "2026-10-06T12:00:00.000Z",
    crl: {
      variant: variant === "restored" ? "valid" : variant,
      sha256:
        variant === "unavailable"
          ? null
          : (variant === "restored" ? 1 : index + 1).toString(16).padStart(64, "0"),
      issuer: ["unavailable", "malformed"].includes(String(variant))
        ? null
        : "CN=ClaimCore Local Evaluation CA",
      thisUpdate: update(variant, false),
      nextUpdate: update(variant, true),
      primarySerial: "AA11",
      witnessSerial: "BB22",
    },
    requests: variant === "unavailable" ? 0 : 2,
    primary: probe("primary", mutant || Boolean(primary), mutant),
    witness: probe("witness", mutant || Boolean(witness), mutant),
  }));
const fixture = () => ({
  format: "claimcore-deployment-qualification",
  formatVersion: 2,
  identity,
  publications: { web: "7".repeat(64), database: "c".repeat(64) },
  run: "claimcore-operating-0123456789abcdef",
  result: "passed",
  properties: {
    liveness: true,
    syntheticReadinessRefused: true,
    exactHostRefusal: true,
    tlsNameRefusal: true,
    nonRoot: true,
    privateMountSeparation: true,
    stopAndReplacement: true,
    startupStop: true,
    privateInputRefusals: true,
    runtimeAdministrationRefused: true,
    installationPreserved: true,
    privateBuildInputsExcluded: true,
  },
  browserTrust: {
    scope: "CHROMIUM_LINUX_NSS",
    version: "159.0.1.2",
    publicCaSha256: fingerprint,
    untrustedRefused: true,
    trustedAuthentication: true,
    wrongHostnameRefused: true,
    expiredCertificateRefused: true,
    removedTrustRefused: true,
  },
  databaseRevocation: {
    production: matrix(false),
    mutant: matrix(true),
    disabledGuardDetected: true,
    readStatePreserved: true,
  },
});
/** @param {ReturnType<typeof fixture>} report @param {"production" | "mutant"} group @param {number} index */
const row = (report, group, index) => {
  const value = report.databaseRevocation[group][index];
  assert.ok(value);
  return value;
};
test("current-attempt deployment evidence requires every boundary and populated preservation result", () => {
  const report = fixture();
  assert.equal(verifyDeploymentReport(report, identity), report);
  for (const key of Object.keys(report)) {
    const missing = structuredClone(report);
    Reflect.deleteProperty(missing, key);
    assert.throws(() => verifyDeploymentReport(missing, identity));
  }
  assert.throws(() => verifyDeploymentReport({ ...report, privateLog: "excluded" }, identity));
  assert.throws(() => verifyDeploymentReport(report, { ...identity, attempt: "3" }));
  assert.throws(() => verifyDeploymentReport(report, { ...identity, revision: "9".repeat(40) }));
  assert.throws(() =>
    verifyDeploymentReport(report, { ...identity, sourceSha256: "9".repeat(64) }),
  );
});
/** @type {((report: ReturnType<typeof fixture>) => void)[]} */
const changes = [
  (report) => {
    for (const item of [
      ...report.databaseRevocation.production,
      ...report.databaseRevocation.mutant,
    ]) {
      for (const result of [item.primary, item.witness]) {
        if (result.outcome === "CONNECTED_READ_VERIFIED") {
          result.stateSha256 = "";
        }
      }
    }
  },
  (report) => {
    report.databaseRevocation.production.pop();
  },
  (report) => {
    report.databaseRevocation.mutant.pop();
  },
  (report) => {
    row(report, "production", 1).primary = probe("primary", true, false);
  },
  (report) => {
    row(report, "production", 1).primary.outcome = "OTHER_FAILURE";
  },
  (report) => {
    row(report, "mutant", 1).primary = probe("primary", false, true);
  },
  (report) => {
    row(report, "production", 0).primary.seededWork = false;
  },
  (report) => {
    row(report, "production", 7).primary.stateSha256 = "9".repeat(64);
  },
  (report) => {
    row(report, "production", 3).crl.issuer = null;
  },
  (report) => {
    row(report, "production", 2).requests = 0;
  },
  (report) => {
    row(report, "production", 1).requests = 2;
  },
  (report) => {
    row(report, "mutant", 1).primary.phase = "OPEN";
  },
  (report) => {
    row(report, "production", 0).crl.issuer = "Synthetic claimant payload";
  },
  (report) => {
    row(report, "production", 4).crl.nextUpdate = "Oct 6 00:00:00 2027 GMT";
  },
  (report) => {
    row(report, "production", 2).crl.sha256 = row(report, "production", 0).crl.sha256;
  },
  (report) => {
    row(report, "mutant", 2).crl.sha256 = "9".repeat(64);
  },
  (report) => {
    row(report, "production", 0).primary.stateSha256 = "";
  },
  (report) => {
    report.publications.database = "9".repeat(64);
  },
  (report) => {
    row(report, "mutant", 2).primary.transportSha256 = "e".repeat(64);
  },
];
test("absent, incomplete or contradictory TLS evidence cannot be published as qualification", () => {
  for (const change of changes) {
    const report = fixture();
    change(report);
    assert.throws(() => verifyDeploymentReport(report, identity));
  }
});
