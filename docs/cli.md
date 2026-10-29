# CLI reference

Generated from the semantic Protocol catalog and CLI command grammar. Regenerate with `dotnet run --project eng/ClaimCore.ProtocolGenerator -- --output contracts`; generator `--check` also checks this file.

A synopsis shows required inputs and brackets optional inputs. Choices use lowercase kebab spelling; exact uppercase wire tokens are also accepted. Mutation operations generate an operation ID when omitted. Keep that ID and the exact prepared frame for retries.

Use `--dry-run` to print the prepared JSON frame without authentication. This checks protocol shape and byte limits; domain admission, authorization and current revision are checked by the service. Use `--preview` with a case or authority act to request a review without recording it.

Value-taking verb options also accept `--name=value`; use `--text=--literal` when search text begins with `--`. Switches keep their valueless spelling.

Case worklist filters `--lifecycle`, `--status` and `--progress` may repeat to select a set. Omit a filter to include all values. `--text` searches claimant, responsible insurer and case reference; `--order reference|recent` selects the page order. A continuation cursor is bound to those filters and order.
`case list --format csv` emits UTF-8 RFC 4180 CSV without a BOM, with wire-path headers and CRLF record endings. Null values are empty cells. `case list --all` follows continuation cursors until the end; each page is its own authorized snapshot. Text and CSV use one header; JSON emits one frame per page. A refusal or failure stops traversal and reports the cursor reached.
When stdin and stdout are terminals and text output is selected, omitting `--expected-revision` on an existing-case act reads the case, previews with that revision and a new operation ID, then asks for confirmation. Only an affirmative answer executes the same reviewed request. A concurrent change is still refused by the service. Omitted correction groups mean KEEP and the review shows every group action.

`--preview` remains preview-only. `--dry-run` stays offline and requires an explicit revision. Redirected input or output and `--format json` also require an explicit revision; there is no skip-confirmation flag.

Human operations default to text. Add `--format json` for structured output or `--language en|lv|ar` to select interface text. Display language does not rewrite authored values.

Top-level `claimcore` lists command families. `claimcore describe case open` describes an operation offline; `claimcore schema operation.execute request` shows its wire schema. `claimcore call` reads one JSON frame from stdin; `claimcore session` reads NDJSON.

## Discovery and frame commands

Help, version, describe and schema commands work offline. Call and session dispatch frames through the configured HTTPS service and authentication mode.

- `claimcore help [topic] [--language en|lv|ar]`
- `claimcore version [--json]`
- `claimcore describe [family verb] [--format text|json] [--language en|lv|ar]`
- `claimcore schema name`
- `claimcore schema endpoint request|response`
- `claimcore call`
- `claimcore session`

Help topics: `overview`, `help`, `version`, `describe`, `schema`, `call`, `session`, `configuration`, `delivery`, `examples`, `verbs`, `CLI_PROTOCOL_GUIDANCE`, `CLI_CONFIGURATION_ISSUE`.

## Exit codes

Both executables use the shared Protocol exit-code table. `claimcore-database help` reports its owner-command grammar without opening credentials or the database. Database refusals include a diagnostic; failures include a fault. Usage errors include the applicable synopsis.

| Exit | Meaning | claimcore | claimcore-database |
| --- | --- | --- | --- |
| 0 | Requested work completed. | yes | yes |
| 2 | Input, usage or action was refused. | yes | yes |
| 3 | A known failure; earlier uncertain attempts remain unresolved. | yes | yes |
| 4 | Commit may have happened; retain the exact operation for recovery. | yes | yes |
| 5 | Ledger verification found divergence. | — | yes |
| 64 | CLI command usage was refused before dispatch. | yes | — |
| 70 | Unexpected CLI defect. | yes | — |
| 130 | CLI interrupted or cancelled. | yes | — |

## case open

Open a case

Register the incident, claimant, insurer and amount claimed.

