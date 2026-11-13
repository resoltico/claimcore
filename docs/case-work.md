# Case work

Start in **Cases** to find a claim, record its facts, decide an amount, record payments or plan work.
ClaimCore records assertions; it does not decide coverage or transfer money. Use fictional data for
[getting started](getting-started.md).

## Find work

Cases opens at **Needs attention**. Use the view pills, search and filters to narrow the results.
**Mine** follows explicit responsibility. **Unassigned** finds active open cases with nobody responsible.
**Decided and not fully paid** includes partly paid cases. Paid cases ready to close need attention too.
A case with several attention reasons counts once.

Search matches references, policy numbers, insurer claim references, claimant, insurer and current
narratives. Percent, underscore and backslash are literal text. An exact readable reference has an open
link even outside the selected view. Unreadable cases remain undisclosed.

Open a row for its workspace, or use **Reassign** to hand it over directly. Totals use the same selection
and remain separate by currency. **Load more** loads another page; concurrent work can change counts.

```sh
claimcore case list --view needs-attention
claimcore case list --responsible-actor ACTOR-ID
claimcore case summary --view mine
```

Responsibility does not grant access. Dates and overdue labels use the installation's business date.
ClaimCore does not send reminder alarms.

## Open a case

Choose **Open a case**. Enter your reference, claimant, insurer, incident and notification dates,
incident country, and claimed amount with its currency. Incident description, policy number and insurer
claim reference are optional. Review the facts and confirm **Open case**.

The reference cannot change or be reused after erasure. The case starts open and undecided, with empty
work, notes and next actions. A claimant can be a person or an organization.

## Open the case workspace

The header identifies the case. The tracker shows recorded stages; Claim, Payment decision and payments,
Notes and Activity occupy the main column. Work and Next actions sit beside them. The primary action is
the core's recommendation; **More actions** contains other offered changes. **Record details** holds IDs,
versions and copy controls.

The paid stage shows progress toward payable. Missing stages stay **Not recorded**, even after closing.
Voided and erased cases show their disposition. A reinstatement notice remains until the next fact change.
A blocked task link explains the current reason and any offered alternative while preserving the workspace.

Follow supplied case links. A UUID-shaped reference stays a reference. An unavailable case is not
identified as either missing or outside your access.

## Decide and record payment

Choose **Decide** to record a decision date and payable amount with currency. A zero-payable decision is
already Paid and needs no payment entry. Otherwise choose **Record payment**, enter its date and positive
amount, review and confirm. Keep accurate entries; removal is only for correcting an entry recorded in
error. Payment counts are bounded by the exact-integer range; page size does not limit the case total.

Payments use the decision currency. Their sum cannot exceed payable. Each date is on or after the
decision date and no later than the business date. A partial payment leaves the case Partly paid; the
card shows payable, paid and remaining amounts, the latest three entries and total count. The paged
`case.payments` read (CLI `case payments`) retrieves earlier current entries, 20 by default and at most 50 per page.
Choose **Show all payments** in the workspace, then **More payments** to continue. Narrow layouts show
the same supplied rows as cards; **Remove payment** remains attached to its current entry.

Use an entry's **Remove payment** action to remove an assertion recorded in error. This does not refund
or reverse a transfer. To correct an entry, remove it and record the right one. Earlier history remains
until case erasure. Recording a payment does not close the case.

You may revise a decision while retaining entries when currency stays the same, payable covers the paid
total, and the decision date does not follow an entry. A decision with retained payment entries cannot
be withdrawn or cleared. Remove entries only if they were recorded in error; keep accurate payments.
Amount and currency always travel together.

## Plan case work

Set a responsible person and write a Handling summary in Work. Assignment requires an active open case;
summary edits also permit a closed active case. If someone loses access or is disabled, their assignment
remains visible as unavailable so another handler can take over. Summary author and time follow actual
text changes, independently of assignment.

Add next actions with a short action and planned date. Dates may be past or future. Choose **Complete**,
**Cancel** or **Reschedule** as offered. Completion records when you marked the action, not when an external
event occurred. **Complete and plan next** records both changes together; a refused part changes neither.
Reopening a completed or cancelled action requires a date.

Closing or voiding preserves pending actions. Reopening or reinstating restores their relevance with the
same dates. Complete/cancel can clean up actions on a closed active case. Erasure clears work and reminders.

## Add and review notes

Write a note in the Notes composer and confirm **Add note**. Notes permit up to 2,000 Unicode scalar
values and 8,192 UTF-8 bytes, with LF line breaks. You can add or remove them on an active open or closed
case when you have Read case and Manage case work. Voided or erased cases refuse note changes.

The workspace shows the latest three current notes and their count. **Show all notes** expands the list
in place and pages through the rest. Removing a note removes it from the current list. Full history can
still disclose its text until case erasure; summary history does not reveal note text.

```sh
claimcore note add CASE-REFERENCE --text "Called the claimant; awaiting documents."
claimcore case notes CASE-REFERENCE --all
claimcore help note remove
```

Notes are independent changes and do not require a case or work revision.

## Work attention and queues

Open **Reminders** for actions across readable cases. Standard views select pending, overdue, today,
upcoming, completed or cancelled work. **Manual filters** enables **Include inactive work** without
changing other filters; returning to standard views restores their boundaries.

An overdue plan still counts as a plan. A case can appear in both overdue and today action queues.
Counts and loaded pages can change as others work; refresh for current results.

## Close or reopen

Choose **Close** when handling is finished. Neither a decision nor payment is required. **Reopen** resumes
ordinary work. Reopen before ordinary financial editing of a closed case, or use **Correct** when offered.

## Correct existing facts

Choose **Correct** to revise selected registration and decision facts together. An open case needs an
existing decision; a closed case can be corrected with or without one. Unselected facts stay unchanged.
Correction cannot invent a missing decision. Payment entries are changed separately, and retained entries
continue to constrain any decision change. A refused correction records nothing.

## Case stewardship

A Data steward can **Void**, **Reinstate** or **Erase** when offered. Void records an entry-error reason
and stops business work while preserving contents. Reinstate records a reason and resumes it.

Erase permanently clears saved business contents, including earlier facts, payments, work, reminders
and notes. The reference remains reserved. Read the consequence and type the shown reference to confirm.
See [erasure limits](operating.md#erasure-limits) for backups and external copies.

## Review, confirmation and delivery

Forms show short hints and precise rules in information tips. Language and regional formats are
independent. A format change does not reinterpret an amount already entered. Input issues link to fields.

Review the proposed changes and confirm once. An interactive CLI command can read an omitted version
and freeze it for review; automation supplies its expected version. Technical versions appear under
Record details or CLI `--details`. A conflict keeps your draft and requires an explicit fresh review.
The CLI never silently rebases.

If delivery is uncertain, keep the original change identity. **Unconfirmed changes** checks it
automatically; lookup does not record it again. Reload retains identity but cannot reconstruct a discarded
request. Follow [delivery recovery](integrating.md#recover-an-uncertain-change) before starting another change.
