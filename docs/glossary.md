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

Payment is Undecided, Decided or Paid. Every Paid value includes its decision. Decision and payment
appear as complete groups in the public business record.

## Ledger position

A sequence number and entry hash identify an accepted ledger position. The hash has 32 bytes and is
shown as hexadecimal at boundaries. Genesis is sequence zero with the zero hash.

## Entry target

The case or authority object named by an accepted ledger entry. Its public wire member is `subject`.
