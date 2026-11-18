# Case work

Start in **Cases** to find a claim, record its facts, decide an amount, record payments or plan work.
ClaimCore records assertions; it does not decide coverage or transfer money. Use fictional data for
[getting started](getting-started.md).

## Find work

Cases opens at **Needs attention**. Use the view pills, search and filters to narrow the results.
**Mine** follows explicit responsibility. **Unassigned** finds active open cases with nobody responsible.
**Decided and not fully paid** includes partly paid cases. Paid cases ready to close need attention too.
A case with several attention reasons counts once.

Search matches the current reference, policy numbers, insurer claim references, party names and
contact details, and current narratives. Exact reference lookup also accepts earlier references. Percent, underscore and backslash are literal text. An exact readable reference has an open
link even outside the selected view. Unreadable cases remain undisclosed.

Open a row for its workspace, or use **Reassign** to hand it over directly. Totals use the same selection
and remain separate by currency. **Load more** loads another page; concurrent work can change counts.

```sh
claimcore case list --view needs-attention
claimcore case list --responsible-actor ACTOR-ID
claimcore case summary --view mine
```

Responsibility leaves read permissions as they were. Dates and overdue labels use the installation's
business date.

## Open a case

Choose **Open a case** and enter your reference. You can add the incident date and country, Received
date, narrative, policy number, insurer claim reference and claimed amount with its currency. Received
means the date your office received the claim. Each observed date must be a valid calendar date at
or before the business date. Record the actual dates even when they arrived out of order.

Missing facts show **Not recorded**. An unrecorded country represents an unknown country. Explicit
zero claimed money remains visible and contributes to summaries. An amount and currency are
recorded or cleared together. Amend or correct facts as they become known.

Use the party rows while opening the case or manage parties from the workspace. A case can have up
to 20 current parties, with multiple claimants, insurers, representatives or other parties. Record a
name, contact details or both. Party changes require Register and access to the case. Removing a
party hides it from current party lists. Its history stays available to authorized readers until
case erasure. Case CSV joins current claimant and insurer names in creation order.

## Change the reference or link a case

With Register, use **Change case reference** on an active open or closed case. Every recorded
reference remains reserved to that case, including after erasure. Opening an earlier reference
shows the current one and identifies the matched reference.

With Manage case work and read access to both cases, you can link a related case and include a
note. The source shows its outgoing links. The target shows readable incoming links, newest first,
and a count. Each case can have up to 20 outgoing links. Links never grant read access. An
unreadable target shows as unavailable. Erasing the source clears its links.

## Open the case workspace

The header identifies the case. The tracker shows recorded stages; Claim, Payment decision and payments,
Notes and Activity occupy the main column. Work and Next actions sit beside them. The primary action is
the core's recommendation; **More actions** contains other offered changes. **Record details** holds IDs,
versions and copy controls.

The Paid stage uses net payments in the decision currency. It is complete when the case is Paid or Overpaid.
Voided and erased cases show their disposition. A reinstatement notice remains until the next fact change.
A blocked task link explains the current reason and any offered alternative while preserving the workspace.

Follow supplied case links. A UUID-shaped reference stays a reference. An unavailable case is not
identified as either missing or outside your access.

## Decide and record payment

Choose **Record payment decision** to enter the decision date, payable amount and currency, and
optional decision basis. You can revise or withdraw a decision independently of recorded payments.
A zero-payable decision with zero net paid is Paid.

Use **Record actual payment** for each transfer or return. Enter the direction, the actual payment
date, a positive amount and its own currency. Include the bank or letter reference and a payee
from the case's current parties when known. A payment can precede the decision or use another
currency. The payment date must be at or before the business date.

The case shows net paid by currency. Progress compares the decision's payable amount with net paid
in that currency. An overpayment or negative net raises **Payments need review** on an active open
case. The tracker uses the latest date of a current Paid entry for the Paid stage. A removed payee
keeps its last recorded name with a removed marker.

The workspace shows the latest three entries and their total count. **Show all payments** opens
the paged list, also available through `claimcore case payments`. Use **Remove payment entry** for
an assertion recorded in error. **Correct payment entry** replaces the amount, currency, direction,
date, reference and payee in one operation. The receipt identifies the removed and replacement
entries. Use Returned to record money actually returned by a recipient.

## Plan case work

Set a responsible person and write a Handling summary in Work. Assignment requires an active open case;
summary edits also permit a closed active case. If someone loses access or is disabled, their assignment
remains visible as unavailable so another handler can take over. Summary author and time follow actual
text changes, independently of assignment.

Add next actions with a short action and planned date. Choose Follow-up or Deadline. Pending
deadlines past their due date appear first in case attention. To complete a reminder, enter when
the action was done or use today's business date. A completion date cannot be in the future.
Deadlines completed by their due date show Met. Later completions show Missed. **Complete and plan
next action** records both changes together. Reopening a completed or cancelled action requires a date.

Closing or voiding preserves pending actions. Reopening or reinstating restores their relevance with the
same dates. Complete/cancel can clean up actions on a closed active case. Erasure clears work and reminders.

## Add and review notes

Write a note in the Notes composer and confirm **Add note**. Notes permit up to 2,000 Unicode scalar
values and 8,192 UTF-8 bytes, with LF line breaks. You can add or remove them on an active open or closed
case when you have Read case and Manage case work. Voided or erased cases refuse note changes.

