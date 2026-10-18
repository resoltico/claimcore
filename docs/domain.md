# Domain contract

ClaimCore records a claims handler's basic case facts and payment information. It is a register of
operator assertions, not an insurer, coverage adjudicator, payment service, or general workflow
engine.

## Business record

<a id="cc-dom-001"></a>

### CC-DOM-001 — Exact public business record

`CaseFields` contains exactly thirteen business fields. Technical values such as operation IDs,
record revisions, timestamps, database users, and available commands remain outside it.

| No. | Meaning                                     | JSON key                   | Source at opening                                                                                 |
| --: | ------------------------------------------- | -------------------------- | ------------------------------------------------------------------------------------------------- |
|   1 | Incident date                               | `incidentDate`             | Required registration input.                                                                      |
|   2 | Date this handler was first notified (FNOL) | `incidentNotificationDate` | Required registration input.                                                                      |
|   3 | Country of incident                         | `incidentCountry`          | Required registration input.                                                                      |
|   4 | Claimant name                               | `claimantName`             | Required registration input.                                                                      |
|   5 | Responsible insurer                         | `insurerName`              | Required registration input.                                                                      |
|   6 | Amount claimed                              | `claimedAmount`            | Required registration input.                                                                      |
|   7 | Currency of amount claimed                  | `claimedCurrency`          | Required registration input.                                                                      |
|   8 | Handler's case reference                    | `caseReference`            | Required top-level command target.                                                                |
|   9 | Payment decision date                       | `paymentDecisionDate`      | `null` until recorded.                                                                            |
|  10 | Amount to be paid                           | `payableAmount`            | `null` until recorded.                                                                            |
|  11 | Currency of amount to be paid               | `payableCurrency`          | `null` until recorded.                                                                            |
|  12 | Payment date                                | `paymentDate`              | `null` until recorded.                                                                            |
|  13 | Case status                                 | `status`                   | Core-derived: initialized `OPENED`, changed by `CLOSE`/`REOPEN`; never supplied as command input. |

Default English labels and explanations come from
[Domain field descriptors](../src/ClaimCore.Domain/FieldDefinitions.fs) and
[command descriptors](../src/ClaimCore.Domain/CommandDefinitions.fs), shared by CLI discovery and
generated browser presentation. The insurer is the handler’s recorded assertion for this claim.

Every rendered case contains all thirteen keys. Unrecorded decision and payment values are `null`,
never zero or an invented date. `OPEN` accepts the seven registration values plus the command's
top-level case reference; the core creates status `OPENED` rather than accepting it as input.

The case reference is immutable: 1–80 printable non-space ASCII characters (U+0021–U+007E).
Resource identity folds ASCII letter case within one installation; snapshots retain the initially
accepted spelling. Exact operation content retains the caller’s spelling, so recasing a request
under an already used operation ID conflicts. The claimant may be a person or an organization.
The record has no claimant type, policy, coverage, reserve, banking, address, notes, attachments
or correction-reason fields.

## State and commands

<a id="cc-dom-002"></a>

### CC-DOM-002 — Core-owned command availability and revalidation

Status is either `OPENED` or `CLOSED`. Status is independent of payment: recording payment does not
close a case, and a case may close without a decision or payment.

| Command              | Effect                                                                                                                                             |
| -------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `OPEN`               | Create an `OPENED`, undecided case at expected revision zero.                                                                                      |
| `AMEND_REGISTRATION` | Replace registration facts on an open, undecided case; keep its reference.                                                                         |
| `CORRECT_CASE`       | Atomically correct existing registration, decision, and payment facts on a decided, paid, or closed case without changing its reference or status. |
| `DECIDE`             | Record or replace an unpaid decision tuple.                                                                                                        |
| `WITHDRAW_DECISION`  | Remove an unpaid decision tuple.                                                                                                                   |
| `RECORD_PAYMENT`     | Record the date the full positive decided amount was paid.                                                                                         |
| `CLEAR_PAYMENT`      | Remove an erroneously recorded payment date while retaining the decision.                                                                          |
| `CLOSE`              | Set an open case to `CLOSED` without changing payment progress.                                                                                    |
| `REOPEN`             | Set a closed case to `OPENED` without changing payment progress.                                                                                   |

Status and payment progress are separate state axes. A typical paid lifecycle is `OPEN` → `DECIDE` →
`RECORD_PAYMENT`; `CLOSE` and `REOPEN` only change status. `CLEAR_PAYMENT` returns a paid case to its
decided-but-unpaid state. Each newly accepted command advances exactly one revision.

Ordinary editing of a closed case requires reopening it; `CORRECT_CASE` can correct its facts
while keeping it closed. A paid date must be cleared before its decision can be withdrawn,
and that unpaid decision must be withdrawn before registration facts can be amended.
Clearing a record does not reverse a real transfer.

