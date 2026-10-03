# Operator workflow design challenge

This separate pass challenges the design against concrete interrupted procedures.

- An uncertain FINISH response can precede a sealed receipt; an interrupt during OBSERVE has the
  same knowledge. Both must retain the original validated lease ID and files, never issue ABORT or
  call the action refused. Pre-FINISH interruption may abort but must not delete captured files.
  SIGKILL cannot deliver a diagnostic; the owner-created lease directory is the documented locator.
  Conversely, a stdout failure after OBSERVED cannot erase known completion. The failure body must
  retain CAPTURED_UNVERIFIED and the same receipt/lease IDs; empty output still requires readback.
  The real closed-pipe control also exposed Python shutdown re-flushing failed stdout and adding
  unstructured diagnostics. Retire the failed descriptor before raising the typed completion
  error, so shutdown cannot retry blocked delivery or append a second traceback.
- Merely syncing a newly written ledger once is insufficient: if its directory sync fails and an
  exact retry uses the existing-record branch, that retry must sync before acknowledging too.
  Damaged or divergent records remain quarantined; no overwrite-based recovery is introduced.
  Returning a previously recorded signature after its issuance window is historical readback,
  not a new signing permission. Missing-ledger expired candidates must still refuse; a replay
  cannot change capturedAt, cycle bytes, signing identity or the native owner's lease deadline.
  Verify saved signature bytes too: matching a stored candidate hash does not make a corrupted
  signature or replaced public key a valid SIGNED response. Never repair the ledger automatically.
- File hashes remain meaningful for binding exact signed topology bytes. Replacing them with a
  semantic key hash would change persisted/public commitments unnecessarily. Add raw key identity
  only to independence checks; challenge both issuance and native consumption so tooling cannot
  hide one key behind two root-pinned PEM encodings.
- Distinct keys do not prove distinct humans or machines. Preserve the existing signed role,
  administrator, host/storage and old-writer fence evidence; the generic build still refuses real
  qualification without its reviewed root and live proof. Do not invent an observer issuer.
- A runbook must distinguish schema verification, full pair audit, historical exact readback and
  current cutover authority. A green verify-data after a lost initial-owner response cannot prove
  that the submitted principal was provisioned. Same-principal provisioning replay must verify the
  original canonical principal, actor row and witnessed event under the authority lock, and must
  still return only historical acceptance after later disable/revocation. A rolled-back primary
  cannot substitute a new principal or reconstruct a missing first event from witness intent.
- Early backup configuration must agree with the downstream custody reader. An experiment accepted
  a 64-byte key that native custody cannot load; rejecting it only after encrypted capture wastes
  work and can leave unusable signed evidence. Require the same nonzero raw 32-byte material and
  canonical non-nil IDs before any capture, retaining valid existing configuration semantics.
- Check delivery into gates too: the local selector was experimentally false for a backup-only
  Python change. Add that dependency to its existing registry and a positive/negative scope
  control; keep documentation-only changes out of the expensive PostgreSQL job.

Accepted with these constraints. Keep existing formats and exact retry identities, use isolated
synthetic verification, run local gates before publishing the PR, and merge only its verified head.
