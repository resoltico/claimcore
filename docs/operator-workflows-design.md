# Operator workflows and independent qualification

## Findings and chosen changes

Fresh initialization, first-owner provisioning, W1/A1–A3/W2 handoff, copy retention, live purge,
witness pruning and terminal loss already keep distinct evidence and outcomes. Preserve those
boundaries, current authority checks, quarantine and the generic build's absent publication root.
The owner runbook needs one concise interrupted-procedure map that names existing reconciliation
commands and the limits of verification instead of leaving operators to infer a safe retry.
The parsed owner prune-witness-payload command is absent from executable help; expose that
separate erasure phase in database-free discovery and regenerate its owned help block.
Initial-owner provisioning has no caller-selected event identity and refuses all repeats. Allow
the same private principal file to read the original revision-one provisioning event only after
its exact canonical action and witness evidence agree. Return its original receipt without
creating an actor, advancing authority, re-enabling the principal or restoring a grant. A different
principal, missing primary event, or witness-only unknown intent still refuses and stays quarantined.

The backup producer catches ordinary exceptions after FINISH, but SIGINT raises KeyboardInterrupt
and currently escapes as REFUSED. It also omits the HELD lease ID from uncertain diagnostics even
though reconcile-backup-capture requires that ID. Catch interrupts at the same phase boundary,
retain the validated opaque lease ID in the uncertainty outcome and successful capture response,
and preserve the exact files. Before FINISH, abort on interrupt; a killed producer's lease-named
archive directory remains the recovery locator. Do not expose paths, nonce, credentials or payload.
Once SEALED and OBSERVED have returned, capture is known even if process cleanup or stdout delivery
fails. Share the command result/diagnostic rendering between the real and synthetic producer:
retain CAPTURED_UNVERIFIED, the receipt ID and lease ID in the failure diagnostic with nonzero exit.

The CHECKPOINT signer syncs ledger content but not its directory before returning a signature.
Sync the existing record's parent before every successful first response or exact replay. A partial
record stays a refusal; no automatic repair or signing a changed cycle is allowed. Apply the same
directory durability to create-only signed deployment aggregate outputs.
Replay must also validate the exact ledger shape/cycle and verify its saved signature against the
original candidate and configured public key before reporting SIGNED. A damaged signature or
replaced key refuses instead of being left for a downstream consumer to discover.
Exact CHECKPOINT replay currently rechecks new-signing freshness before consulting its ledger,
so delayed recovery cannot retrieve a previously issued signature. Validate identity and canonical
bytes first, then require freshness only for a new ledger entry. A historical replay retains the
original candidate and signature and cannot renew its time or the owner's capture lease.

Key separation compares PEM bytes in backup signer, promotion and independent-host topology paths.
An experiment accepted one Ed25519 key for primary and witness under different PEM line wrapping.
Keep exact file-hash pins as integrity commitments; separately compare parsed raw Ed25519 public
keys for separation. Enforce the seven-key set (five role observers, fence observer, aggregate
signer) in both Python aggregate verification and native product consumption, and the six-observer
set during topology qualification. Use existing OpenSSL and native DER parsing; no new dependency,
format version, migration or alternate trust root. Apply raw identity to COPY_ATTESTOR/CHECKPOINT
and two-owner promotion checks as well.

The producer also admits 32–4096-byte or zero commitment keys and noncanonical/nil key IDs,
while the native custody reader requires a nonzero raw 32-byte installation key and canonical
opaque identities. Refuse those inputs before capture and signing. Keep the existing valid format
and demonstrate both valid configuration and these negative controls through the real loader.
Preserve the common signer's bounded refusal categories in the managed tool's diagnostics. The
local job selector must also include executable backup-tool sources; otherwise a Python-only
operator fix can skip its required qualification despite being executed by those tests.

## Verification and operational effects

Regression controls must reject differently wrapped copies of the same key, interrupted FINISH
and OBSERVE, and a signer directory-sync failure followed by exact retry. Retain positive distinct
keys and exact replay. Exercise actual filesystem/signature and published owner boundaries with
synthetic data. Update the current owner runbooks, named contract inventories and Unreleased notes.
Same-host tests still cannot establish independent human, host, storage or administrator custody.
