# Glossary

The [machine-readable glossary](../src/ClaimCore.Presentation/Messages/Localization/glossary.json) owns
translated terms. The contract generator checks spelling restrictions and message arguments.
Interface language and regional date/number formats are independent; Chinese script selection takes
precedence over region defaults.

## Language review

English is the source. All eight translated languages have current authored catalogs. Release
qualification requires complete, current translations in every offered language. During development,
coverage checks require en, lv and ar; other languages warn. Runtime resolution uses English for
missing or stale translations. All translations are AI-assisted; native-speaker approval is unverified.

| Language | Current status |
| --- | --- |
| Latvian (lv) | AI-assisted; human review pending |
| Arabic (ar) | AI-assisted; human review pending |
| Korean (ko) | AI-assisted; human review pending |
| Japanese (ja) | AI-assisted; human review pending |
| Simplified Chinese (zh-Hans) | AI-assisted; human review pending |
| Traditional Chinese (zh-Hant) | AI-assisted; human review pending |
| Spanish (es) | AI-assisted; human review pending |
| German (de) | AI-assisted; human review pending |

Current Korean catalog terms are 청구 건 (case) and 후속 조치 (reminder); native-speaker review is unverified.
Latvian uses versija for entity version, Datu pārvaldnieks for Data steward and Lietu apstrādātājs for
Case handler. Arabic uses ملف for a case and مرجع الملف for its reference; the prohibited legal-case
term is not used. Chinese uses 案件; German uses Wiedervorlage and nächster Schritt; Spanish erasure
uses Suprimir. Grammar-dependent labels remain separate even when English wording matches.

Intentional identical-language cognates are Spanish Cursor, Auditor, Total and Roles; German Cursor,
Name, Person, Code and Version. Automated catalog checks and screenshots establish structure and
rendering, not linguistic approval. Human review records should identify the language, English source
and contexts reviewed. Executable localization journeys cover case work, reviews, RTL layouts
and expanded text.

## People, access and tasks

**People** names the subject that registers accounts, maintains profiles, grants roles and enables or
disables accounts. **Access** names rights and the policy that determines what a principal may do.
The capabilities are `MANAGE_ACCESS` and `READ_ACCESS`; `people.*` endpoint IDs identify people reads
and commands. **Actor** and **ActorId** remain the technical registered-identity terms, for both human
and service principals. A dotted task ID, such as `case.recordPayment` or `people.grant`, is the task
identity in submissions, advice, receipts, history and CLI declarations.

The translated party roles, payment directions, closure outcomes, reminder kinds, deadline outcomes,
reporting dimensions, day bands, digest, and installation operator wording are AI-assisted and await
fluent review in all eight translated languages.

## Account and sign-in identity

A person uses a human account; automation uses a service account. In wire types, a principal is the exact
issuer, kind and immutable subject/client identity. An actor is its registered ActorId, enabled state
and optional entered profile. Current roles authorize actions across all cases or one case.
A display name or email address is not a sign-in identity and grants no access.

## Case identity and reference

A CaseId is the UUID of its accepted `case.open` change. References remain reserved to that case
under ASCII-case-insensitive comparison even after a correction or erasure. Tagged ID and reference
selectors make UUID-shaped references unambiguous. Earlier references resolve to the current case.

## Facts, decision and payment

A payment decision records its date, payable amount, currency and optional basis. A payment entry
records a Paid or Returned direction, date, positive amount and currency, with optional evidence
and payee. Its ID is the UUID of the accepting operation. Progress uses the net payment totals
in the decision currency and can be Undecided, Decided, Partly paid, Paid or Overpaid.

A correction updates selected registration or decision facts. Amount and currency are asserted
together. Decision changes preserve payment entries.

## Lifecycle and status

Open/closed status describes whether routine work continues. Void stops work while preserving contents
and history; reinstatement permits work again without changing open/closed status. Erasure permanently
removes the live case's erasable contents while retaining its reference and change metadata.
[Erasure limits](operating.md#erasure-limits) explain copies outside the live database.

## Case family

A case family includes the case facts, payment entries, case work, reminders, notes, parties and source links. Each owned
record has its own revision; each task declares the preconditions reviewed for its change. The family
sequence advances for every accepted family change.
Erasing the family clears its live erasable contents together. A person has a separate lifetime and
combines identity, profile, enablement and role assignments.

## Party and related case

A party belongs to one case and has a role with an optional name and contact details. Current
party summaries use creation order. A case link has a source and target. Readers can see incoming
links only from cases they can read.

## Closure and net paid

Closure is the current outcome of an accepted close. The operator chooses Settled, Declined,
Withdrawn, Duplicate or Other. Net paid is recorded Paid money minus Returned money within one
currency. Every reporting total carries the number of cases that contributed.

## Deadline, daily digest and installation operator

A deadline is a reminder kind. The completion date determines whether the deadline was met.
The daily digest is an optional email prompt with counts and a queue link. The installation
operator is the authority used for database-owner administration of initialization and recovery.

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
a headline describes composite case state, combining lifecycle, status and payment progress. Inactive
reminder parents report coarse case state without inferring an open/closed status. The recommendation is an offered next action. The HUMAN document carries a Screen of ordered semantic regions containing entries, tables, stages,
codes, notices or timelines. Human clients render these values generically; DATA remains available
for automation.

## Change and unconfirmed changes

An operation ID is a UUIDv7 identifying one exact submitted change. Its first-execution window is ten
minutes; accepted retry lookup follows current authorization. A local recovery attempt stores identity, never the business request
or credentials. Restart recovery checks the result and cannot reconstruct a submission.

`operation.get` returns accepted metadata under current disclosure rights, a pending deadline, or final
NOT_ACCEPTED for the requester after locked absence confirmation. It appends nothing. Own metadata can
remain available without case roles; business contents still require current rights.
See [delivery recovery](integrating.md#recover-an-uncertain-change) and the [clock contract](operating.md#database-clock).

## Inputs, outcomes and retained records

Current admission checks new assertions. Durable decoding checks the current stored grammar and retained
invariants; reading history does not reapply today's admission rules. Input issues are an ordered,
nonempty set of targeted violations, and one field can have multiple issues. Show all of them.

Public outcomes distinguish success, refusal and failure. Execution distinguishes acceptance, replay
and unknown delivery. A failure does not disprove a prior uncertain commit. A ledger position is a
sequence and 32-byte entry hash; an independently retained checkpoint supplies external comparison evidence.

The new payment-correction wording is agent-translated in all offered languages and awaits fluent
review. Moving the existing assignment label to shared Presentation preserves its prior translations.

CLI display-locale guidance and operational delivery wording were agent-translated in all offered
languages and await fluent review.
## ClaimCore browser session

A ClaimCore browser session is one protected browser authentication ticket stored by ClaimCore. Its
canonical principal includes kind, issuer and subject, independently of registration/profile hints.
Ending these sessions removes the selected principal's tickets; it does not revoke identity-provider
sessions, credentials or independent bearer tokens. The new session-termination wording in all eight
translated languages is agent-produced and awaits fluent review.
The bounded CSV resource advice and browser download chrome are agent-translated in all offered languages;
these additions await fluent review.

The case-targeted reminder captions and case-restriction reset control are agent-translated in all offered languages and await fluent review.

The revised CLI diagnostic and recovery setup wording is AI-assisted in all eight translated languages and awaits fluent review.
