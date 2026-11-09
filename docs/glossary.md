# Glossary

The [machine-readable glossary](../src/ClaimCore.Presentation/Messages/Localization/glossary.json) owns
translated terms. The contract generator checks spelling restrictions and message arguments.
Interface language and regional date/number formats are independent; Chinese script selection takes
precedence over region defaults.

## Language review

English is the source. Required catalog coverage is en, lv and ar; the other languages can use generated
English fallback for missing or stale translations. All eight translated languages have the review entry
“AI-assisted revision 2026-11; human review pending”. No native-speaker approval is recorded.

| Language | Current status |
| --- | --- |
| Latvian (lv) | AI-revised; human review pending |
| Arabic (ar) | AI-revised; human review pending |
| Korean (ko) | Draft with English fallback; human review pending |
| Japanese (ja) | Draft with English fallback; human review pending |
| Simplified Chinese (zh-Hans) | Draft with English fallback; human review pending |
| Traditional Chinese (zh-Hant) | Draft with English fallback; human review pending |
| Spanish (es) | Draft with English fallback; human review pending |
| German (de) | Draft with English fallback; human review pending |

Korean 청구 건 (case) and 후속 조치 (reminder) are candidates needing native confirmation.
Latvian uses versija for entity version, Datu pārvaldnieks for Data steward and Lietu apstrādātājs for
Case handler. Arabic uses ملف for a case and مرجع الملف for its reference; the prohibited legal-case
term is not used. Chinese uses 案件; German uses Wiedervorlage and nächster Schritt; Spanish erasure
uses Suprimir. Grammar-dependent labels remain separate even when English wording matches.

Intentional identical-language cognates are Spanish Cursor, Auditor, Total and Roles; German Cursor,
Name, Person, Code and Version. Automated catalog checks and screenshots establish structure and
rendering, not linguistic approval. Human review records should identify the language, English source
and contexts reviewed. [Locale screenshots](images/localization/) show a case workspace and review;
RTL and pseudolocale captures cover direction and expanded text.

## Account and sign-in identity

A person uses a human account; automation uses a service account. In wire types, a principal is the exact
issuer, kind and immutable subject/client identity. An actor is its registered ActorId, enabled state
and optional entered profile. Current roles authorize actions across all cases or one case.
A display name or email address is not a sign-in identity and grants no access.

## Case identity and reference

A CaseId is the UUID of its accepted OPEN change. Its original reference is immutable,
ASCII-case-insensitively unique, retained after erasure and never reused. Tagged ID/reference selectors
make UUID-shaped references unambiguous. See [Integrating](integrating.md#identity-and-records).

## Facts, decision and payment

A payment decision records its date, payable amount and currency. Payment records a date against that
decision. Progress is Undecided, Decided, Partly paid or Paid. Payments are up to 50 positive entries
identified by their accepting operation UUID. Their sum cannot exceed payable, and all use the decision
currency. Zero payable with no entries is Paid. Recording or removing an entry does not transfer or refund money.

A correction updates selected existing registration or decision facts. Amount and currency are asserted
together. Retained entries constrain decision changes; clearing a decision requires no entries.

## Lifecycle and status

Open/closed status describes whether routine work continues. Void stops work while preserving contents
and history; reinstatement permits work again without changing open/closed status. Erasure permanently
removes the live case's erasable contents while retaining its reference and change metadata.
[Erasure limits](operating.md#erasure-limits) explain copies outside the live database.

## Case work and responsibility

Case work has an optional responsible person and handling summary, with an independent version starting
at zero. Responsibility is a recorded fact, separate from viewing rights and roles. Mine selects cases
for which the current account is responsible within its current visibility. Later disable/revocation
retains that responsibility and marks the person unavailable.

## Reminder and follow-up

A reminder has an immutable parent/ID, its own version, action and planned date, and Pending, Completed
or Cancelled state. Its creation ID is the accepting operation UUID. Complete-and-plan-next finishes
one reminder and creates one replacement atomically. Pending includes overdue dates.

Next follow-up is the earliest pending reminder date, with stable ID ties. Case due filters classify
that minimum; reminder filters classify individual actions. Close/void retain dates for reactivation.
A missing next action raises attention without blocking work or creating a synthetic reminder.

## Observation basis

A read reports database observedAt, its businessDate and the installation timeZone. The client clock
does not classify overdue work. Separate reads/pages can have different bases; previously loaded
reminders retain their own page's basis until refreshed.

## Work view and presentation view

A work view is a named selection of readable cases or reminders. Attention explains work needing action;
a headline describes state; the recommendation is an offered next action. The response's presentation
`view` is a Screen of ordered semantic regions with seven content kinds. Human clients render these
values generically; typed data remains available for interaction and machine output.

## Change and unconfirmed changes

An operation ID is a UUIDv7 identifying one exact submitted change. Its first-execution window is ten
minutes; replay is checked first. A local recovery attempt stores identity, never the business request
or credentials. Restart recovery checks the result and cannot reconstruct a submission.

`operation.get` returns accepted metadata under current disclosure rights, a pending deadline, or final
NOT_ACCEPTED for the requester after locked absence confirmation. It appends nothing. Own metadata can
remain available without case roles; business contents still require current rights.
See [delivery recovery](integrating.md#delivery-recovery) and the [clock contract](operating.md#database-clock).

## Inputs, outcomes and retained records

Current admission checks new assertions. Durable decoding checks the current stored grammar and retained
invariants; reading history does not reapply today's admission rules. Input issues are an ordered,
nonempty set of targeted violations, and one field can have multiple issues. Show all of them.

Public outcomes distinguish success, refusal and failure. Execution distinguishes acceptance, replay
and unknown delivery. A failure does not disprove a prior uncertain commit. A ledger position is a
sequence and 32-byte entry hash; an independently retained checkpoint supplies external comparison evidence.