Unsent inline text stays in memory for the same signed-in account and case through read refreshes,
Settings, and unrelated reminder or financial changes. Leaving a dirty case asks for a decision.
Cancelling a copied review keeps the inline text; only its own unchanged accepted note or explicit
discard clears it. Signing out or changing the account/session clears it; it is never saved for restart.
Same-case read refreshes preserve the active composer and caret while checking facts and access.
An access refusal keeps the composer hidden until a fresh read permits adding notes.

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
upcoming, completed or cancelled work. Kind, deadline outcome, state, due, assignment and text filters intersect
with the selected view. In **Manual filters**, the initial state is **Pending**; choose **Any** to
include completed and cancelled reminders too. **Include inactive work** can be selected explicitly
in either mode and includes closed or voided parent cases where the selected view permits them.
The controls show their effective defaults, including the standard view's inactive-parent choice.

An overdue plan still counts as a plan. A case can appear in both overdue and today action queues.
Counts and loaded pages can change as others work; refresh for current results.

Reminder links select the exact reminder within its parent case, including completed or cancelled
reminders. Direct URLs and reloads read that reminder directly. A known return to the queue restores its
filters, scroll and focus under the same workspace owner; direct entry falls back to the reminder queue.

## Close or reopen

Choose **Close the case** when handling is finished. Confirm the outcome and optionally record a
closing note. The core suggests Declined for zero payable or Settled when net paid covers a
positive payable. You can choose any listed outcome. The Closed tracker stage shows that outcome
and the business date of the close.

Choose **Reopen the case** and enter a reason. The receipt and authorized history record it.
Reopen and close again to record a new outcome. Each close remains in history.

## Correct existing facts

Choose **Correct case facts** to revise selected registration and decision facts together. An open case needs an
existing decision; a closed case can be corrected with or without one. Unselected facts stay unchanged.
Correction requires an existing decision for changes to the decision group. You can clear the decision
basis or withdraw the decision while keeping payment entries. A refused correction records nothing.

## Case stewardship

A Data steward can **Void case record**, **Reinstate case record** or **Erase case** when offered. Void records an entry-error reason
and stops business work while preserving contents. Reinstate records a reason and resumes it. Each
accepted reason remains attached to its own receipt and full history entry, including after later
changes; current rights and case erasure control disclosure.

Erase permanently clears saved business contents, including earlier facts, payments, work, reminders
notes, parties and source links. Every reference remains reserved. Read the consequence and type the shown reference to confirm.
Erasure also freezes the reviewed case-family sequence. Changes to any owned work, note, reminder or
payment invalidate that review even when the case fact version stays unchanged. Review again explicitly
before another attempt. Unrelated cases and account changes do not change this family sequence;
current permission checks still apply.
See [erasure limits](operating.md#erasure-limits) for backups and external copies.

## Review, confirmation and delivery

Forms show short hints and precise rules in information tips. Language and regional formats are
independent. A format change does not reinterpret an amount already entered. Input issues link to fields.

A new visible review starts at the beginning and focuses its heading. Reading position stays with the
same review through Settings and display changes; returning to edit restores the authoring position.

Review the proposed changes and confirm once. An interactive CLI command can read an omitted version
and freeze it for review; automation supplies its expected version. Technical versions appear under
Record details or CLI `--details`. A conflict keeps your draft and requires an explicit fresh review.
The CLI never silently rebases. In the browser, accepted case receipts show the recorded facts at
full available width, with Close above them; closing the receipt returns to the case workspace.

If delivery is uncertain, keep the original change identity. **Unconfirmed changes** checks it
automatically; lookup does not record it again. Reload retains identity but cannot reconstruct a discarded
request. Follow [delivery recovery](integrating.md#recover-an-uncertain-change) before starting another change.

Browser Back and Forward preserve managed history entries, including a Settings detour with an authored
draft. Declining departure preserves the original Forward branch. Additional navigation while a guarded
traversal is being restored or awaiting consent is ignored until that transition settles. Reload and
cross-document departure request the browser's native warning while drafts, prepared or uncertain work,
or delivery need protection. Browser activation and prompting rules apply; this does not persist drafts
or guarantee protection against forced termination. Unknown history entries have no provable offset and
are followed without rewriting the history stack.

## Report on current case facts

Use registered, received and closed date ranges and the inactivity filter in Cases. Date ranges are
inclusive. Inactivity uses the latest case-family activity on or before the selected date. A missing
date falls outside a date filter. These reports describe current facts at the observation time.

Choose a summary group for registered, received or closed month, outcome, responsible person or
days open. The summary shows case counts and claimed, payable and net-paid totals by currency,
with each total's contributing-case count. A group can contain cases with no recorded money.
In the CLI use `claimcore case summary --group-by received-month`.

**Download CSV** exports the selected case list, grouped summary, timeline or reminder queue.
The reminder export includes kind, completion date and deadline outcome. A refusal or resource
limit returns an explanation before an incomplete export can be accepted.

## Daily reminder digest

An owner can add a digest email address to a person's profile. When the installation has SMTP
configured, ClaimCore sends one daily prompt with counts for the person's readable assigned work.
The message links to the Reminders queue. The in-app queue supplies the current dates and attention.
See [Digest configuration](operating.md#daily-reminder-digest).

### Case reminder queue

The case workspace supplies a **Case reminders** link for a readable case that has not been erased.
It opens the queue with that case reference, an explicit **Any** state selection and inactive cases
included, so pending, completed and cancelled reminders on closed or voided cases remain reachable. The queue visibly identifies whether the restriction uses a case
reference or ID. State, due, assignment and text selections compose with it and survive reload.
**Clear case restriction** removes only the case target, preserving the remaining filters and
starting from the first page. The query still checks current access; the caption does not establish
that a requested case exists or is readable.
