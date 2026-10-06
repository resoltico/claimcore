import { randomBytes, randomUUID } from "node:crypto";
import { join } from "node:path";
import { jsonFile, privateFile } from "./local-files.mjs";

const material = () => randomBytes(32).toString("base64");

/** @param {string} directory @param {number} uid @param {number} gid */
export function runtimeKeys(directory, uid, gid) {
  const witnessId = randomUUID();
  jsonFile(
    directory,
    "witness-key.json",
    { version: 1, activeKeyId: witnessId, keys: [{ id: witnessId, materialBase64: material() }] },
    uid,
    gid,
  );
  privateFile(join(directory, "writer.capability"), randomBytes(32), uid, gid);
  jsonFile(
    directory,
    "suppression-key.json",
    { version: 1, keyId: randomUUID(), materialBase64: material() },
    uid,
    gid,
  );
  const artifactId = randomUUID();
  const now = new Date();
  const end = new Date(now);
  end.setUTCFullYear(end.getUTCFullYear() + 1);
  const verify = new Date(end.getTime() + 3600000);
  jsonFile(
    directory,
    "recovery-artifact-key.json",
    {
      version: 1,
      activeKeyId: artifactId,
      artifactLifetimeSeconds: 3600,
      keys: [
        {
          id: artifactId,
          encryptionBase64: material(),
          macBase64: material(),
          issueFrom: now.toISOString(),
          issueUntil: end.toISOString(),
          verifyUntil: verify.toISOString(),
          maximumExports: 65536,
        },
      ],
    },
    uid,
    gid,
  );
}

/** @param {string} issuer */
export function webEnvironment(issuer) {
  return {
    CLAIMCORE_WEB_ORIGIN: "https://app.localhost:5443",
    CLAIMCORE_WEB_LISTEN_ADDRESS: "0.0.0.0",
    CLAIMCORE_WEB_LISTEN_PORT: "5443",
    CLAIMCORE_WEB_STATE_DIR: "/var/lib/claimcore/web",
    CLAIMCORE_WEB_CERTIFICATE_PATH: "/etc/claimcore/web.pfx",
    CLAIMCORE_CONNECTION_FILE: "/etc/claimcore/primary.connection",
    CLAIMCORE_WITNESS_CONNECTION_FILE: "/etc/claimcore/witness.connection",
    CLAIMCORE_WITNESS_KEY_FILE: "/etc/claimcore/witness-key.json",
    CLAIMCORE_WRITER_CAPABILITY_FILE: "/etc/claimcore/writer.capability",
    CLAIMCORE_SUPPRESSION_KEY_FILE: "/etc/claimcore/suppression-key.json",
    CLAIMCORE_RECOVERY_ARTIFACT_KEY_FILE: "/etc/claimcore/recovery-artifact-key.json",
    CLAIMCORE_OIDC_ISSUER: issuer,
    CLAIMCORE_OIDC_CA_CERT_FILE: "/etc/claimcore/ca.pem",
    CLAIMCORE_WEB_PROBE_CA_CERT_FILE: "/etc/claimcore/ca.pem",
    CLAIMCORE_OIDC_CLIENT_ID: "claimcore-web",
    CLAIMCORE_OIDC_CLIENT_SECRET_FILE: "/etc/claimcore/oidc-client.secret",
    CLAIMCORE_OIDC_API_AUDIENCE: "claimcore-api",
    CLAIMCORE_OIDC_CLI_CLIENT_ID: "claimcore-cli",
    CLAIMCORE_OIDC_SERVICE_CLIENT_ID: "claimcore-service",
  };
}
