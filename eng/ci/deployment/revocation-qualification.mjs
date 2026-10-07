import assert from "node:assert/strict";
import { variants } from "../../operations/revocation-fixture.mjs";

import { connected, refused, verifyProbe } from "./transport-evidence.mjs";
/** @typedef {import("./transport-evidence.mjs").Probe} Probe */
/** @typedef {{variant: string, sha256: string | null, issuer: string | null, thisUpdate: string | null, nextUpdate: string | null, primarySerial: string, witnessSerial: string}} Crl */
/** @typedef {{variant: string, observedAt: string, crl: Crl, requests: number, primary: Probe, witness: Probe}} Observation */
/** @param {string} variant @param {string} target */
const expected = (variant, target) =>
  ["valid", "restored"].includes(variant) ||
  (variant.endsWith("-revoked") && !variant.startsWith(target))
    ? connected
    : refused;

/** Same admission is applied to normal and mutant observations. @param {Observation[]} observations */
export function verifyRevocationMatrix(observations) {
  assert.deepEqual(
    observations.map((item) => item.variant),
    ["valid", "unavailable", ...variants.slice(1), "restored"],
  );
  const [baseline] = observations;
  assert.ok(baseline);
  for (const key of ["publicationSha256", "transportSha256", "runtimeVersion", "runtimeFiles"]) {
    assert.deepEqual(Reflect.get(baseline.primary, key), Reflect.get(baseline.witness, key));
  }
  for (const item of observations) {
    for (const target of ["primary", "witness"]) {
      const result = verifyProbe(target === "primary" ? item.primary : item.witness);
      /** @type {Probe} */
      const initial = target === "primary" ? baseline.primary : baseline.witness;
      assert.equal(
        result.outcome,
        expected(item.variant, target),
        `Revocation boundary ${item.variant}/${target}`,
      );
      assert.equal(result.publicationSha256, initial.publicationSha256);
      assert.equal(result.transportSha256, initial.transportSha256);
      assert.equal(result.runtimeVersion, initial.runtimeVersion);
      assert.deepEqual(result.runtimeFiles, initial.runtimeFiles);
      assert.equal(result.stateSha256, result.outcome === connected ? initial.stateSha256 : "");
    }
  }
  for (const item of observations) {
    assert.equal(item.crl.variant, item.variant === "restored" ? "valid" : item.variant);
    assert.equal(item.crl.primarySerial, baseline.crl.primarySerial);
    assert.equal(item.crl.witnessSerial, baseline.crl.witnessSerial);
    assert.ok(item.variant === "unavailable" ? item.requests === 0 : item.requests > 0);
  }
  return true;
}
/** @param {import("./browser-trust-qualification.mjs").Compose} compose */
const requests = (compose) =>
  compose(["logs", "--no-log-prefix", "revocation"])
    .split("\n")
    .filter(
      (line) =>
        line.startsWith('{"crlRequest":') &&
        !line.includes('"127.0.0.1"') &&
        !line.includes('"::1"'),
    ).length;
