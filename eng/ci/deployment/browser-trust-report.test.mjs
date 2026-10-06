import assert from "node:assert/strict";
import test from "node:test";
import { verifyBrowserTrustReport } from "./browser-trust-qualification.mjs";
const fingerprint = `${"AB:".repeat(31)}AB`;
const report = {
  scope: "CHROMIUM_LINUX_NSS",
  version: "159.0.1.2",
  publicCaSha256: fingerprint,
  untrustedRefused: true,
  trustedAuthentication: true,
  wrongHostnameRefused: true,
  expiredCertificateRefused: true,
  removedTrustRefused: true,
};
test("browser trust evidence requires the exact CA and every real-boundary result", () => {
  assert.equal(verifyBrowserTrustReport(report, fingerprint), report);
  for (const key of Object.keys(report)) {
    const missing = { ...report };
    Reflect.deleteProperty(missing, key);
    assert.throws(() => verifyBrowserTrustReport(missing, fingerprint));
  }
  for (const key of [
    "untrustedRefused",
    "trustedAuthentication",
    "wrongHostnameRefused",
    "expiredCertificateRefused",
    "removedTrustRefused",
  ]) {
    assert.throws(() => verifyBrowserTrustReport({ ...report, [key]: false }, fingerprint));
  }
  for (const changed of [
    { ...report, publicCaSha256: "foreign CA" },
    { ...report, scope: "MOCKED_VALIDATOR" },
    { ...report, version: "unverified" },
    { ...report, ignoreHTTPSErrors: true },
  ]) {
    assert.throws(() => verifyBrowserTrustReport(changed, fingerprint));
  }
});
