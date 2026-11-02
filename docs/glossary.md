# Glossary

## Principal and actor

A principal is an exact issuer and immutable human subject or service client identity. An actor is its
registered identifier and enabled state. Current grants authorize actions on an installation or case.
Principal kinds and roles have declared vocabularies. Identity and scope values are admitted before use;
malformed retained identities or role spellings are integrity faults.

## Admission and permanent format

Admission applies current rules to newly asserted values. Permanent format describes what a retained
value can represent. Reading retained values uses permanent format.

## Case act

A requested change from the [case-act declaration](../src/ClaimCore.Domain/CaseActs.fs), including business
work and stewardship. Availability depends on current case state, the supplied values and the actor's rights.

## Decision

An accepted payment decision records its date, payable amount and currency.

## Public outcome

A public result distinguishes success, refusal and failure. Execution further distinguishes acceptance,
replayed acceptance and an unknown outcome. An unknown commit or delivery needs resolution before a
client can treat the operation as accepted or not accepted.

## Input issues

A nonempty ordered collection of targeted violations from independent checks in one admission phase.
Targets include business fields, identity inputs and correction groups; one target may have several
distinct issues. The wire carries `INVALID_INPUT.issues`; the CLI and browser present every issue.
Current-state and business-date admission follow parsing. A non-input refusal reports one state or
transition cause.

## Payment progress

Progress is Undecided, Decided or Paid. Every Paid value retains its complete decision and payment
record. Both appear as complete groups in the public business record.

## Ledger position

A sequence number and entry hash identify a committed ledger position, including recovery controls.
The hash has 32 bytes and is shown as hexadecimal at boundaries. Genesis is sequence zero with the zero hash.

## Entry target

A ledger entry names a case, an authority target or an actor-scoped final-nonacceptance control.
Public acceptance receipts expose case/authority targets as `subject`; recovery controls have no business
acceptance receipt and do not appear in case or authority history.

## Case identity and reference

A case's stable CaseId is its accepted OPEN operation UUID. Its original reference is immutable,
ASCII-case-insensitively unique, retained after erasure and never reused. Tagged ID/reference selectors
make UUID-shaped references unambiguous. See [Integration](integrating.md) for the wire contract.

## Recovery attempt

An identity-only client record scoped to service origin, installation, actor and local attempt. It must
commit before mutation handoff. It contains no business request or credentials; restart recovery reads
actor-scoped status or deliberately settles the operation instead of reconstructing a submission. See [Recovery](integrating.md#delivery-recovery).

## Final nonacceptance

A committed actor/operation control promises that this pair cannot later be accepted in the supported
state. Repeated resolution appends no second control; another actor's same raw operation UUID is not
consumed. Controls advance the head/checkpoint and reconstruct fence projections.

## Passive operation status

An enabled valid registered actor's read of its own accepted metadata, previously committed final
nonacceptance, or unresolved outcome. It appends no control and does not block acceptance. Authorship
permits recovery metadata even without current case roles; content disclosure remains independently authorized.

## Responsibility and follow-up

Case work holds an optional responsible ActorId and planned follow-up date. Responsibility is a factual
assignment, independent of grants and visibility. My cases means that the requester is responsible;
Unassigned means no account is responsible. An unavailable assignee remains recorded until explicitly
changed. Due state uses the query snapshot's database instant and installation business zone; only active,
open cases are actionable.

## Targeted update and reaffirmation

A partial update changes selected facts while retaining omissions from locked state. A money assertion
always contains amount and currency together. Payment reaffirmation explicitly acknowledges the existing
payment when correcting a paid decision; it requires payment-recording authority and does not transfer
money. Read reviews do not authorize execution or replace the expected revision.
