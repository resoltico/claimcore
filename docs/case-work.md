# Case work

Use **Find a case** from the sidebar or the phone toolbar. Suggestions appear inside the dialog. Press Ctrl+K, or Command+K on Apple
platforms. Choose a suggestion, enter an exact reference, or search All with your text. **Filter this
view** limits the current worklist. Use the **Cases** breadcrumb to return to your last worklist.

Tables show the columns relevant to each view. Secondary values sit under the first primary value
on narrow screens. Row menus offer additional controls and **View details** opens a dialog.
The worklist checks for updates when you return to the window. Its footer shows the last update and
supports a manual refresh.

Adding a note and completing a reminder from its primary button record the change in one step.
Choose **Complete with details…** in the reminder's menu to edit the completion date and review
it. The result remains visible until you dismiss it or leave the case.

Start in **Cases** to find a claim, record its facts, decide an amount, record payments or plan work.
ClaimCore records assertions; it does not decide coverage or transfer money. Use fictional data for
[getting started](getting-started.md).

## Find work

Cases opens at **Needs attention**. Use the view tabs, **More views**, and **Filter this view** to narrow the results.
On phones, swipe the view tabs to reach More views or open **Filters** for the list filters and export.

**Mine** follows explicit responsibility. **Unassigned** finds active open cases with nobody responsible.
**Decided and not fully paid** includes partly paid cases. Cases ready to close and active cases with financial exceptions need attention too.
A case with several attention reasons counts once.

Search matches the current reference, policy numbers, insurer claim references, party names and
contact details, and current narratives. Percent, underscore and backslash are literal text.
An exact readable reference has an **Open** link even outside the selected view. The link opens the stable Case ID.

An empty worklist distinguishes a view with no matches from an accessible scope with no cases.
Use **Clear filters** to remove applied filters while keeping the view.

When search text is incomplete or invalid, Cases keeps your draft and the last valid selection.
Submit the draft to see the correction needed. For a malformed bookmark, correct or clear the listed
filter before loading results. For a repeated single-value filter, choose the value to keep, edit
it if needed and apply it. Other filters stay in place.

Open a row for its workspace, or use **Reassign** to hand it over directly. Totals use the same selection
and remain separate by currency. **Load more** loads another page; concurrent work can change counts.

```sh
claimcore case list --view needs-attention
claimcore case list --responsible-actor ACTOR-ID
claimcore case summary --view mine
```

Responsibility leaves read permissions as they were. Dates and overdue labels use the installation's
business date.

If case or reminder membership or order changes while paging, start again from the first page.
Report pages also restart when the facts contributing to their totals change.

## Open a case

Choose **Open a case** to open the centered work dialog. On a short screen, scroll the dialog to reach **Review changes**. The register assigns a reference when you record the case, and the receipt
shows the assigned value. Use the policy number or insurer claim reference to record external identifiers.
You can add the incident date and country, Received
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

From the CLI, `claimcore case open` records the case and returns its assigned reference.

## Link related cases

Adding a link requires Manage case work on the source and read access to both live cases. You can
include a note. The source shows its outgoing links. The target shows readable incoming links,
newest first, and a count. Each case can have up to 20 outgoing links. A source handler can detach an
active link using its handle when the target is inaccessible. Erasing either case closes the link
and clears its private target and note, including links detached earlier.

## Open the case workspace

The header identifies the case. The tracker shows recorded stages; Claim, Payment decision and payments,
Notes and Activity occupy the main column. Work and Next actions sit beside them. The primary action is
the core's recommendation. **More actions** groups the other offered changes, including data stewardship.
Activity and receipt details show the recorded comparisons and IDs,
versions and copy controls.

Payable coverage uses current net payments in the decision currency. The tracker gives closure its own step.
Voided and erased cases show their disposition. A reinstatement notice remains until the next fact change.
A blocked task link explains the current reason and any offered alternative while preserving the workspace.

Follow supplied case links. A UUID-shaped reference stays a reference. An unavailable case is not
identified as either missing or outside your access.

## Decide and record payment

Choose **Record payment decision** to enter the decision date, payable amount and currency, and
optional decision basis. You can revise or withdraw a decision independently of recorded payments.
A zero-payable decision with zero net paid shows **No payment due**.

Use **Record actual payment** for each transfer or return. Enter the direction, the actual payment
date, a positive amount and its own currency. Include the bank or letter reference and a payee
from the case's current parties when known. A payment can precede the decision or use another
currency. The payment date must be at or before the business date.

The case shows exact net paid for each currency with current transfers. Equal payments and
returns leave a recorded zero. Removing the last entry removes the currency balance.
Progress compares the decision's payable amount with the net in that currency and shows the
exact amounts alongside a meter when comparable. Transfers keep their own dates. **Latest
transfer date** is the greatest date among current payments and returns.

