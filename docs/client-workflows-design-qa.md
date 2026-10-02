# Complete client workflows design challenge

This separate pass checks the proposed transitions against current codecs, fixtures and callers
before implementation.

- **A receipt is historical.** Use its reference only as navigation target; CaseDetail rereads current
  state, history and lifecycle. OPEN, exact accepted replay and ordinary commands share this rule.
- **Uncertainty is not failure.** CliRemoteWireCodec checks RECOVER_EXACT inside generic faults;
  browser classification must inspect typed fault data, including nested execution faults. A known
  accepted receipt still proves acceptance even if settlement is unconfirmed. Recovery must not
  display stale mutation actions after either result.
- **Pending-list membership is not identity.** A dropped submit may already be accepted; direct
  inspection must work even if the pending list is empty. A pruned accepted preparation can be
  absent from inspection, so retain the exact ID visibly for Operations observation. Do not imply
  that a missing preparation proves failure.
- **Preparation loss differs from submit loss.** Unknown preparation retains frozen exact bytes for
  the existing explicit prepare retry. Do not clear or rebase that request. Submit follows a retained
  preparation and can move to durable Recovery without persisting claimant material in the browser.
- **Navigation must have one meaning.** Disabling navigation while editing preserves drafts and
  avoids silent case-context changes. Back explicitly ends editing. Recovery replaces an uncertain
  editor; it is not an implicit discard of a mutable draft.
- **Closing a dialog is not permission for a late result to reopen it.** Abort inspection reads on
  replacement, close and unmount, and check the signal before applying results. Serialize previews
  and mutations using the existing synchronous admission guard; rendered disabled buttons alone are not
  enough against repeated actions in one render.
- **A definite recovery refusal can still make old inspection stale.** Invalidate selected authority
  after every resolution/dismissal response, not only successful mutations. Require a new inspection before
  another confirmed action. Preserve explicit notices and exact identities under reconnects.
- **Session and presentation are separate.** Session read/refusal tests already distinguish
  request-local disclosure refusal from delivery loss, and logout clears the authenticated subtree.
  Exercise expiry at the published boundary; do not turn a transient network failure into an
  invented logout. Existing canonical-date/decimal and language-state tests remain required.
- **Assurance must detect the original failures.** Add reverse-order read and close-before-response
  tests, a dropped-response handoff test with an empty pending list, a no-second-mutation assertion,
  and an OPEN return-target assertion. Run all registered tests without filters or retries and keep
  generated inventories synchronized. Do not add a new contract or relax coverage/size policy.

The design is accepted with the pruned-preparation fallback clarified above. No storage, service
schema, authentication policy or business transition change is required.

The session-ordering follow-up is accepted after checking App's explicit Try again branch and
useSession's public refresh/logout calls. Reverse-order responses must retain the newest snapshot;
a refresh started before logout must not resurrect the authenticated subtree. Epoch remount is
deliberate clearing, not persistence of drafts across authentication. Transient case-read delivery
still does not invent a session expiry or trigger a new login.

CLI classification is challenged against WebWireCodec's actual prepare, submission and resolution
renderers and the core's retained-review paths. A failed-before-attempt response can describe
uncertainty from an earlier operation; its label does not erase that knowledge. Qualify the nested
RECOVER_EXACT fault with real typed wire encodings and safe-retry negative controls. Keep explicit
definite execution and accepted-receipt classification intact, and verify generated corpus lock
identity after the change.

The recovery dispatch follow-up is accepted after tracing Dashboard's only existing lock callback
to OperationEditor and confirming recovery export issues witnessed custody evidence. Lock only
dispatched mutations, preserve coalescing before rerender, and release after outcome classification.
Unknown resolve/dismiss/retain must keep their own ID/digest visibly, rather than rely on pending-list
membership. Known acceptance with unconfirmed settlement must display both facts; it cannot be
turned into a new editable draft or a claim that witness evidence is fully settled.

The browser deadline is accepted after examining request and exportResponse body awaits and the
existing never-settling mock transports. A transport timeout must remain a delivery failure;
isMutationUncertain classifies it conservatively. Do not pass a new abort signal to mutation fetch,
cancel server execution, recreate an ID, or use a timeout as a rejection. Test stalled requests, a
late valid result, normal completion with timer cleanup and export waiting. Twenty seconds is client
response-wait policy, not a promised maximum server execution time or a production resilience claim.

Final classifier review also checks a completed failed execution carrying an explicit RECOVER_EXACT
fault. A definite current attempt failure does not erase earlier uncertainty. Apply the same fault
direction in CLI classification as in the browser, while preserving accepted and rejected execution
tags and unconfirmed settlement. The added codec case proves classification of a typed wire shape;
it does not claim the test store produced a real unknown commit.