```sh
claimcore case open [--operation-id VALUE] CASE-REFERENCE --incident-date VALUE --incident-notification-date VALUE --incident-country VALUE --claimant-name VALUE --insurer-name VALUE --claimed-amount VALUE --claimed-currency VALUE
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--incident-date` | Incident date — Date on which the incident occurred. Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--incident-notification-date` | Incident notification date (FNOL) — Date when this claims handler was first notified of the incident by anyone, not necessarily the claimant. Not the date another insurer or organisation was notified. Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--incident-country` | Country of incident (ISO alpha-2 code) — Two uppercase ASCII letters identifying the incident country; ISO alpha-2 syntax only, without membership validation. ZZ means unknown or international. Use two uppercase letters. ZZ means unknown or international. |  |  |
| `--claimant-name` | Claimant name — Name of the person or company making the claim. Up to 200 characters. |  |  |
| `--insurer-name` | Responsible insurer — Insurer recorded by the claims handler as responsible for this claim. Up to 200 characters. |  |  |
| `--claimed-amount` | Amount claimed — Amount requested by the claimant. Use ASCII digits and a dot, for example 0.10; up to 4 decimal places. No redundant leading zeros (00.10), signs or commas. |  |  |
| `--claimed-currency` | Currency of claimed amount — Currency in which the amount is claimed. 3 uppercase ASCII letters (A–Z). |  |  |

## case amend-registration

Amend registration

Replace the initial facts. The handler's case reference remains unchanged.

```sh
claimcore case amend-registration [--operation-id VALUE] CASE-REFERENCE --expected-revision N --incident-date VALUE --incident-notification-date VALUE --incident-country VALUE --claimant-name VALUE --insurer-name VALUE --claimed-amount VALUE --claimed-currency VALUE
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Expected revision — The case revision on which this request is based. |  |  |
| `--incident-date` | Incident date — Date on which the incident occurred. Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--incident-notification-date` | Incident notification date (FNOL) — Date when this claims handler was first notified of the incident by anyone, not necessarily the claimant. Not the date another insurer or organisation was notified. Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--incident-country` | Country of incident (ISO alpha-2 code) — Two uppercase ASCII letters identifying the incident country; ISO alpha-2 syntax only, without membership validation. ZZ means unknown or international. Use two uppercase letters. ZZ means unknown or international. |  |  |
| `--claimant-name` | Claimant name — Name of the person or company making the claim. Up to 200 characters. |  |  |
| `--insurer-name` | Responsible insurer — Insurer recorded by the claims handler as responsible for this claim. Up to 200 characters. |  |  |
| `--claimed-amount` | Amount claimed — Amount requested by the claimant. Use ASCII digits and a dot, for example 0.10; up to 4 decimal places. No redundant leading zeros (00.10), signs or commas. |  |  |
| `--claimed-currency` | Currency of claimed amount — Currency in which the amount is claimed. 3 uppercase ASCII letters (A–Z). |  |  |

## case correct

Correct case facts

Correct recorded details on a case with a decision, payment or closed status. All changes are recorded together. Clearing a payment date does not refund money.

```sh
claimcore case correct [--operation-id VALUE] CASE-REFERENCE --expected-revision N --registration keep|replace [--incident-date VALUE] [--incident-notification-date VALUE] [--incident-country VALUE] [--claimant-name VALUE] [--insurer-name VALUE] [--claimed-amount VALUE] [--claimed-currency VALUE] --decision keep|replace|clear [--payment-decision-date VALUE] [--payable-amount VALUE] [--payable-currency VALUE] --payment keep|replace|clear [--payment-date VALUE]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Expected revision — The case revision on which this request is based. |  |  |
| `--registration` | Registration — Keep the current registration details or replace all seven details together. | keep, replace |  |
| `--incident-date` | Incident date — Date on which the incident occurred. Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--incident-notification-date` | Incident notification date (FNOL) — Date when this claims handler was first notified of the incident by anyone, not necessarily the claimant. Not the date another insurer or organisation was notified. Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--incident-country` | Country of incident (ISO alpha-2 code) — Two uppercase ASCII letters identifying the incident country; ISO alpha-2 syntax only, without membership validation. ZZ means unknown or international. Use two uppercase letters. ZZ means unknown or international. |  |  |
| `--claimant-name` | Claimant name — Name of the person or company making the claim. Up to 200 characters. |  |  |
| `--insurer-name` | Responsible insurer — Insurer recorded by the claims handler as responsible for this claim. Up to 200 characters. |  |  |
| `--claimed-amount` | Amount claimed — Amount requested by the claimant. Use ASCII digits and a dot, for example 0.10; up to 4 decimal places. No redundant leading zeros (00.10), signs or commas. |  |  |
| `--claimed-currency` | Currency of claimed amount — Currency in which the amount is claimed. 3 uppercase ASCII letters (A–Z). |  |  |
| `--decision` | Payment decision — Keep the current payment decision, replace its date, amount and currency together, or clear the decision. | keep, replace, clear |  |
| `--payment-decision-date` | Payment decision date — Date when this claims handler decided the amount to be paid. Not recorded until a decision is made. Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--payable-amount` | Amount to be paid — Amount the handler decided should be paid. Not recorded until decided; may differ from the claimed amount. Use ASCII digits and a dot, for example 0.10; up to 4 decimal places. No redundant leading zeros (00.10), signs or commas. |  |  |
| `--payable-currency` | Currency of amount to be paid — Currency of the amount to be paid. Not recorded until decided; may differ from the claimed currency. 3 uppercase ASCII letters (A–Z). |  |  |
| `--payment` | Payment record — Keep the current payment date, replace it, or clear the recorded date. Clearing the date does not refund money. | keep, replace, clear |  |
| `--payment-date` | Payment date — Date when the full decided amount was paid, as recorded by the handler. Leave unrecorded if unpaid or unknown. Recording payment does not make or verify a bank transfer. Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |

## case decide

Record payment decision

Record or replace the decided amount and currency. This does not send money.

```sh
claimcore case decide [--operation-id VALUE] CASE-REFERENCE --expected-revision N --payment-decision-date VALUE --payable-amount VALUE --payable-currency VALUE
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Expected revision — The case revision on which this request is based. |  |  |
| `--payment-decision-date` | Payment decision date — Date when this claims handler decided the amount to be paid. Not recorded until a decision is made. Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--payable-amount` | Amount to be paid — Amount the handler decided should be paid. Not recorded until decided; may differ from the claimed amount. Use ASCII digits and a dot, for example 0.10; up to 4 decimal places. No redundant leading zeros (00.10), signs or commas. |  |  |
| `--payable-currency` | Currency of amount to be paid — Currency of the amount to be paid. Not recorded until decided; may differ from the claimed currency. 3 uppercase ASCII letters (A–Z). |  |  |

## case withdraw-decision

Withdraw payment decision

Clear the unpaid decision while preserving its history.

```sh
claimcore case withdraw-decision [--operation-id VALUE] CASE-REFERENCE --expected-revision N
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Expected revision — The case revision on which this request is based. |  |  |