An overpayment, negative net in any currency, or a Settled outcome without a covered positive
payable raises **Payments need review** on an active case. It suppresses **Ready to close** and
the seeded close outcome. Authorized users can deliberately close by choosing an outcome.
Active closed cases remain eligible for payment recording, correction and removal. These changes
preserve the recorded closure and can bring a closed case into **Needs attention**.

The workspace shows the latest three entries and their total count. **Show all payments** opens
the paged list, also available through `claimcore case payments`. Use **Remove payment entry** for
an assertion recorded in error. **Correct payment entry** asks which facts to change and keeps
the other facts. The original payee can be kept even after removal. A changed payee must be a
current party on the same case. Clear a reference or payee explicitly. A successful correction
records both entry identities in one operation.

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

Closing or voiding preserves pending actions and their dates. Reopening a closed case or
reinstating a previously open case returns pending actions to active queues. A previously closed
case stays closed after reinstatement. Complete/cancel can clean up actions on a closed active case. Erasure clears work and reminders.

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

## Add, download and withdraw attachments

Open **Attachments** in the case workspace. Anyone who can read the case can list and download
current files. A human case handler can add and withdraw files on open and closed cases while
writes are available. Voided cases retain readable files.

Use **Choose a file**, or drop or paste one file on **Drop or paste a file** when your browser
supplies a file. Review its saved filename before confirming. The register refuses file types
that can run programs, such as `.exe`, `.js` and `.msi`. Put a refused file in an archive if the case
needs it. The register preserves the exact bytes and stores one copy of identical bytes per case.
Each file can be up to 25 MiB, including empty files. Each case can retain up to 100 distinct files
and 256 MiB in total. **Preview** is available for verified static PNG and JPEG images and PDFs
within the supported media profile. The PDF viewer displays pages and static form appearances in a
read-only view. Use **Download** for the original file if the browser cannot display the preview.
Same-name files show their attachment IDs. Use **View details** to copy the full ID of any file.

Withdrawal records who acted and when. It hides the selected attachment's details in current
reads, earlier receipts and history. Its bytes still count against the case's limits. Uploading the
exact bytes again requires a new review and creates a new attachment ID that can be read. The
earlier ID remains withdrawn. Whole-case erasure removes the retained files and private details.

```sh
claimcore case attachments CASE-REFERENCE --details
claimcore attachment add CASE-REFERENCE --file ./evidence.jpg
claimcore attachment download CASE-REFERENCE ATTACHMENT-ID --output ./evidence-copy.jpg
claimcore attachment withdraw CASE-REFERENCE ATTACHMENT-ID
```

Use `--details` to find the attachment ID in the list. The CLI checks the download name against the
current authorized list and verifies the complete download before opening the destination. It
normally publishes atomically from a private staging directory in the
system temporary area. When the destination is on a different filesystem or the filesystem rejects
hard links as unsupported, the CLI creates the destination exclusively and writes the verified bytes
there. Both methods refuse existing entries, including symlinks, and request owner-only permissions.
On filesystems such as FAT and ExFAT, the volume's mount policy controls permissions. An abrupt stop
can leave a private staging file or an incomplete destination. Direct creation opens the requested
path once with no overwrite. For failure cleanup, the CLI holds the parent directory open separately
and only attempts removal when it can match the directory entry to the file it created and the
directory is protected against replacement by another user. In the shell, Ctrl-C before publication
cancels the save. A save that completes while Ctrl-C arrives reports success. Review any remaining
file before retrying. Downloaded copies remain under the recipient's
control after withdrawal or erasure.

## Work attention and queues

Open **Reminders** for actions across readable cases. Select **Pending**, **Mine**, **Overdue**,
**Today**, or **Upcoming** from the tabs. **More views** contains **Completed** and **Cancelled**.
**Filter this view**, the toolbar filters, and **Download CSV** use the current selection.

Open **More filters** to use **Manual filters** or change **Include inactive case work**. Manual
filtering uses **Pending** when no state is selected. Choose **Any** to include completed and cancelled
reminders. The toolbar shows the effective defaults for the selected mode. Including inactive case work
includes closed or voided parent cases where the selection permits them.

An overdue plan still counts as a plan. A case can appear in both overdue and today action queues.
Counts and loaded pages can change as others work; refresh for current results.

Reminder links select the exact reminder within its parent case, including completed or cancelled
reminders. Direct URLs and reloads read that reminder directly. A known return to the queue restores its
filters, scroll and focus under the same workspace owner; direct entry falls back to the reminder queue.

## Close or reopen

Choose **Close the case** when handling is finished. Confirm the outcome and optionally record a
closing note. When no financial exception needs review, the core suggests Declined for zero
payable or Settled when net paid covers a positive payable. You can choose any listed outcome.
The Closed tracker stage shows the outcome and the business date of the close.

Choose **Reopen the case** and enter a reason. The receipt and authorized history record it.
Reopen and close again to record a new outcome. Each close remains in history.

