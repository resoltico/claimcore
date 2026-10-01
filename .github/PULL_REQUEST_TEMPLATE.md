## Scope and impact

Describe the change, intentional breaks and preserved authority. Identify changes to architecture,
security, contracts, workflow/gate policy, dependencies or release controls explicitly.

## Design and QA

Link the design and separate QA record. Distinguish agent/source review from owner authorization
(see `docs/owner-review.md`). Name any change to a contract (`config/contracts.lock.json`), to the test
inventories (`tests/inventory/`) or to a workflow or gate, because those are what the owner reviews.
Do not pre-check owner approval on the owner's behalf: neither CI nor a source review can grant it, and
the owner decides on the current head/base before a manual merge.

## Verification and handoff

Record the actual published head and link its qualifying run/attempt. State actual commands/results,
CI status, draft/readiness, limitations and any live-setting activation still pending. Update this
section after follow-up commits; do not leave a superseded head or a “pending” claim after verification.

## Publication hygiene

Identify the feature branch. Disclose temporary infrastructure and preparation commits, and confirm
what was removed from the submitted tree. Do not conflate opening this PR with approval or merge.
