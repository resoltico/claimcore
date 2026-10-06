import { readFixture, trustStore } from "./browser-trust-store.mjs";
import { authenticate, forward, refused } from "./browser-trust-navigation.mjs";

const origins = ["https://app.localhost:5443/", "https://identity.localhost:5444/"];
let stage = "CONFIGURATION";
async function qualify() {
  const fixture = readFixture();
  const store = trustStore(fixture.ca);
  const close = await Promise.all([
    forward(5443, fixture.web),
    forward(5444, fixture.identity),
    forward(5445, fixture.expiry),
  ]);
  try {
    stage = "UNTRUSTED";
    for (const origin of origins) {
      await refused(origin, "ERR_CERT_AUTHORITY_INVALID");
    }
    stage = "TRUSTED_AUTHENTICATION";
    store.import();
    const version = await authenticate(fixture.password);
    stage = "WRONG_HOST";
    await refused("https://wrong.localhost:5443/", "ERR_CERT_COMMON_NAME_INVALID");
    await refused("https://wrong.localhost:5444/", "ERR_CERT_COMMON_NAME_INVALID");
    stage = "EXPIRED_CERTIFICATE";
    await refused("https://app.localhost:5445/", "ERR_CERT_DATE_INVALID");
    stage = "REMOVED_TRUST";
    store.remove();
    for (const origin of origins) {
      await refused(origin, "ERR_CERT_AUTHORITY_INVALID");
    }
    process.stdout.write(
      `${JSON.stringify({
        scope: "CHROMIUM_LINUX_NSS",
        expiredCertificateRefused: true,
        version,
        publicCaSha256: store.fingerprint,
        untrustedRefused: true,
        trustedAuthentication: true,
        wrongHostnameRefused: true,
        removedTrustRefused: true,
      })}\n`,
    );
  } finally {
    await Promise.all(close.map((dispose) => dispose()));
  }
}
await qualify().catch(() => {
  process.stderr.write(
    `${JSON.stringify({ diagnostic: "BROWSER_TRUST_QUALIFICATION_FAILED", stage })}\n`,
  );
  process.exitCode = 1;
});