## Correct existing facts

Choose **Correct case facts** to revise selected registration and decision facts together. An open case needs an
existing decision; a closed case can be corrected with or without one. Unselected facts stay unchanged.
Correction requires an existing decision for changes to the decision group. You can clear the decision
basis or withdraw the decision while keeping payment entries. A refused correction records nothing.

## Case stewardship

A Data steward can **Void case record**, **Reinstate case record** or **Erase case** when offered. Void records an entry-error reason
and stops business work while preserving contents. Reinstate records a reason and restores the previous open or closed handling. Each
accepted reason remains attached to its own receipt and full history entry, including after later
changes; current rights and case erasure control disclosure.

Erase permanently clears saved business contents, including earlier facts, payments, work, reminders,
notes, attachments, parties and related-case links. The issued Case ID and reference remain. Read the
consequence and type the shown reference to confirm.
The erasure review covers owned work, notes, reminders, parties, attachments and retained relationships
even when the case revision stays fixed. If the review becomes stale, read the current case and
review the erasure again. Confirmation also checks current permission.
Erasing either case closes the relationship and clears its retained target and note, including
a link detached before erasure. An authorized source handler can detach an active link using
its handle when the target is inaccessible. Free text authored independently on another case
keeps that case’s own erasure lifetime. See [erasure limits](operating.md#erasure-limits) for
backups and external copies.

## Review, confirmation and delivery

Forms keep applicable hints, precise input rules and input issues with their fields. Language and
regional formats are independent. A format change does not reinterpret an amount already entered.
Input-issue summaries link to the corresponding controls.

A new visible review starts at the beginning and focuses its heading. Reading position stays with the
same review through Settings and display changes; returning to edit restores the authoring position.

Review the proposed changes and confirm once. An interactive CLI command can read an omitted version
and freeze it for review; automation supplies its expected version. Technical versions appear under
Technical details or CLI `--details`. A conflict keeps your draft and requires an explicit fresh review.
The CLI never silently rebases. In the browser, accepted case receipts show the recorded facts at
full available width, with Close above them; closing the receipt returns to the case workspace.

If delivery is uncertain, keep the original change identity. **Unconfirmed changes** checks it
automatically. If browser storage blocks coordination, open **Unconfirmed changes** and choose
**Check now** on the affected operation. Checks only read the recorded outcome. Reload retains
the identity. Keep the original request separately if you may need to resend it. Follow
[delivery recovery](integrating.md#recover-an-uncertain-change) before starting another change.

The browser asks before leaving authored changes, a pending review or an uncertain delivery.
Closing an untouched invalid attempt needs no discard confirmation. A Settings detour keeps the
draft and reading position. Reload or departure from the workspace can show the browser's native
warning while a change needs protection or delivery is in progress. Draft fields are held in the
current page. The recovery identity can be checked after reload.

## Report on current case facts

Open **Case report** from Cases to report on the current selection. The Cases queue itself loads the case list.
Use **Filters** to enter both ends of a registered, received or closed date range, then apply them together.
Date ranges are
inclusive. Inactivity uses the latest case-family activity on or before the selected date. A missing
date falls outside a date filter. These reports describe current facts at the observation time.

Choose a summary group for registered, received or closed month, outcome, responsible person or
days open. Each report page shows the whole selection's case counts and a page of its groups and
currency breakdowns. Claimed, payable and net-paid totals include counts of the cases that
contribute them. A group can contain cases with no recorded money. In the CLI use
`claimcore case summary --group-by received-month`.

Use **Next report page** to continue and **Restart report** to return to page 1. When a page link
becomes invalid, restart the report to refresh the selection. For a temporary read failure,
**Try again** reloads the requested page.

**Export case list CSV** and **Export report CSV** export the complete selected list or grouped report.
Activity and reminders retain **Download CSV** for their own selections. A grouped report's CSV is independent of the page you are viewing. A
disclosure refusal blocks its download until the report confirms access again. The reminder
export includes kind, completion date and deadline outcome. A denied or oversized export returns
an explanation in place of a file.

## Daily reminder digest

An Owner can add a digest email address to a person's profile. With SMTP enabled, ClaimCore makes
at most one delivery attempt per business day for an eligible recipient with reminder counts for
readable work assigned to them. The message links to the Reminders queue, which supplies current
dates and attention.
See [Digest configuration](operating.md#daily-reminder-digest).

### Case reminder queue

The case workspace supplies a **Case reminders** link for a readable case that has not been erased.
It opens the queue with that case reference, an explicit **Any** state selection and inactive cases
included, so pending, completed and cancelled reminders on closed or voided cases remain reachable. The queue visibly identifies whether the restriction uses a case
reference or ID. State, due, assignment and text selections compose with it and survive reload.
**Clear case restriction** removes only the case target, preserving the remaining filters and
starting from the first page. The query still checks current access; the caption does not establish
that a requested case exists or is readable.
