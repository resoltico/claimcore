# Product scope and justified complexity

Audit base: `1616b47ba2e10deeb51415c9257a17ad30091131` (published v0.6.0).
The [domain](domain.md), [architecture](architecture.md) and [operations](operations.md) own the
requirements. This review checks mechanisms against those requirements, including alternatives.

## Requirements and retained mechanisms

| Mechanism | Required property and decision |
| --- | --- |
| Actor authority | Individual authenticated principals, explicit default-deny grants and current resource disclosure checks. A database role or installation-owner role cannot substitute for case authority. Retain the actor-bound facade and locked grant checks. |
| Independent witness | Primary rows alone cannot establish accepted authority or freshness. Retain ordered evidence, exact settlement, independent custody and read fences. Removing the witness or treating a local pair as independent would weaken the stated guarantee. |
| Recovery | Preserve exact operation bytes and identity; acceptance, future authority and earlier attempt knowledge differ. Retain admitted attempts, definite settlements, revocation and paged inspection. Automatic rebasing or an inferred rollback would destroy required evidence. |
| Copy custody | One copy's verified absence cannot establish all-known-copy absence. Retain signed inventory, distinct verifier/approval roles, retention and holds. A general deleted flag cannot replace purpose-bound copy evidence. |
| Privacy | Voiding preserves history; live purge, witness-payload prune, copy absence and the suppression horizon have different effects and preconditions. Retain those phases and conservative unknown-copy handling. They are not extra business fields. |
| Installation and deployment | The generic release supports synthetic evaluation on macOS/Linux with a loopback service. Real-data activation and independent-host qualification require an operator-reviewed source-pinned root and policy plus their independent evidence. Keep the generic refusal and the published conditional contracts; do not add a production configuration switch or claim that source-preview CI supplies independent evidence. |

These guarantees justify the main mechanisms despite the small thirteen-field business record.
An event-only primary, one generic approval, or a single erase operation would simplify names while
shifting uncertainty, custody and irreversible-deletion risk onto callers. Those alternatives do
not meet the supported contracts. No additional workflow framework or deployment mode is needed.

## Concrete simplification

- `RestoreReportClaims.RealDataReady` is always false: its parser rejects true, its live producer
  supplies false, and canonical output is an intermediate report. Remove that internal field and
  emit the required false wire value at the report boundary. Keep rejection of forged readiness.
- `FencedTailVerification.RealDataReady` mixes local evidence with qualification. Local construction
  writes false; the owner recheck changes it to true only after independently verified host evidence
  matches. Remove the field. The private owner qualification result already represents success;
  its successful response emits true, while every refusal emits false. Activation still rechecks
  the exact signed candidate through its separate verifier.
- `historicalDigest` ignores its database-clock argument; its only caller performs a clock query
  solely for that argument. Remove both. Historical verification uses signed issuance time to
  reconstruct the original candidate; it grants no new freshness or W2 authority.
- `withRoots` accepts a list and tries alternative roots, although both callers derive exactly
  zero or one root from the single reviewed deployment profile. Replace the unused multiple-root
  machinery with one reviewed-root operation. Preserve missing-root refusal, evidence disposal
  and root-buffer cleanup on success and failure.

Keep the scoped readiness checks on independently verified host proofs: they protect a different
boundary and are not removed merely because the current issuer emits one value. Keep the public
recovery artifact kind and wire identifiers introduced by v0.6.0; removing them would impose a
compatibility break without simplifying the required import behavior.

## Delivery and evidence

No public payload, format, baseline, runtime option, dependency or protection is changed. Update
internal producers, consumers and fixtures together. Add a negative control for forged intermediate
readiness, retain real signed-proof and physical restore/activation qualifications, and regenerate
the affected inventory and contract-test map through their owners. The separate
[design QA](product-scope-design-qa.md) challenges these decisions before implementation.
