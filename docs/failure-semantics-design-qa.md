# Failure semantics design challenge

This separate pass checks whether the proposed advice can authorize unsafe repetition.

- Admission producers run before core dispatch: connection/origin/media/session/antiforgery checks,
  endpoint fallback and bounded body/input decoding. Definition's session-forbidden projection follows
  a read-only definition check and cannot dispatch a mutation. Body-too-large is not post-dispatch.
  Their typed `NOT_STARTED` claim applies only to this request, never previous attempts.
- The export route marks completion before validating attachment metadata. Issuance is witnessed
  work even though the endpoint returns a recovery query outcome. Null phase must remain conservative
  for export, and for ordinary completed-response failure. Do not infer acceptance from either.
- The same dispatch marker covers reads. Mutation uncertainty requires both possible dispatch and
  a state-changing endpoint. Keep the catalog default conservative for unclassified endpoints.
- A `RECOVER_EXACT` rejection can deny a new action while an older submission remains unresolved.
  Include direct refusal nesting in both clients and give the classifier an ordinary-refusal
  negative control. Definite execution rejections currently cannot carry `RECOVER_EXACT`; do not
  add a branch for that unsupported combination. Confirmed acceptance remains distinct from
  acceptance with unconfirmed settlement.
- Revoked authority and attempt exhaustion do not prove historical nonexecution. Presentation must
  direct exact evidence review without promising that replay is authorized or that new work is safe.
  Keep current typed action policy; do not recast every revoked result as an uncertain new attempt.
- Explicit cancellation notices describe this request's boundary only. Cancellation before a new
  attempt must not erase prior uncertainty, and accepted receipts retain their acceptance presentation
  when settlement is unconfirmed. No browser request timeout becomes a cancellation outcome.
- Existing administration progress marks commit before calling it and retains confirmed completion
  across cleanup failure. HTTP partial responses abort, and CLI lost mutation delivery exits 4.
  Preserve these mechanisms rather than replace them with a generic exception-to-retry rule.

Accepted with these constraints. Test expected phases and exits as independent literal tables,
exercise actual HTTP export refusal, and reject contradictory/old host shapes through generated
validators. Match all delivery artifacts to the revised lock; no persisted format or migration changes.
