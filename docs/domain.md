# Domain contract

ClaimCore records a claims handler's basic case facts and payment information. It is a register of
operator assertions, not an insurer, coverage adjudicator, payment service, or general workflow
engine.

## Business record

<a id="cc-dom-001"></a>
### CC-DOM-001 — Exact public business record

`CaseFields` contains exactly thirteen business fields. Technical values such as operation IDs,
record revisions, timestamps, database users, and available commands remain outside it.

| No. | Meaning | JSON key | Source at opening |
|---:|---|---|---|
| 1 | Incident date | `incidentDate` | Required registration input. |
| 2 | Date this handler was first notified (FNOL) | `incidentNotificationDate` | Required registration input. |
| 3 | Country of incident | `incidentCountry` | Required registration input. |
| 4 | Claimant name | `claimantName` | Required registration input. |
| 5 | Allegedly responsible insurer | `insurerName` | Required registration input. |
| 6 | Amount claimed | `claimedAmount` | Required registration input. |
| 7 | Currency of amount claimed | `claimedCurrency` | Required registration input. |
| 8 | Handler's case reference | `caseReference` | Required top-level command target. |
| 9 | Payment decision date | `paymentDecisionDate` | `null` until recorded. |
| 10 | Amount to be paid | `payableAmount` | `null` until recorded. |
| 11 | Currency of amount to be paid | `payableCurrency` | `null` until recorded. |
| 12 | Payment date | `paymentDate` | `null` until recorded. |
| 13 | Case status | `status` | Core-derived as `OPENED`; never supplied by a command. |

Every rendered case contains all thirteen keys. Unrecorded decision and payment values are `null`,
never zero or an invented date. `OPEN` accepts the seven registration values plus the command's
top-level case reference; the core creates status `OPENED` rather than accepting it as input.

The case reference is immutable and compared case-sensitively within one installation. ClaimCore
retains its accepted Unicode content without normalization or case folding. The claimant may be a
person or an organization; the model deliberately does not add claimant type, policy, coverage,
reserve, banking, address, notes, attachments, or correction-reason fields.

## State and commands

<a id="cc-dom-002"></a>
### CC-DOM-002 — Core-owned command availability and revalidation

Status is either `OPENED` or `CLOSED`. Status is independent of payment: recording payment does not
close a case, and a case may close without a decision or payment.

| Command | Effect |
|---|---|
| `OPEN` | Create an `OPENED`, undecided case at expected revision zero. |
| `AMEND_REGISTRATION` | Replace registration facts on an open, undecided case; keep its reference. |
| `DECIDE` | Record or replace an unpaid decision tuple. |
| `WITHDRAW_DECISION` | Remove an unpaid decision tuple. |
| `RECORD_PAYMENT` | Record the date the full positive decided amount was paid. |
| `CLEAR_PAYMENT` | Remove an erroneously recorded payment date while retaining the decision. |
| `CLOSE` | Set an open case to `CLOSED` without changing payment progress. |
| `REOPEN` | Set a closed case to `OPENED` without changing payment progress. |

Status and payment progress are separate state axes. A typical paid lifecycle is `OPEN` → `DECIDE` →
`RECORD_PAYMENT`; `CLOSE` and `REOPEN` only change status. `CLEAR_PAYMENT` returns a paid case to its
decided-but-unpaid state. Each newly accepted command advances exactly one revision.

A closed case must be reopened before editing. A paid date must be cleared before its decision can be
withdrawn, and that unpaid decision must be withdrawn before registration facts can be amended.
Clearing a record does not reverse a real transfer.

Only the core derives available commands from current state, and execution revalidates the command
against authoritative state and expected revision. An advertised command is advisory, not commit
authority; adapters do not add another action policy.

The decision date, payable amount, and payable currency are all present or all absent. A payment date
requires a complete decision, a positive payable amount, and a date on or after the decision. Zero is
a valid decision amount but cannot be marked paid. The payable amount and currency need not match the
claim.

## Scalars

- Dates use real `YYYY-MM-DD` calendar values from year 0001 through 9999. Present dates follow
  incident ≤ notification ≤ decision ≤ payment. Newly recorded event dates cannot be later than the
  host-supplied business date.
- Input amounts match `(0|[1-9][0-9]{0,17})(\.[0-9]{1,4})?`: no sign, exponent, leading integer
  zeroes, or bare decimal point. The maximum is `999999999999999999.9999`; values are rejected rather
  than rounded. Accepted views render the same decimal value canonically, without insignificant
  trailing zeroes.
- Currencies are exactly three uppercase ASCII letters. This checks syntax, not ISO membership.
- References allow 1–80 Unicode scalar values, country 1–100, and names 1–200. Blank values,
  surrounding whitespace, control characters, and malformed Unicode are rejected.

Canonical request identity deliberately retains authored amount spelling and exact reference text;
read [CLI and protocol](cli.md#canonical-request-identity-and-recovery) before constructing retries.

## Revisions and history

Every newly accepted operation increments the case revision once. A command carries the revision it
was prepared against; a fresh stale command is rejected. An exact replay of an accepted operation
returns its retained receipt without another revision. An exact retained but unaccepted preparation
made stale by a different commit directs the operator to Recovery without inventing a fresh review.

Revisions remain nonnegative signed 64-bit values below `Int64.MaxValue`; restored snapshots must be
positive. A case at `Int64.MaxValue - 1` advertises no new command, and execution refuses another
transition rather than overflowing or creating an unrepresentable terminal revision. Exact replay
remains a read of the original accepted receipt.

Current views may advertise state-derived commands. Receipts and history are snapshots and advertise
no actions. Read the current case before preparing another change. Safe recovery from an uncertain
result is defined in [CLI and protocol](cli.md#canonical-request-identity-and-recovery).

Changing this contract requires coordinated domain, codec, CLI, Web contract, schema, migration,
documentation, and test changes. Do not extend the record through an adapter-only field.
