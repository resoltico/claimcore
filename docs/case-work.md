# Case work

ClaimCore records a handler's assertions about a claim. It records facts, decisions and payment dates;
it does not adjudicate coverage or transfer money. Start with the Cases worklist, or follow
[getting started](getting-started.md) to record a synthetic case.

## Find work

Cases opens at **Needs attention**. Choose another standard view in the navigation: Mine, Overdue,
Due today, No next action, Unassigned, Undecided, Decided and unpaid, Closed, Voided or All. Each view
shows the count you can currently read. Additional filters narrow the selected view.

**Mine** follows explicit responsibility, including assigned cases that are no longer active. **Unassigned**
means active, open cases with nobody responsible. An overdue plan still counts as a plan. A case can
need attention for several reasons at once; the overall count counts that case once. Paid cases ready
to close also need attention.

Search by reference, claimant, insurer, incident description or handling summary. Percent, underscore
and backslash are literal text. An exact visible reference match has a direct open link even when that
case is outside the current view. A case you cannot read is not disclosed.

Rows show names, amounts, the current headline, attention, responsibility and the next action. Money
keeps meaningful fractional precision and uses the currency's normal minimum decimal places. Dates use
your selected format. Open a row to see the case; use **Load more** for another page.

The CLI uses the same views and filters:

```sh
claimcore case list --view needs-attention
claimcore case list --view mine
claimcore reminder list --view overdue
```

Responsibility and access are separate. Being assigned never grants permission to read or change a
case. Due dates use the installation's business date, shown once in the web shell. An absent or closed
browser does not deliver alarms.