`AMEND_REGISTRATION` requires an open, undecided case. `CORRECT_CASE` applies to a decided, paid or
closed case and always supplies all three tagged groups. Registration is `KEEP` or `REPLACE` with
all seven inputs. Decision is `KEEP`, `REPLACE` with its complete date/amount/currency tuple, or
`CLEAR`; payment is
`KEEP`, `REPLACE` with its date, or `CLEAR`. `KEEP` reads accepted state under the authoritative ledger
lock, never a browser copy. A correction cannot be all-KEEP or make a no-op revision, add a missing
decision or payment, change reference or status, or create an intermediate cleared snapshot.
Clearing a paid decision requires clearing payment. Replacing a paid decision requires explicit
payment replacement/reaffirmation or clearing payment so an old payment assertion is never silently
carried to a new payable amount. A successful correction writes one complete revision; a refusal
writes neither current state nor accepted history.

Domain derives command eligibility from current state. Actor-bound current views filter that advice
through Application's current resource authorization; installation context offers `OPEN` only
when that actor may create a case. Correction offers fix unauthorized groups at KEEP and
case-scoped history advice reflects the same resource authority. A case-scoped editing grant does not imply installation creation.
Execution revalidates the command against authoritative state, current authority and expected
revision. An advertised command is advisory, not commit authority; adapters do not add another
action policy.

<a id="cc-life-001"></a>

### CC-LIFE-001 — Case stewardship

Disposition is separate from business status and payment facts. `VOID` requires a reason and
blocks business commands while preserving every accepted snapshot. `REINSTATE` requires a
reason and restores ordinary case work on a voided record. Reasons contain 1–200 Unicode
scalars under the same admission profile as newly asserted names: no surrounding whitespace,
controls, line/paragraph separators or explicitly forbidden invisible/directional characters.
Joining characters and other permitted format characters remain accepted.
Neither act reverses a payment or changes a business field.

`ERASE` removes the case's business payload and its whole history from the live database.
It retains the case reference, revision and ledger metadata. One current human `DATA_STEWARD`
can execute any stewardship act; a `CASE_HANDLER` cannot. Every accepted act advances one
revision. Erasure is final: the reference cannot be opened again and erased operations cannot
be replayed as retained requests. Its physical limits are in [Operations](operations.md).

## Scalars

- Dates use real `YYYY-MM-DD` calendar values from year 0001 through 9999. Present dates follow
  incident ≤ notification ≤ decision ≤ payment. Newly asserted event dates cannot be later than the
  supplied business date, derived from the database instant and installation's immutable time zone.
  Reaffirming an unchanged accepted date does not invalidate that value after a clock change.
  This applies to ordinary registration amendments, unpaid decision replacements and explicit
  corrections. Each changed date is checked independently. Clearing a decision or payment removes
  its date from current state, so recording it again is a
  new assertion.
- Input amounts match `(0|[1-9][0-9]{0,17})(\.[0-9]{1,4})?`: no sign, exponent, leading integer
  zeroes, or bare decimal point. The maximum is `999999999999999999.9999`; values are rejected rather
  than rounded. Accepted views render the same decimal value canonically, without insignificant
  trailing zeroes.
- Currencies are exactly three uppercase ASCII letters. This checks syntax, not ISO membership.
- Incident country is exactly two uppercase ASCII letters. This checks syntax, not ISO membership;
  `ZZ` means unknown/international. Localized country names are advisory display hints.
- Retained business text is non-empty and excludes NUL, unpaired surrogates and Unicode
  noncharacters as permanent format invariants and follows the [permanent byte bounds](architecture.md#durable-records). New name assertions contain 1–200 Unicode
  scalars, no surrounding whitespace,
  controls, line/paragraph separators, or the forbidden invisible/directional characters described
  by the [shared Domain text policy](../src/ClaimCore.Domain/Validation.fs). Names and stewardship
  reasons share this profile: U+200C (ZWNJ), U+200D (ZWJ), U+180E (Mongolian vowel separator),
  permitted selectors and private-use characters remain accepted. Unicode category Format is
  not a blanket refusal. Display warnings expose suspicious code points without rewriting data.
- Format and admission are separate. Decoding, generic schemas, SQL and verification enforce
  permanent scalar syntax and state invariants. Current admission policy judges newly asserted
  values only: unchanged retained names and dates may be reaffirmed. Tightening admission never
  invalidates accepted records. Browser input hints describe policy without imposing it on reads.

Canonical operation identity retains authored amount spelling and exact reference text.
An uncertain retry preserves the original identity and bytes; see [API](api.md).

## Revisions and history

Every newly accepted case operation advances one revision. A fresh request carries its expected
revision; execution rechecks it against locked state. While request content is retained, an
authorized exact replay returns the original acceptance metadata without another entry or revision.
Snapshot disclosure uses the [current authorized context](architecture.md#cc-auth-001);
[erased requests](#cc-life-001) cannot be replayed. Different content under a used identity is
refused. Rejection records nothing.

Expected revisions are integers from 0 through M = 9007199254740991; accepted revisions and
ledger sequences are 1 through M. M − 1 may advance to M; M is terminal for new acts. Global
head exhaustion is checked before arithmetic, while exact authorized replay remains possible.
Advertised commands are advice; receipts and history advertise no commands. Read current state before
starting a different operation. Domain owns these rules, and adapters cannot add business fields.

Domain uses one opaque claim and case record. `CaseView` is live (Active/Voided disposition) or
erased; `CaseSnapshot` keeps reference and revision outside that view. `CaseAct` covers the nine
business commands and three stewardship acts. Domain decides all record transitions.
