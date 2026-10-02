# Failure semantics and actionable diagnostics

## Findings and constraints

The request failure boundary records dispatch and returned-result knowledge. CLI classification
currently treats dispatched reads as uncertain mutations, but treats invalid export metadata after
witnessed issuance as a definite failure. Browser classification conservatively treats null phases
as uncertain, including known admission refusals. Use the existing host phase contract rather than
another diagnostic-ID classification table.

Client recovery classification checks nested faults but omits nested rejections carrying
`RECOVER_EXACT`. A refusal to start another attempt does not settle an earlier attempt. Revocation
presentation says "before execution", contrary to the retained historical uncertainty contract.
Attempt-limit guidance must require exact evidence review before new work. Cancellation outcomes
currently fall through to a generic incomplete notice.

## Chosen design

Declare `NOT_STARTED` for every typed admission/input refusal. Retain null phase for returned-result
delivery failure and invalid export metadata: completion of processing is not a delivered acceptance
or rollback result. CLI host failures exit 4 only for a mutating endpoint without `NOT_STARTED`;
read-only failures exit 3. Preserve the default-mutation endpoint catalog and strict correlated schemas.

Both clients must honor `RECOVER_EXACT` in direct and nested refusal payloads as well as faults.
Keep rejection, cancellation,
accepted receipts and unresolved settlement as separate outcomes; do not rewrite Application rules
or automatically replay work. Render cancellation explicitly and qualify it as applying to this
request, without settling earlier attempts. Correct revocation and attempt-limit explanations in
native presentation and all three browser languages.

## Alternatives and dependents

An additional phase or generalized diagnostic/action framework would add a concept without new
knowledge. An ID whitelist in CLI would duplicate host policy and miss future post-dispatch causes.
Removing null phases would falsely claim either non-dispatch or a known mutation result.

Regenerate the correlated contract lock, localization outputs and affected test inventories. Update
the diagnostic owner document and Unreleased outcomes. Verify actual host failure bytes through CLI
classification, the export HTTP guard, strict browser admission, nested recovery guidance and distinct
cancellation presentation. Existing transaction, cancellation, administration checkpoint and partial
response protections remain required; source inspection is separate from execution evidence.
