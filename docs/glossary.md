# Glossary

The machine-readable [glossary seeds](../src/ClaimCore.Protocol/Messages/Localization/glossary.json)
are checked against core and interface catalogs by the protocol generator. Use these terms consistently.

The interface supports English, Latvian, Arabic, Korean (South Korea), Japanese, modern standard
Spanish and German, and separate Simplified (`zh-Hans`) and Traditional (`zh-Hant`) written Chinese.
Regional date and number formats are independent of interface text; Chinese script selection takes
precedence over regional defaults.

Korean, Japanese, Spanish, German and both Chinese catalogs have draft translation status.
All nine catalog sets have AI-assisted semantic review against owning English and consumer behavior,
including recovery, settlement, payment choices, ordering, counts and operator faults. No human
native-speaker sign-off is recorded for Latvian, Arabic or the draft translations. Automated completeness
and placeholder checks establish structural consistency, not linguistic approval. Record a reviewer's
language, reviewed source and catalog/context scope when reporting linguistic approval.

| Concept | English | Latvian | Arabic |
| --- | --- | --- | --- |
| Case | case | lieta | ملف |
| Claim | claim | prasījums | مطالبة |
| Claimant | claimant | prasītājs | المطالب |
| Insurer | insurer | apdrošinātājs | شركة التأمين |
| Payment decision | payment decision | izmaksas lēmums | قرار الدفع |
| Reminder | reminder | atgādinājums | تذكير |
| Next action | next action | nākamā darbība | الإجراء التالي |
| Responsible person | responsible | atbildīgais | المسؤول |
| Owner role | owner | īpašnieks | المالك |
| Void | void | anulēt | إبطال |
| Erase | erase | dzēst | محو |

Use ملف for an Arabic case, rather than قضية. Profiles hold authored names and optional contact details;
identity-provider hints do not become profiles automatically. A case view is a named selection of readable
cases. Attention explains why work needs action; a headline describes the current case state. The
recommended act is the core's offered next action and never a stewardship default.

## Principal and actor

A principal is an exact issuer and immutable human subject or service client identity. An actor is its
registered identifier, enabled state and optional authored profile. Current grants authorize actions on an installation or case.
Principal kinds and roles have declared vocabularies. Identity and scope values are admitted before use;
malformed retained identities or role spellings are integrity faults.

## Admission and permanent format

Admission applies current rules to newly asserted values. Permanent format describes what a retained
value can represent. Reading retained values uses permanent format.

## Case act

A requested change from the [case-act declaration](../src/ClaimCore.Domain/CaseActs.fs), including financial facts and stewardship. Work and reminders have separate typed operation families. Availability depends on current case state, the supplied values and the actor's rights.

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

A ledger entry names case facts, case work, reminder effects, an authority target or an actor-scoped final-nonacceptance control.
Public acceptance receipts expose typed business targets as `subject`; recovery controls have no business
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

## Case work and responsibility

Case work holds an optional responsible ActorId and Handling summary with its own revision, starting at
zero. Responsibility is independent of grants and visibility and is inherited by every case reminder.
Mine means that the requester is currently responsible inside current read access. An unavailable
assignee remains recorded until explicitly changed.

## Reminder and follow-up

A reminder is an identified, independently revisioned action and planned date in Pending, Completed or
Cancelled state. Its creation ID equals its operation UUID. Next follow-up is the minimum Pending due
date, with stable reminder-ID ties. Pending includes overdue dates. Current Pending reminders on active
open cases have Overdue, Today or Upcoming timing; terminal reminders and reminders on inactive parents
have no actionable timing label. Close/void retains planned dates for reactivation.
Complete-and-plan-next completes one reminder and creates one replacement atomically under one accepted
operation identity.

## Work attention

An active open case needs a next action when it has no Pending reminders. This monitored condition does
not block completion or require a fake action. Missing plan, overdue work, unassigned and unavailable
assignee overlap; affected-case totals count each case in their union once. Date classification uses
the [observation basis](#observation-basis) and does not append synthetic history.

## Observation basis

A work read reports the database observation instant, the business date derived from that instant and
the installation's business time zone. The client clock does not determine whether work is overdue.
Separate reads and loaded pages can have different bases; a loaded reminder retains its own page's
basis until refreshed.

## Incident description and Handling summary

Optional 600-scalar LF plain text: what happened and current handling context, respectively. They have
separate domain types and current admission owners. Omission preserves a partial value; explicit clear
removes it. Neither text field replaces structured status, lifecycle, progress or reminder state.

## Targeted update and reaffirmation

A partial update changes selected facts while retaining omissions from locked state. A money assertion
always contains amount and currency together. Payment reaffirmation explicitly acknowledges the existing
payment when correcting a paid decision; it requires payment-recording authority and does not transfer
money. Read reviews do not authorize execution or replace the expected revision.
