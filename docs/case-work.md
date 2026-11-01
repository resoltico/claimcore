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

Live rows show claimant, insurer, claimed amount/currency, any payable amount/currency,
notification age and last-change time. The main badge prioritizes erasure, voiding and closure over
payment progress. Erased rows show reference, erasure state and last-change time. Listing
requires the same case-read rights as opening a case. Use Load more for another page; each page checks
current rights and uses a separate snapshot.

The browser keeps selected filters, search and order in the URL. Follow its case links to preserve exact
references, including values that need URL escaping. The CLI supports the same filters through
`case list`; [Integrating](integrating.md#worklist-queries-and-summary-rows) covers query and export details.

Responsibility is an explicit case fact, separate from access grants. My cases selects cases whose
responsible account is yours; Unassigned selects cases without a responsible account. Neither selection
changes who may read or update a case. All accessible cases is the initial view. The CLI uses
`claimcore case list --assignment MINE` or `--assignment UNASSIGNED`.

Follow-up is an optional planned date, including future dates. Overdue, Today, Upcoming and Unscheduled
apply only to active, open cases; closed, voided and erased cases are Inactive. Follow-up ordering puts
dated work first and absent dates last, with stable reference ties. Page and summary
responses each report their own database observation, installation business date and zone; use that basis
rather than the browser clock. `--order notified` still orders by the current notification assertion.

`claimcore case summary` uses the same filters to count lifecycle, status and progress and to sum
claimed and payable amounts per currency. Counts remain inside the selected filter. Voided facts
contribute when selected; erased cases contribute only to lifecycle counts. Totals are exact, with
no rounding or currency conversion.

The browser keeps responsibility and due filters in the URL. Summary counts include those dimensions
inside the selected filter. Totals and the loaded page may come from different observations and display
their own date basis. Row age uses the page's business date.

## Plan case work

UPDATE_WORK changes responsibility, follow-up, or both atomically while a case is active and open.
Each field is independently set or cleared; omission leaves it unchanged. At least one field must change.
A human CASE_HANDLER with case access can manage work. Service handlers keep their existing business
rights but cannot manage responsibility or follow-up.

The case-scoped candidate picker discloses only eligible human accounts' subject and ActorId, in bounded
pages. A selected account must still be enabled and hold both case-read and case-work rights when the
write executes. Responsibility grants no access. If an accepted assignee later loses eligibility, the
case retains its assignment and marks the account unavailable. Duplicate subjects remain distinguishable
by ActorId. Closing or voiding retains work; reopening or reinstating restores its active due meaning.
Erasure removes work with the other business payloads.

## Open a case

Choose OPEN when it is offered, then supply the reference and every registration value: incident date,
this handler's first notification date, incident country, claimant, responsible insurer, claimed amount
and currency. Review the proposed facts and confirm to record revision one. Acceptance returns to the case
page with a notice naming the act and revision. Status begins OPENED;
decision and payment begin unrecorded.

The reference is immutable printable non-space ASCII within the
[reference format](../src/ClaimCore.Domain/Values.fs). Lookup folds ASCII letter case in one installation;
snapshots retain the creation spelling. The stable CaseId is the accepted OPEN operation UUID; the
reference and CaseId remain reserved after erasure. A claimant may be a person or organization; insurer
records the handler's assertion. There are no policy, coverage, reserve, banking, address, notes or attachment fields.

AMEND_REGISTRATION updates selected registration facts only while the case is open and undecided. Before ordinary
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

Registration supports KEEP or UPDATE. Decision supports KEEP, UPDATE or CLEAR; payment supports KEEP,
complete REPLACE, REAFFIRM or CLEAR where current advice permits it. UPDATE asserts only selected facts.
A money assertion always includes both amount and currency; the pair cannot be updated halfway.
Omitted facts retain their accepted values under the ledger lock. Groups the actor cannot change remain
KEEP. Interactive CLI correction defaults omitted groups to KEEP and shows the selected actions in review.

A correction cannot be all-KEEP, create a no-op revision, or add a missing decision/payment. Clearing a paid
decision also requires clearing payment. Updating a paid decision requires explicit authorized payment replacement,
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
calendar; country/currency suggestions still permit free entry. Authored amounts retain their entry display locale, so later preference changes do not reinterpret
their meaning before preview. Canonical prefilled values retain their canonical meaning. Input refusals focus a summary listing every issue; follow its links to the affected
controls. Amount/date entry is reviewed in canonical wire
form before confirmation. Language, display locale and theme are separate presentation choices;
suspicious-Unicode warnings do not rewrite authored values. Navigation is locked while a mutation is in
flight. Follow [delivery recovery](integrating.md#delivery-recovery) if submission or reload is interrupted.

## Business record

The [business-field declaration](../src/ClaimCore.Domain/CaseFields.fs) owns each field's kind, group and
searchable status. Catalog label and meaning identities select the core labels and explanations.
Technical IDs, revisions, timestamps and advice remain separate from business facts. The view places
complete groups under `registration`, `decision` and `payment`; null means the whole group is unrecorded.
The `work` section holds independently optional responsibility and follow-up facts.

## Scalars

[Domain value modules](../src/ClaimCore.Domain/Values.fs) own permanent syntax; amount and currency values
are in [MoneyValues](../src/ClaimCore.Domain/MoneyValues.fs). The
[validation adapter](../src/ClaimCore.Domain/Validation.fs) assigns failures to input targets. Retained
reads and verification enforce permanent format/state invariants without applying today's admission to
old assertions. Unchanged retained names and dates may be reaffirmed.

Dates are real YYYY-MM-DD values in years 0001–9999. Recorded dates follow incident ≤ notification ≤
decision ≤ payment. Planned follow-up dates may be in the future. Newly asserted event dates cannot exceed the business date derived from the database
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
Expand a disclosed full receipt to inspect its facts and the core-supplied lifecycle and field changes.
Core computes comparison context within each page's read snapshot, including the predecessor of its last
visible entry. Loading another page is not required to obtain that comparison. Withheld or erased facts
are not inferred; an unavailable comparison is shown as unavailable.

History follows the current summary/full disclosure modes. Superseded snapshots need full-history rights;
current case access alone does not grant them. See the [glossary](glossary.md) for project terms.

## Web case navigation

The Cases queue and accepted operations open `/cases/id/CASE-ID`. These canonical links retain case
identity independently of how its reference looks. Exact reference lookup uses
`/cases/reference/REFERENCE` with an encoded reference segment, including UUID-shaped references.
The two selectors remain distinct through reads, history and editing; a UUID-shaped reference is never
inferred to be a CaseId. Update bookmarks that used the former unqualified reference path. Task links add
`/acts/ACT` to the selected route, and actual case actions use the admitted case identity.

The case workspace groups current facts, case work, decision/payment and history. Its primary action is
chosen from the actual offered tasks; closed and voided cases do not automatically recommend reopening.
Technical details keep copyable CaseId and revision available. Targeted edits and review preserve raw
entry context, exact canonical values and the authored expected revision.
