import { operationId } from "./v3-ui.fixtures";

/** Independent structural envelope fixture; no signature-verification claim. */
export const recoveryArtifact = {
  format: "claimcore-recovery-artifact",
  formatVersion: 3,
  keyId: operationId,
  exportId: operationId,
  installationId: operationId,
  epoch: 1,
  caseId: operationId,
  preparerActorId: operationId,
  preparerGrantRevision: 0,
  importerActorId: null,
  exporterActorId: operationId,
  exporterGrantRevision: 0,
  operationId,
  issuedAt: "2026-09-07T12:00:00.0000000+00:00",
  expiresAt: "2026-09-08T12:00:00.0000000+00:00",
  canonicalCommandFormat: 3,
  requestFingerprintVersion: 1,
  nonceBase64: "A".repeat(16),
  ciphertextBase64: "AAAA",
  tagBase64: "A".repeat(24),
  macSha256: "a".repeat(64),
};
