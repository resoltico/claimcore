# Case work

ClaimCore records a handler's assertions about a claim. It records facts, decisions and payment dates;
it does not adjudicate coverage or transfer money. Start with the Cases worklist, or follow
[getting started](getting-started.md) to record a synthetic case.

## Find work in the worklist

Choose a saved view, search, or combine lifecycle, status and progress filters. Open and undecided means
ACTIVE + OPENED + UNDECIDED; Decided and unpaid means ACTIVE + DECIDED. Recently changed orders by the last
accepted change. Closed and Voided select their respective status or lifecycle; All clears the filters.

Search finds substrings in the reference, claimant and insurer. It applies Unicode compatibility
normalization and invariant lowercase to both stored text and queries. Percent, underscore and backslash
are literal characters. Matching follows the same rules in the browser, CLI and service.

Live rows show claimant, insurer, claimed amount/currency, any payable amount/currency, payment progress
and last-change time. Erased rows show reference, revision, erasure state and last-change time. Listing
requires the same case-read rights as opening a case. Use Load more for another page; each page checks
current rights and uses a separate snapshot.

The browser keeps selected filters, search and order in the URL. Follow its case links to preserve exact
references, including values that need URL escaping. The CLI supports the same filters through
`case list`; [Integrating](integrating.md#worklist-queries-and-summary-rows) covers query and export details.

The CLI offers assignment and age-based triage without a separate assignment act:
`claimcore case list --assignment ASSIGNED_TO_ME` selects cases where you hold a valid case-scoped
case-reading grant. Installation access alone does not make a case assigned. `--order notified`
orders oldest notification dates first, with erased cases last. Notification date is the current
registration assertion of this handler's first notification; correcting that fact changes its order.

`claimcore case summary` uses the same filters to count lifecycle, status and progress and to sum
claimed and payable amounts per currency. Counts remain inside the selected filter. Voided facts
contribute when selected; erased cases contribute only to lifecycle counts. Totals are exact, with
no rounding or currency conversion.

The browser starts with My cases when you hold a case-scoped grant and no worklist is selected in the
URL. Choose Oldest first to order by notification; row age counts whole days to the installation's
business date. Open a case by activating its row. Matching totals use the same filters and expand from
one line on a narrow screen. Field groups each have an Explain fields toggle and exact-copy icons.

## Open a case

Choose OPEN when it is offered, then supply the reference and every registration value: incident date,
this handler's first notification date, incident country, claimant, responsible insurer, claimed amount
and currency. Review the proposed facts and confirm to record revision one. Acceptance returns to the case
page with a notice naming the act and revision. Status begins OPENED;
decision and payment begin unrecorded.

The reference is immutable printable non-space ASCII within the
[reference format](../src/ClaimCore.Domain/Values.fs). Lookup folds ASCII letter case in one installation;
snapshots retain the first spelling. A claimant may be a person or organization; insurer records the
handler's assertion. There are no policy, coverage, reserve, banking, address, notes or attachment fields.

AMEND_REGISTRATION replaces registration only while the case is open and undecided. Before ordinary
registration editing of a decided case, clear any recorded payment and withdraw the decision, or use the applicable
correction workflow below.

## Decide and record payment

DECIDE records or replaces an unpaid decision with its date, payable amount and currency.
WITHDRAW_DECISION removes an unpaid decision. RECORD_PAYMENT needs a recorded decision with a positive
payable amount; it records the payment date. CLEAR_PAYMENT removes that recorded date. These acts do not
execute or reverse a transfer.

The progress strip distinguishes Undecided, Decided and Paid. A recorded decision or payment group always
contains all its declared values; an unrecorded group is null, with no invented date or zero. Payment
never closes the case automatically.

## Close or reopen

CLOSE changes status to CLOSED and needs no decision or payment. REOPEN changes it back to OPENED.
Ordinary editing of a closed case requires reopening; correction has its separate rules. Closing leaves
lifecycle, recorded facts and progress intact.

## Correct existing facts

CORRECT changes existing groups atomically while preserving reference and status. An open case needs an
existing decision; a closed case allows correction independently of payment progress. Review the current
and proposed values before confirming.

Registration supports KEEP or complete REPLACE. Decision and payment support KEEP, complete REPLACE or
CLEAR where current advice permits it. KEEP reads accepted state under the ledger lock. Groups the actor
cannot change remain KEEP. Interactive CLI correction also defaults omitted groups to KEEP and shows the
group actions in its review.

A correction cannot be all-KEEP, create a no-op revision, or add a missing decision/payment. Clearing a paid
decision also requires clearing payment. Replacing a paid decision requires explicit payment replacement,
reaffirmation or clearing, even when its payable amount is unchanged. Acceptance creates one complete
revision; refusal leaves the accepted state intact. See the
[correction policy](../src/ClaimCore.Domain/CaseCorrection.fs) and
[transitions](../src/ClaimCore.Domain/Transitions.fs) for the authoritative rules.

## Case stewardship

Lifecycle is ACTIVE, VOIDED or ERASED, independently of open/closed status and payment progress.
VOID records an entry-error reason and blocks business work while retaining facts and history.
REINSTATE records a reason and restores ACTIVE state. Both leave business facts unchanged. Reasons use
the same current text admission as names.

A human DATA_STEWARD may void, reinstate or erase; CASE_HANDLER alone cannot. ERASE clears live business
facts and the case's complete retained payload history, including searchable projections. Reference,
revision and ledger metadata remain. Erasure is final: the reference cannot be opened again, and erased
request content cannot replay. [Operations](operating.md#erasure-limits) explains the physical limits.

## Review, confirmation and delivery

Available acts reflect current state and resource rights. Opening a case requires installation-scope
registration rights; a case-scoped handling grant does not suffice. Advice and preview grant no commit
authority: execution rechecks rights, state and expected revision against the locked current case.

Browser forms use the catalog's field kinds and help. Date controls provide localized segments and a
calendar; country/currency suggestions still permit free entry. Unambiguous locale amounts normalize
before preview. Input refusals focus a summary listing every issue; follow its links to the affected
controls. Amount/date entry is reviewed in canonical wire
form before confirmation. Language, display locale and theme are separate presentation choices;
suspicious-Unicode warnings do not rewrite authored values. Navigation is locked while a mutation is in
flight. Follow [delivery recovery](integrating.md#delivery-recovery) if submission or reload is interrupted.

## Business record

The [business-field declaration](../src/ClaimCore.Domain/CaseFields.fs) owns each field's kind, group and
searchable status. Catalog label and meaning identities select the core labels and explanations.
Technical IDs, revisions, timestamps and advice remain separate from business facts. The view places
complete groups under `registration`, `decision` and `payment`; null means the whole group is unrecorded.

## Scalars

[Domain value modules](../src/ClaimCore.Domain/Values.fs) own permanent syntax; amount and currency values
are in [MoneyValues](../src/ClaimCore.Domain/MoneyValues.fs). The
[validation adapter](../src/ClaimCore.Domain/Validation.fs) assigns failures to input targets. Retained
reads and verification enforce permanent format/state invariants without applying today's admission to
old assertions. Unchanged retained names and dates may be reaffirmed.

Dates are real YYYY-MM-DD values in years 0001–9999. Recorded dates follow incident ≤ notification ≤
decision ≤ payment. Newly asserted event dates cannot exceed the business date derived from the database
instant and installation zone. Clearing then recording a date is a new assertion; reaffirming an unchanged
date remains possible after a clock change.

Amounts are unsigned decimal strings with at most eighteen integer and four fractional digits. There are
no exponents, redundant leading zeroes or rounding. Views show canonical values, and operation identity
compares equal amounts by value while retaining the authored request spelling. Currency and country check
uppercase ASCII syntax, not ISO membership. ZZ means unknown/international country; translated names are
presentation hints.

Retained business text is nonempty, excludes NUL, unpaired surrogates and Unicode noncharacters, and obeys
permanent byte bounds. Newly asserted names/reasons contain 1–200 Unicode scalars without surrounding
whitespace, controls, line/paragraph separators or forbidden invisible/directional characters. Permitted
joining/format characters remain allowed. The
[display policy](../src/ClaimCore.Protocol/Messages/TextDisplay.fs) provides bounded warnings while keeping
raw values intact. Search text has its own 1–100-scalar limit.

## Revisions and history

Execution compares the expected revision with locked current state. Retained exact replay preserves the
original acceptance without another entry, subject to
[current disclosure](integrating.md#disclosure-and-cursors). Different content under a used operation ID
refuses. Rejection changes no accepted state.

Reference identity folds ASCII case and amount identity compares decimal value. Respelled equal values
under the same operation ID replay the original acceptance. The ledger retains the authored request.
Revisions and sequences obey the exact-integer bound in
[typed positions](../src/ClaimCore.Domain/Identifiers.fs), enforced by SQL and durable decoding.

The history timeline starts with the newest accepted revision and loads older changes in bounded pages.
Expand a disclosed full receipt to inspect its facts and compare changed fields with the adjacent prior
revision. If that revision is not loaded, load the preceding page; withheld or erased facts cannot be
compared.

History follows the current summary/full disclosure modes. Superseded snapshots need full-history rights;
current case access alone does not grant them. See the [glossary](glossary.md) for project terms.
