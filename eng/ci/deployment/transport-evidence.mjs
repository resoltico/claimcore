import assert from "node:assert/strict";

export const connected = "CONNECTED_READ_VERIFIED";
export const refused = "TLS_AUTHENTICATION_REFUSED";
export const runtimeFiles = [
  "System.Net.Security.dll",
  "System.Security.Cryptography.dll",
  "libSystem.Security.Cryptography.Native.OpenSsl.so",
  "libssl.so.3",
  "libcrypto.so.3",
];
/** @typedef {{outcome: string, seededWork: boolean, phase: string, stateSha256: string, publicationSha256: string, transportSha256: string, runtimeVersion: string, runtimeFiles: Record<string, string>}} Probe */
/** @param {Probe} value */
export function verifyProbe(value) {
  assert.ok(value && typeof value === "object");
  assert.deepEqual(Object.keys(value).sort(), [
    "outcome",
    "phase",
    "publicationSha256",
    "runtimeFiles",
    "runtimeVersion",
    "seededWork",
    "stateSha256",
    "transportSha256",
  ]);
  assert.ok(
    [connected, refused].includes(value.outcome),
    "Unrelated failures cannot qualify TLS refusal.",
  );
  const success = value.outcome === connected;
  assert.equal(value.phase, success ? "READ" : "OPEN");
  assert.equal(value.seededWork, success);
  assert.ok(success ? /^[0-9a-f]{64}$/u.test(value.stateSha256) : value.stateSha256 === "");
  for (const field of ["publicationSha256", "transportSha256"]) {
    assert.match(Reflect.get(value, field), /^[0-9a-f]{64}$/u);
  }
  assert.match(value.runtimeVersion, /^[0-9]+\.[0-9]+\.[0-9]+$/u);
  assert.deepEqual(Object.keys(value.runtimeFiles).sort(), [...runtimeFiles].sort());
  for (const digest of Object.values(value.runtimeFiles)) {
    assert.match(digest, /^[0-9a-f]{64}$/u);
  }
  return value;
}