## case record-payment

Record actual payment

Record the date the full decided amount was actually paid. This does not execute or verify a transfer.

```sh
claimcore case record-payment [--operation-id VALUE] CASE-REFERENCE --expected-revision N --payment-date VALUE
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Expected revision — The case revision on which this request is based. |  |  |
| `--payment-date` | Payment date — Date when the full decided amount was paid, as recorded by the handler. Leave unrecorded if unpaid or unknown. Recording payment does not make or verify a bank transfer. Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |

## case clear-payment

Correct an erroneous payment record

Remove an erroneously recorded payment date. This is not a refund or reversal of an actual transfer. Earlier history remains.

```sh
claimcore case clear-payment [--operation-id VALUE] CASE-REFERENCE --expected-revision N
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Expected revision — The case revision on which this request is based. |  |  |

## case close

Close the case

Close the case. Closing does not mean that payment was made.

```sh
claimcore case close [--operation-id VALUE] CASE-REFERENCE --expected-revision N
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Expected revision — The case revision on which this request is based. |  |  |

## case reopen

Reopen the case

Reopen the case, preserving its details and history.

```sh
claimcore case reopen [--operation-id VALUE] CASE-REFERENCE --expected-revision N
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Expected revision — The case revision on which this request is based. |  |  |

## case void

Void case record

Block business acts while retaining the case and its history.

```sh
claimcore case void [--operation-id VALUE] CASE-REFERENCE --expected-revision N --reason VALUE
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Expected revision — The case revision on which this request is based. |  |  |
| `--reason` | Stewardship reason — Why the case record should be voided or reinstated. Up to 200 characters. |  |  |

## case reinstate

Reinstate case record

Restore ordinary case work on a voided record.

```sh
claimcore case reinstate [--operation-id VALUE] CASE-REFERENCE --expected-revision N --reason VALUE
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Expected revision — The case revision on which this request is based. |  |  |
| `--reason` | Stewardship reason — Why the case record should be voided or reinstated. Up to 200 characters. |  |  |