/** @param {import("./browser-trust-qualification.mjs").Compose} compose @param {string} service @param {string} target @returns {Probe} */
function probe(compose, service, target) {
  return verifyProbe(JSON.parse(compose(["run", "--rm", "--no-deps", service, target]).trim()));
}
/** @param {import("./browser-trust-qualification.mjs").Compose} compose @param {string} service */
function matrix(compose, service) {
  /** @type {Observation[]} */
  const observations = [];
  /** @type {Crl | undefined} */
  let valid;
  for (const variant of ["valid", "unavailable", ...variants.slice(1), "restored"]) {
    let crl;
    if (variant === "unavailable") {
      compose(["stop", "revocation"]);
      assert.ok(valid);
      crl = { ...valid, variant, sha256: null, issuer: null, thisUpdate: null, nextUpdate: null };
    } else {
      crl = JSON.parse(
        compose([
          "run",
          "--rm",
          "--no-deps",
          "--entrypoint",
          "node",
          "configure",
          "/source/eng/operations/revocation-fixture.mjs",
          "publish",
          variant === "restored" ? "valid" : variant,
        ]),
      );
      compose(["up", "--detach", "--wait", "--force-recreate", "revocation"]);
      if (variant === "valid") {
        valid = crl;
      }
    }
    const observedAt = new Date().toISOString();
    const before = requests(compose);
    const primary = probe(compose, service, "primary");
    const witness = probe(compose, service, "witness");
    observations.push({
      variant,
      observedAt,
      crl,
      requests: requests(compose) - before,
      primary,
      witness,
    });
  }
  return observations;
}
/** @param {import("./browser-trust-qualification.mjs").Compose} compose @param {Observation[]} production */
function verifyRuntime(compose, production) {
  const [baseline] = production;
  assert.ok(baseline);
  assert.match(baseline.primary.runtimeVersion, /^[0-9]+\.[0-9]+\.[0-9]+$/u);
  const architecture = compose([
    "run",
    "--rm",
    "--no-deps",
    "--entrypoint",
    "uname",
    "administration",
    "-m",
  ]).trim();
  assert.ok(["aarch64", "x86_64"].includes(architecture));
  const systemDirectory =
    architecture === "aarch64" ? "/usr/lib/aarch64-linux-gnu" : "/usr/lib/x86_64-linux-gnu";
  for (const [file, digest] of Object.entries(baseline.primary.runtimeFiles)) {
    /** @type {string} */
    const directory =
      file.startsWith("libssl") || file.startsWith("libcrypto")
        ? systemDirectory
        : `/usr/share/dotnet/shared/Microsoft.NETCore.App/${baseline.primary.runtimeVersion}`;
    /** @type {string[]} */
    /** @type {string[]} */
    const [actual] = compose([
      "run",
      "--rm",
      "--no-deps",
      "--entrypoint",
      "sha256sum",
      "administration",
      `${directory}/${file}`,
    ])
      .trim()
      .split(/\s+/u);
    assert.equal(actual, digest, "Probe must use production TLS/cryptography runtime bytes.");
  }
}
/** @param {Observation[]} production @param {Observation[]} mutant */
export function verifyDisabledGuard(production, mutant) {
  const [original] = production;
  const [control] = mutant;
  assert.ok(original && control);
  for (const item of mutant) {
    for (const target of ["primary", "witness"]) {
      const result = target === "primary" ? item.primary : item.witness;
      const baseline = target === "primary" ? production[0]?.primary : production[0]?.witness;
      assert.equal(
        result.outcome,
        connected,
        "Disabled production guard must reach initialized read.",
      );
      assert.equal(result.seededWork, true);
      assert.equal(result.stateSha256, baseline?.stateSha256);
      assert.notEqual(result.transportSha256, baseline?.transportSha256);
      assert.notEqual(result.publicationSha256, baseline?.publicationSha256);
      assert.equal(result.transportSha256, control.primary.transportSha256);
      assert.equal(result.publicationSha256, control.primary.publicationSha256);
      assert.equal(result.runtimeVersion, original.primary.runtimeVersion);
      assert.deepEqual(result.runtimeFiles, original.primary.runtimeFiles);
      verifyProbe(result);
    }
  }
  assert.throws(() => verifyRevocationMatrix(mutant), /Revocation boundary/u);
}
/** @param {import("./browser-trust-qualification.mjs").Compose} compose */
export function revocationQualification(compose) {
  compose(["build", "transport-qualification", "transport-negative-control"]);
  compose([
    "run",
    "--rm",
    "--no-deps",
    "--entrypoint",
    "node",
    "configure",
    "/source/eng/operations/revocation-fixture.mjs",
    "issue",
  ]);
  compose(["stop", "web"]);
  const production = matrix(compose, "transport-qualification");
  verifyRevocationMatrix(production);
  verifyRuntime(compose, production);
  const mutant = matrix(compose, "transport-negative-control");
  verifyDisabledGuard(production, mutant);
  // Repeat the enabled healthy path after the independent mutant and restored evidence.
  const restoredPrimary = probe(compose, "transport-qualification", "primary");
  const restoredWitness = probe(compose, "transport-qualification", "witness");
  assert.deepEqual(restoredPrimary, production[0]?.primary);
  assert.deepEqual(restoredWitness, production[0]?.witness);
  compose(["up", "--detach", "--wait", "web"]);
  return { production, mutant, disabledGuardDetected: true, readStatePreserved: true };
}
