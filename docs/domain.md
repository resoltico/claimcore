# Case work

ClaimCore records the handler's assertions about a claim. It records facts, decisions and payment dates;
it does not adjudicate coverage or transfer money.

Use [getting started](getting-started.md) to try the register, or the [generated CLI reference](cli.md) to
look up an act. The sections below explain the business meaning and the limits of correction and erasure.

## Business record

<a id="cc-dom-001"></a>

### CC-DOM-001 — Exact public business record

A case has thirteen business fields: incident date, this handler's first notification date, incident country,
claimant, responsible insurer, claimed amount/currency, case reference, decision date, payable amount/currency,
payment date and open/closed status. Technical IDs, revisions, timestamps and advice stay outside this record.
The [field table](../src/ClaimCore.Contract/Fields/FieldTable.fs) owns JSON keys and labels.

Opening supplies the seven registration values and top-level reference. Decision and payment groups begin as
null; status begins OPENED. The view places the seven registration members in `registration`, the three
decision members in `decision`, and the payment date in `payment`. A recorded group contains all its members;
null means the whole group is unrecorded, with no invented zero or date. Claimant may identify a person or
organization; insurer records the handler's assertion.

The immutable reference uses printable non-space ASCII, within the [reference format](../src/ClaimCore.Contract/Formats.fs).
Resource lookup folds ASCII letter case within an installation; accepted snapshots retain the initial spelling.
Operation identity compares amounts by value and references after ASCII case folding. Respelled equal values
under the same operation ID replay the original acceptance. The ledger retains the request as authored.
There are no policy, coverage, reserve, banking, address, notes or attachment fields.

## State and acts

<a id="cc-dom-002"></a>

### CC-DOM-002 — Core-owned command availability and revalidation

A case may be OPENED or CLOSED independently of its payment progress. Payment does not close a case; closing
needs no decision or payment. Use `claimcore describe` for current inputs and correction choices.

- OPEN creates revision one with expected revision zero. AMEND_REGISTRATION replaces registration on an open,
  undecided case.
- DECIDE and WITHDRAW_DECISION record or remove an unpaid decision. RECORD_PAYMENT and CLEAR_PAYMENT record
  or remove its payment date. Clearing recorded facts does not reverse a real transfer.
- CLOSE and REOPEN change status. Ordinary editing of a closed case requires reopening.
- CORRECT changes existing facts atomically while preserving reference and status, including on a closed case.

Before ordinary registration editing, clear payment and withdraw the decision. A correction instead supplies
all three tagged groups. Registration may KEEP or REPLACE all registration values. Decision and payment may
KEEP, REPLACE their complete tuples, or CLEAR. KEEP reads accepted state under the ledger lock.

A correction cannot be all-KEEP, make a no-op revision, add a missing decision/payment, or silently carry an
old payment assertion to a replaced payable amount. Clearing a paid decision also clears payment. Replacing
it requires explicit payment replacement/reaffirmation or clearing. Acceptance creates one complete revision;
refusal creates no intermediate state. [Case transitions](../src/ClaimCore.Domain/Case.fs) own these rules.

Current core advice filters eligible work by the actor's resource rights. Unauthorized correction groups stay
KEEP. Case-scoped handling does not grant installation creation. Execution rechecks rights, state and expected
revision; an offered command or preview grants no commit authority.

<a id="cc-life-001"></a>

### CC-LIFE-001 — Case stewardship

Disposition is independent of status and payment. VOID records an entry-error reason and blocks business work
while preserving history. REINSTATE records a reason and restores ordinary work on a voided case. Both leave
business fields unchanged. Reasons use the same current text admission profile as names.

A human DATA_STEWARD may void, reinstate or erase; CASE_HANDLER alone cannot. Each acceptance advances one
revision. ERASE clears the case's business projection and complete live payload history, retaining reference,
revision and ledger metadata. It is final: the reference cannot be opened again and erased request content
cannot replay. [Operations](operations.md#erasure-limits) explains the physical limits.

## Scalars

Permanent syntax lives in [Contract Formats](../src/ClaimCore.Contract/Formats.fs); current admission is composed
in [Domain field definitions](../src/ClaimCore.Domain/FieldDefinitions.fs) and [validation](../src/ClaimCore.Domain/Validation.fs).
Retained reads and verification enforce permanent format/state invariants, without applying today's admission
to old assertions. Unchanged retained names and dates may be reaffirmed.

Dates are real YYYY-MM-DD values in years 0001–9999. Present dates follow incident ≤ notification ≤ decision
≤ payment. Newly asserted event dates cannot exceed the supplied business date, derived from the database
instant and installation zone. Clearing then recording a date makes a new assertion; reaffirming an unchanged
date remains possible after a clock change.

Amounts are unsigned decimal strings, with at most eighteen integer and four fractional digits. No exponents,
redundant leading zeroes or rounding are accepted. Views show canonical decimal value; replay identity compares that value while the retained request
keeps the authored amount spelling. Currency and country check uppercase ASCII syntax, not ISO
membership. ZZ means unknown/international country; translated country names are display hints.

Retained business text is nonempty, excludes NUL, unpaired surrogates and Unicode noncharacters, and obeys
permanent byte bounds. Newly asserted names/reasons contain 1–200 Unicode scalars, without surrounding
whitespace, controls, line/paragraph separators or explicitly forbidden invisible/directional characters.
Joining characters and permitted format characters remain allowed. The [display policy](../src/ClaimCore.Contract/Messages/TextDisplay.fs)
exposes bounded suspicious-scalar warnings without rewriting raw values.

## Revisions and history

Execution compares expected revision with locked current state. Retained exact replay preserves original
acceptance without another entry, subject to [current disclosure](architecture.md#cc-auth-001). Different
content under a used ID refuses. Rejection changes no accepted state.

The maximum safe revision/sequence is defined by [Revision](../src/ClaimCore.Domain/CaseModel.fs).
The last available value may be reached once; new work then refuses before arithmetic. Authorized exact replay
remains possible at capacity. Read current state before preparing a different operation. If delivery is
uncertain, preserve the original frame and use the [resolution flow](api.md#cc-cli-003).