## case erase

Erase case payload

Permanently remove business payload from the live database. WAL, backups and copies elsewhere are not erased.

```sh
claimcore case erase [--operation-id VALUE] CASE-REFERENCE --expected-revision N
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Expected revision — The case revision on which this request is based. |  |  |

## authority register

Register actor

Register a principal without granting case or administration capabilities.

```sh
claimcore authority register [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |

## authority grant

Grant role

Grant a role at installation or case scope.

```sh
claimcore authority grant [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE --role owner|case-reader|case-registrar|case-handler|payment-recorder|data-steward|auditor [--case-reference VALUE]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |
| `--role` | Role — The capability role to grant or revoke. | owner, case-reader, case-registrar, case-handler, payment-recorder, data-steward, auditor |  |
| `--case-reference` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |

## authority revoke

Revoke role

Revoke a role at installation or case scope. The final enabled human owner remains protected.

```sh
claimcore authority revoke [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE --role owner|case-reader|case-registrar|case-handler|payment-recorder|data-steward|auditor [--case-reference VALUE]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |
| `--role` | Role — The capability role to grant or revoke. | owner, case-reader, case-registrar, case-handler, payment-recorder, data-steward, auditor |  |
| `--case-reference` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |

## authority enable

Enable actor

Enable a registered actor.

```sh
claimcore authority enable [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |

## authority disable

Disable actor

Disable a registered actor. The final enabled human owner remains protected.

```sh
claimcore authority disable [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |

## case get

Read a case

Read the current accepted case view and available acts.

```sh
claimcore case get CASE-REFERENCE
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |

## case list

List cases

Read one page of cases visible to the actor.

```sh
claimcore case list [--cursor VALUE] [--limit N] [--lifecycle active|voided|erased]... [--status opened|closed]... [--progress undecided|decided|paid]... [--text VALUE] [--order reference|recent]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--cursor` | Cursor — Continue the same query from the opaque cursor returned by the service. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |
| `--lifecycle` | Lifecycle — Restrict results to the selected lifecycles. Omit this filter to include every lifecycle. | active, voided, erased |  |
| `--status` | Status filter — Restrict results to the selected open or closed statuses. Erased cases have no status. | opened, closed |  |
| `--progress` | Payment progress — Restrict results to undecided, decided or paid cases. Erased cases have no payment progress. | undecided, decided, paid |  |
| `--text` | Search text — Text searched within the claimant name, responsible insurer or handler's case reference. Search uses Unicode normalization and invariant lowercase. Up to 100 characters. |  |  |
| `--order` | Order — Order by case reference, or by latest accepted change with case reference as the tie-breaker. | reference, recent | REFERENCE |

## case history

Read case history

Read accepted history for one case, within current disclosure permissions.

```sh
claimcore case history CASE-REFERENCE [--full] [--cursor VALUE] [--limit N]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `CASE-REFERENCE` | Handler's case reference — Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained. Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--full` | Full history — Include accepted snapshots when full history is visible to the actor. |  |  |
| `--cursor` | Cursor — Continue the same query from the opaque cursor returned by the service. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## operation observe

Observe an operation

Read an accepted operation receipt when it is visible to the actor.

```sh
claimcore operation observe OPERATION-ID
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `OPERATION-ID` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |

## operation resolve

Resolve an operation

Read the author’s receipt or durably prevent the operation from being accepted.

```sh
claimcore operation resolve OPERATION-ID
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `OPERATION-ID` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |

## authority list

List actors

Read registered actors and available authority acts.

```sh
claimcore authority list [--cursor VALUE] [--limit N]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--cursor` | Cursor — Continue the same query from the opaque cursor returned by the service. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## authority grants

Read actor grants

Read one page of grants for the selected actor.

```sh
claimcore authority grants ACTOR-ID [--cursor VALUE] [--limit N]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `ACTOR-ID` | Actor ID — The registered actor whose grants are being read. |  |  |
| `--cursor` | Cursor — Continue the same query from the opaque cursor returned by the service. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## context

Read context

Read the authenticated actor’s capabilities and available resources.

```sh
claimcore context
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |

