# Complete client workflows design

## Evidence and scope

The browser editor freezes an exposed draft, rotates identity only after a definite refusal or
explicit edit, binds consent to a preparation instance, and coalesces prepare/submit dispatch.
The CLI preserves authored bytes, acquires process-local credentials, carries whole-request
deadlines, and classifies uncertain dispatch independently of business rejection. Keep these
mechanisms and the core-owned revision, grant, and recovery decisions.

Tracing OPEN through Return to case exposes a wrong destination: Dashboard derives the reference
from its pre-command current case, which is absent for OPEN. Navigation also changes the selected
case while an editor remains mounted. Keep navigation and logout unavailable during an editor;
the existing explicit Back action ends editing. Return using the accepted receipt's reference and
read current state again rather than treating the receipt as current permission.

A lost submit leaves the entire editor locked, including its route to Recovery, and retains private
form/review state contrary to the documented delivery boundary. Replace that editor with Recovery,
passing only operation ID, request digest, and a recovery notice. Clear the selected case. The
Recovery view reads the exact identity independently of pending-list membership; accepted work can
already have moved to the terminal list. Never automatically resolve, dismiss, or resubmit.

Generic core faults recommending RECOVER_EXACT and completed executions with unconfirmed settlement
must remain uncertain in the browser just as in CLI classification. Acceptance evidence remains
definite acceptance, even when witness settlement still needs recovery. Do not hide an accepted
receipt behind a generic error or manufacture a new command identity.

Operation lookup permits changing its input while a read is pending. Bind feedback to the captured
input, cancel obsolete reads, coalesce repeated observation, and discard completions after input
change or unmount. Recovery inspection similarly needs cancellation and stale-result rejection;
clear an old selection before inspecting another. Closing details must cancel an outstanding
attempt-page read, preventing a closed dialog from reopening.

After a recovery mutation, invalidate the inspected authority/actions before reloading. An uncertain
resolve/dismiss/retain must direct fresh inspection, rather than leave an old Resolve button usable.
Serialize recovery reads/import previews with mutation actions so an unrelated late read cannot
clear a mutation's busy state. Keep mutations nonabortable after dispatch.

## Alternatives and dependents

Do not add browser persistence of claimant drafts or recovery bytes, a second business state machine,
automatic mutation retries, or a routing framework. Existing Dashboard state, typed callbacks,
AbortController for reads, and the existing recovery admission guard suffice. Canonical input remains
independent of locale: dates, amounts and command tokens are authored exactly; presentation only
formats accepted values. Preserve accessible modal confirmation and language-switch consent.

Update component and race tests, the published browser lifecycle and generated test inventories,
current Web documentation and Unreleased outcomes. The service contract and database baseline need
no change. Complete frontend gates, native suites, published CLI/browser checks and authoritative CI
must verify the delivered revision; source inspection alone is not workflow evidence.

## Session completion ordering

Repeated Try again can start overlapping session snapshots. Completion order currently decides the
active session, allowing an older response to replace newer knowledge. Give each refresh/logout a
monotonic request serial and apply only the latest active completion; invalidate reads on unmount.
Key the authenticated Dashboard by session epoch so newly established session knowledge cannot
reuse another epoch's claimant-bearing subtree. This introduces no polling or automatic login.

## CLI nested fault classification

PrepareOutcome.PrepareFailed and failed-before-attempt submission/resolution can carry a CoreFault
whose explicit direction is RECOVER_EXACT. CliRemoteWireCodec currently checks only a direct fault
inside FAILED, missing these nested shapes. Read the exact typed fault member and apply that direction
to FAILED and FAILED_BEFORE_ATTEMPT; retain exit 3 for safe technical failures and exit 4 for uncertain
settlement. The fix changes response classification, not JSON framing or server behavior.

## Recovery dispatch and settlement knowledge

Dashboard currently receives mutation locks only from OperationEditor. Recovery resolve, dismiss,
retain and witnessed export need the same navigation/logout lock through their nonabortable
dispatch. Release it after the classified response; a missing response keeps exact identity in a
nonpayload handoff. Use the existing RecoveryTarget callback for resolve/dismiss and import retain,
including uncertain failures that leave an empty pending list. Reads/previews remain cancelable.
Show known acceptance separately from unconfirmed settlement: a receipt is proof of acceptance,
while an explicit recovery notice still directs inspection of the unsettled evidence.

## Bounded browser response waits

The browser's request/export paths currently await fetch and body delivery indefinitely; existing
pending-request tests demonstrate the resulting permanent dispatch lock. Apply a fixed twenty-second
response deadline to the whole asynchronous transport/validation result. Promise.race bounds client
knowledge without aborting a dispatched mutation or retrying it. A late result cannot replace the
already-returned uncertain result. Clear timers after any completed request. Use one transport helper
for JSON and export responses, a localized timeout notice, and the existing preparation retry and
submission/recovery handoffs. Keep cancellation of ordinary reads under their existing callers.