`claimcore case summary` reports counts and exact totals for the same selection. Totals stay separate
by currency, with no conversion or rounding. See [query and export details](integrating.md#worklist-queries-and-summary-rows)
for filters, independent pages and machine formats.

## Open the case workspace

The workspace leads with the claimant, reference, insurer, incident and notification dates, and current
headline. The tracker shows recorded Opened, Decided, Paid and Closed stages using their actual dates.
A case may close without a decision or payment; those absent stages say **Not recorded**. Voided, erased
and just-reinstated cases have a status panel instead. The reinstated panel remains until the next
case-fact change; changing work or reminders does not replace it.

The primary button is the core's offered recommendation. **Edit facts** groups the other offered fact
actions; stewardship actions are separate. Claim, decision and payment details stay together. Next
actions, responsibility and handling summary have their own controls. **Activity** combines fact, work
and reminder changes, with Everything, Facts and Work filters. **Record details** holds technical
identifiers and versions in one place.

A deep link to an unavailable action keeps the workspace visible and explains the core reason with an
offered alternative. Permission failures name an applicable role only when you can read the case.
An unavailable case is not distinguished from one outside your access.

Case links use `/cases/id/CASE-ID`; exact reference links use `/cases/reference/REFERENCE` with an encoded
reference segment. A reference that looks like a UUID stays a reference. Task links append `/acts/ACT`.
Follow the supplied links rather than constructing a different selector.

## Plan case work

Choose a responsible person, write a Handling summary, and add the next actions from the workspace.
Responsibility is separate from access: assigning someone never gives them permission to read or change
the case. The picker shows eligible people by their saved names. If someone later loses access or is
disabled, the assignment remains visible as unavailable so another handler can take over.

The Handling summary describes the current handling situation. It is separate from Incident description,
which describes what happened. Summary author and time follow the last actual text change; changing
responsibility does not claim authorship. You can update the summary on an open or closed active case;
changing responsibility needs an open active case.

Each next action has its own planned date and Pending, Completed or Cancelled state. Add several when
needed. An overdue action still counts as a plan. Planned dates can be in the past or future. These dates
help organize work; ClaimCore does not send alarms or automatically complete actions.

Choose **Complete** when you have marked an action done, **Cancel** when it is no longer needed, or
**Reschedule** to change its Pending action or date. The completion time records when you marked it,
not proof of when an external event happened. Reopening a Completed or Cancelled action needs a date.

**Complete and plan next** completes one action and creates its replacement together. If either part
cannot be recorded, neither changes. Completing the last Pending action without a replacement is also
valid; the case then needs a next action.

Closing or voiding a case keeps its Pending actions as inactive. Reopening or reinstating brings back
those same dates. You may complete or cancel remaining actions on a closed active case. Erasing a case
clears its saved work and action text, including earlier versions.

## Work attention and queues

Navigation counts the work you can read. A case can need attention because it lacks a next action,
has overdue or due-today work, lacks a responsible person, has an unavailable responsible person, or is paid and ready
to close. A case with several reasons still counts once in Needs attention.

Open **Reminders** to see actions across cases. Use its standard views and filters to find Pending,
Overdue, Due today, Upcoming, Completed or Cancelled work. Actionable views use active open cases;
Completed and Cancelled actions remain reviewable on readable cases whose contents have not been erased.

Choose **Manual filters** to filter independently of a standard reminder view and enable **Include
inactive work**. Your other filters stay in place. Turn Manual filters off to return to the default
work view; standard views keep their own boundaries even when other filters are selected.
A case with both overdue and today actions can appear in both corresponding action queues.

Counts and pages can change as others work. Refresh to see the latest results. Opening a case and then
planning its work are separate changes. Financial totals count each case once, regardless of how many
actions it has. Search in Reminders finds current action text.

## Open a case

Choose **Open a case** and enter your reference, claimant, insurer, incident and notification dates,
incident country, and claimed amount with its currency. Incident description is optional. Review the
facts and choose the named confirmation to record the case.

Your reference cannot change or be reused after erasure. A claimant can be a person or organization.
The case starts open and undecided. Responsibility, Handling summary and next actions start empty;
add them from the workspace.

## Decide and record payment

Choose **Decide** to record the decision date and payable amount with its currency. An unpaid decision
can be changed or withdrawn when offered. **Record payment** needs a positive recorded decision and
records the payment date. **Clear payment** removes that date. These actions record assertions; they do
not transfer money or reverse a transfer. Recording payment does not automatically close the case.

Missing decision or payment details mean they have not been recorded. ClaimCore does not invent dates
or zero amounts. Dates must be real and follow incident, notification, decision and payment order.
Newly asserted event dates cannot be after the installation's business date.

## Close or reopen

Choose **Close** when handling is finished. A decision or payment is not required. Closing preserves the
recorded facts. **Reopen** makes the case open again. Reopen before ordinary financial editing of a
closed case; use **Correct** when its separate correction rules fit the change.

## Correct existing facts

Choose **Correct** to change already recorded facts together. An open case needs an existing decision;
a closed case can be corrected with or without recorded financial stages. Select only what should
change and review the proposed differences. Unselected facts stay as they are.

Amount and currency always travel together. A correction cannot create a missing decision or payment.
Clearing a paid decision also needs payment cleared. Updating a paid decision requires an explicit
choice to replace, reaffirm or clear payment. If the complete correction is refused, nothing changes.

## Case stewardship

A data steward can **Void**, **Reinstate** or **Erase** when offered. Void records an entry-error reason
and blocks further business work while keeping the facts. Reinstate records a reason and brings the
case back. Both preserve recorded financial details.

Erase permanently clears the case's saved business contents, including earlier versions and work.
The reference stays reserved and cannot be opened again. Read the consequence carefully and type the
shown reference to confirm. See [erasure limits](operating.md#erasure-limits) for backups and other copies.

## Review, confirmation and delivery

Forms show short hints; open the information tip for precise rules. Dates and amounts use your selected
format. Changing format while editing does not reinterpret an amount you already entered. An input
error lists every reported issue and links to the affected fields.

Review shows what will change. Routine actions use one named confirmation. Stewardship actions explain
their consequence; irreversible actions require the shown reference or change identifier. In CLI
automation, an irreversible human command also needs a matching `--confirm` value. See the
[CLI reference](cli.md) for exact syntax and the [integration guide](integrating.md) for machine calls.

Someone may change a case while you review. A conflict keeps your draft and shows the changes you may
read, with current author names. Compare it with the latest case before reviewing a fresh change.
Financial facts, case work and individual reminders can change independently.

## Recover an uncertain result

If ClaimCore cannot confirm whether a change was recorded, open **Unconfirmed changes**.
The interface checks automatically. A pending result shows when a final answer becomes available;
it does not mean rejection. After the operation's ten-minute window, a locked check establishes final
absence for your account without changing the register. An enabled registered account can check its own
metadata without current case roles. **Send again** repeats the identical dispatched request and ID.

Reloading or closing a tab keeps delivery identifiers but cannot reconstruct the authored request.
The interface asks before discarding an unsent draft or retained retry. See
[delivery recovery](integrating.md#delivery-recovery) for detailed result meanings and CLI steps.

## Review recorded changes

**Activity** combines case facts, work and reminder changes. Filter Everything, Facts or Work and choose
**Load more** when offered. Names come from current saved profiles. Profile erasure replaces the saved
name with an anonymous account reference. Earlier values appear only when your history access allows
them; erased or withheld contents remain hidden.

The concise event and highlighted differences come from Core. Choose **Record details** for the complete
exact differences and the operation identity, revision and recorded time used for support.

In the CLI use `claimcore case timeline REFERENCE`, optionally with `--filter facts` or `--filter work`.
JSON and CSV exports retain canonical values. See [integration](integrating.md) for formats, history permissions and live traversal.
