# Product scope design QA

This separate skeptical pass challenges the [design](product-scope-design.md).

1. **Small business scope does not make custody unnecessary.** A thirteen-field register can still
   disclose personal data, acknowledge an operation or leave backups after purge. Check the actual
   authority, recovery and erasure contracts before deleting a mechanism. The main mechanisms
   protect distinct required effects; their removal is not justified by field count.
2. **Conditional support is not deployed qualification.** Source-preview scope and an absent
   reviewed deployment root are explicit. Keep rejection of real-data initialization and full
   qualification; a test root, source merge or owner-selected file cannot create publication trust.
   `ReviewedDeploymentRoot.current` already validates the key and policy together, disproving a
   suspected split-configuration defect. No additional capability registry is needed.
3. **A report is not readiness.** The report parser already requires `realDataReady=false` for both
   supported scopes. Removing the internal field must retain that check and exact canonical bytes.
   Forge true on otherwise valid reports and require refusal, alongside valid false counterparts.
4. **Successful qualification must still be earned.** The owner's private qualification path checks
   the pinned publication, current signed independent proof, local primary/witness/WAL evidence,
   exact identities and expiry before returning success. Only that result may emit true. Keep the
   independently verified proof's scope/readiness guards and the separate activation recheck.
5. **Historical proof does not obtain a fresh lease.** The ignored clock argument cannot contribute
   evidence. Removing its query must leave original signed-time validation, current owner identity
   and exact witnessed historical reconciliation intact. Existing expired-current versus valid-
   historical signed-observation controls exercise the distinction.
6. **One root does not mean weaker verification.** Both production helper callers use the one
   source-pinned root; direct `verifyWithRoot` methods exist for explicit cryptographic qualification.
   Keep those testable verifiers and all signature/topology/source binding checks. On every loaded
   evidence outcome, dispose its buffers and clear the acquired root; never try another trust root.
7. **Opacity is not free simplification.** A native private-record probe prevents construction but
   also hides record getters. Introducing extra wrappers or projection types merely to remove
   independently verified proof guards would add concepts and weaken review. Retain those guards.
8. **Published interfaces matter.** v0.6.0 is public. The chosen changes affect only internal
   representations and helper arguments; public readiness fields and refusal behavior remain.
   Verify unchanged generated contracts and canonical report output rather than assume it.

The design is approved as an agent design decision. Required complete verification and the user's
explicit merge authorization remain separate from that decision and from production certification.
