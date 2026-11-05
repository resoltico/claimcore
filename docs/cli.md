# CLI reference

Generated from the semantic Protocol catalog and CLI command grammar. Regenerate with `dotnet run --project eng/ClaimCore.ProtocolGenerator -- --output contracts`; generator `--check` also checks this file.

A synopsis shows required inputs and brackets optional inputs. Choices use lowercase kebab spelling; exact uppercase wire tokens are also accepted. Mutation operations generate an operation ID when omitted. Keep that ID and the exact prepared frame for retries.

Use `--dry-run` to print the prepared JSON frame without authentication. This checks protocol shape and byte limits; domain admission, authorization and current revision are checked by the service. Use `--preview` with any case, work, reminder or authority act to request a review without recording it.

Value-taking verb options also accept `--name=value`; use `--text=--literal` when search text begins with `--`. Switches keep their valueless spelling.

Case worklist filters `--lifecycle`, `--status` and `--progress` may repeat to select a set. Omit a filter to include all values. `--text` searches current claimant, responsible insurer, case reference, incident description and handling summary; `--assignment mine|unassigned` selects responsibility within current access; `--due` selects actionable due state using the returned business-date basis; `--order reference|recent|notified|follow-up` selects the page order. Cursors bind all filters and order.
`case list --format csv`, `reminder list --format csv`, `case timeline --format csv` and `authority history --format csv` emit UTF-8 RFC 4180 CSV without a BOM, with wire-path headers and CRLF record endings. Null values are empty cells. `case list --all`, `case timeline --all` and `reminder list --all` follow continuation cursors until the end; each page is its own authorized snapshot. Concurrent ordering/filter changes can move, duplicate or omit rows; `--all` does not pin a snapshot export. Case-list text and CSV use one header; JSON emits one frame per page. A refusal or failure stops traversal and reports the cursor reached.
Timeline CSV emits one row per disclosed changed field, or a metadata-only row when no changes are disclosed. `changes.kind` distinguishes `DISCLOSED`, `WITHHELD` and `ERASED`. `field` uses core semantic names; reminder fields include the reminder ID. `before` and `after` contain canonical scalars: money is `amount CURRENCY`, and actor changes use ActorIds. Recorded actor names and name kinds are separate columns.
CSV is a spreadsheet-safe export: cells beginning with `=`, `+`, `-`, `@`, TAB or CR, including formula triggers after leading spaces/TABs, are prefixed with an apostrophe before quoting. This alters affected cells; use `--format json` for exact values.
Interactive text commands check authored syntax before any read. For existing-case changes, omitting `--expected-revision`, `--expected-work-revision` or `--expected-reminder-revision` reads the current entity and previews with its own revision. New-case OPEN previews without an existing-case lookup. Ordinary authority verbs preview after reading the authenticated context. A supplied operation ID is retained; otherwise one ID is allocated after valid syntax. Confirmation executes the exact reviewed request; declining prints Not recorded and exits 0. The service rechecks current permissions and state. Omitted correction groups mean KEEP; the review shows changed values and one Unchanged summary.

For existing-case changes, `--dry-run`, redirected input/output and `--format json` require an explicit revision. `--dry-run` stays offline and `--preview` remains preview-only; irreversible writes require exact target confirmation. Type the case reference for case ERASE, or the operation ID for ERASE_PROFILE and SETTLE. Redirected commands require `--confirm TARGET`; a mismatching target is a usage error. This local option is never sent in the wire request. Recovery settlement follows the same operation-ID rule.

Human data text visibly escapes terminal controls, line/paragraph separators and directional formatting as `\u{XXXX}` (uppercase hexadecimal). Literal backslashes are doubled, so displayed escapes are distinct from exact stored values. Arabic text and joining characters remain readable. This applies to redirected text too; JSON/NDJSON retain exact decoded values and CSV keeps its export rules.
Narrative LF paragraph breaks are trusted layout around independently escaped data segments. Optional incident description and handling summary each admit 600 Unicode scalar values, preserving authored whitespace and spelling. Reminder action text is a separate 200-scalar single-line value. Empty or whitespace-only supplied text is invalid; omission keeps an existing value and `--clear-incident-description` or `--clear-handling-summary` explicitly clears it.
Use the field's `-file PATH`, `-stdin` or `-editor` option for longer text, for example `--handling-summary-file summary.txt` or `--action-file action.txt`. Sources are exclusive. File/stdin/editor input converts CRLF to LF before preparation; direct protocol CR is refused. User-supplied files are preserved. Editor entry requires a terminal and `VISUAL` or `EDITOR` naming one executable, without shell expressions or arguments. Owned temporary files/directories use 0600/0700 permissions; cleanup failure prevents request preparation and reports a diagnostic.
Work starts empty at revision zero after OPEN. Work edits preserve financial fact revisions; reminder edits preserve both fact and work revisions. `reminder get` reads one current record, including completed/cancelled records, using parent and reminder identities. `reminder list` defaults to pending actions on active open accessible cases. Due filtering there selects individual reminder dates; case `--due` uses the earliest pending reminder. Past-due reminders remain pending. No-plan attention is a warning, not a refusal or automatic reminder.
`reminder complete-and-plan-next` is one atomic two-effect operation: complete one pending reminder and create exactly one next pending reminder whose ID equals the operation ID. Human output refreshes the core work view after acceptance, including missing-plan attention; JSON/NDJSON contain only the requested structured reply. Initial OPEN and subsequent reminder planning are explicitly separate operations. Work overview counts cover accessible actionable cases independently of page/search and report their server date, zone and observation instant; suspended clients do not deliver alarms.
Human operations default to text. Add `--format json` for structured output or `--language en|lv|ar` to select interface text. Display language does not rewrite authored values. Human money, dates and observation footers use en-GB for English, lv-LV for Latvian and Latin-digit ar-AE for Arabic. Canonical JSON and CSV values are unchanged.

Top-level `claimcore` lists command families. `claimcore describe case open` describes an operation offline; `claimcore schema operation.execute request` shows its wire schema. `claimcore call` reads one JSON frame from stdin; `claimcore session` reads NDJSON.

`claimcore configure` atomically saves only public connection settings in `$XDG_CONFIG_HOME/claimcore/profile.json` (default `~/.config/claimcore/profile.json` on Unix). Files are 0600 and the profile directory is 0700. Every read uses private-file admission and refuses unknown members. Environment values override profile values individually; `CLAIMCORE_OIDC_CLIENT_SECRET_FILE` is environment-only. `claimcore configure --show` displays effective values and their sources without reading secret content.

`claimcore shell` requires interactive authentication mode and terminal stdin. It shares one process-local access/refresh-token session across commands, refreshes on expiry and signs in again on refresh failure. Fifteen idle minutes, exit, EOF and SIGTERM discard tokens. Arrow-key history stays in memory. Quotes and backslashes group arguments; metacharacters do not execute or expand. Each command prints its exit code and the shell continues after exit 4 with the frozen retry frame and recovery guidance. Ctrl-C cancels a command; a second Ctrl-C at an empty prompt exits. Interactive review still requires terminal stdout.

## Discovery, configuration and frame commands

Help, version, describe and schema commands work offline. Call and session dispatch frames through the configured HTTPS service and authentication mode.

- `claimcore help [topic | family [verb...]] [--language en|lv|ar]`
- `claimcore version [--json]`
- `claimcore --version [--json]`
- `claimcore describe [family verb] [--format text|json] [--language en|lv|ar]`
- `claimcore schema name`
- `claimcore schema endpoint request|response`
- `claimcore call`
- `claimcore session`
- `claimcore shell`
- `claimcore recovery list [--format text|json] [--language en|lv|ar]`
- `claimcore recovery check ATTEMPT_ID [--format text|json] [--language en|lv|ar]`
- `claimcore recovery settle ATTEMPT_ID [--format text|json] [--language en|lv|ar]`
- `claimcore configure --show`
- `claimcore configure --language en|lv|ar`
- `claimcore configure --service-url URL --issuer URL --client-id ID --auth-mode interactive|automation [--service-trust-root-file PATH] [--oidc-trust-root-file PATH] [--language en|lv|ar]`

Help topics: `overview`, `help`, `version`, `describe`, `schema`, `call`, `session`, `shell`, `recovery`, `configure`, `configuration`, `delivery`, `examples`, `verbs`, `CLI_PROTOCOL_GUIDANCE`, `CLI_CONFIGURATION_ISSUE`.

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
claimcore case open [--operation-id VALUE] CASE-REFERENCE --incident-date VALUE --incident-notification-date VALUE --incident-country VALUE --claimant-name VALUE --insurer-name VALUE --claimed-amount VALUE --claimed-currency VALUE [--incident-description VALUE] [--incident-description-file PATH | --incident-description-stdin | --incident-description-editor] [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--incident-date` | Incident date — Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--incident-notification-date` | Notified date — Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--incident-country` | Country — Use two uppercase letters. ZZ means unknown or international. |  |  |
| `--claimant-name` | Claimant name — Up to 200 characters. |  |  |
| `--insurer-name` | Responsible insurer — Up to 200 characters. |  |  |
| `--claimed-amount` | Amount claimed — Use up to 18 digits before the decimal point and up to 4 after it. Use ASCII digits and a dot; no spaces, signs, commas or redundant leading zeros. |  |  |
| `--claimed-currency` | Currency of claimed amount — 3 uppercase ASCII letters (A–Z). |  |  |
| `--incident-description` | Incident description — Optional plain text, up to 600 Unicode scalar values. LF paragraph breaks are permitted. Whitespace and Unicode spelling are preserved; unsafe controls and directional formatting are refused. |  |  |

## case amend-registration

Update claim details

Update selected registration facts. Omitted facts and the case reference stay unchanged.

```sh
claimcore case amend-registration [--operation-id VALUE] CASE-REFERENCE --expected-revision N [--incident-date VALUE] [--incident-notification-date VALUE] [--incident-country VALUE] [--claimant-name VALUE] [--insurer-name VALUE] [--claimed-amount VALUE] [--claimed-currency VALUE] [--incident-description VALUE] [--incident-description-file PATH | --incident-description-stdin | --incident-description-editor] [--clear-incident-description] [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case revision on which this request is based. |  |  |
| `--incident-date` | Incident date — Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--incident-notification-date` | Notified date — Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--incident-country` | Country — Use two uppercase letters. ZZ means unknown or international. |  |  |
| `--claimant-name` | Claimant name — Up to 200 characters. |  |  |
| `--insurer-name` | Responsible insurer — Up to 200 characters. |  |  |
| `--claimed-amount` | Amount claimed — Use up to 18 digits before the decimal point and up to 4 after it. Use ASCII digits and a dot; no spaces, signs, commas or redundant leading zeros. |  |  |
| `--claimed-currency` | Currency of claimed amount — 3 uppercase ASCII letters (A–Z). |  |  |
| `--incident-description` | Incident description — Optional plain text, up to 600 Unicode scalar values. LF paragraph breaks are permitted. Whitespace and Unicode spelling are preserved; unsafe controls and directional formatting are refused. |  |  |

## case correct

Correct case facts

Correct selected existing facts together. A paid decision change requires explicit payment reaffirmation, replacement or clearing. Clearing a payment date does not refund money.

```sh
claimcore case correct [--operation-id VALUE] CASE-REFERENCE --expected-revision N --registration keep|update [--incident-date VALUE] [--incident-notification-date VALUE] [--incident-country VALUE] [--claimant-name VALUE] [--insurer-name VALUE] [--claimed-amount VALUE] [--claimed-currency VALUE] [--incident-description VALUE] --decision keep|update|clear [--payment-decision-date VALUE] [--payable-amount VALUE] [--payable-currency VALUE] --payment keep|replace|reaffirm|clear [--payment-date VALUE] [--incident-description-file PATH | --incident-description-stdin | --incident-description-editor] [--clear-incident-description] [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case revision on which this request is based. |  |  |
| `--registration` | Claim — Keep registration facts or update selected facts. Omitted facts stay unchanged; amount and currency are asserted together. | keep, update |  |
| `--incident-date` | Incident date — Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--incident-notification-date` | Notified date — Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--incident-country` | Country — Use two uppercase letters. ZZ means unknown or international. |  |  |
| `--claimant-name` | Claimant name — Up to 200 characters. |  |  |
| `--insurer-name` | Responsible insurer — Up to 200 characters. |  |  |
| `--claimed-amount` | Amount claimed — Use up to 18 digits before the decimal point and up to 4 after it. Use ASCII digits and a dot; no spaces, signs, commas or redundant leading zeros. |  |  |
| `--claimed-currency` | Currency of claimed amount — 3 uppercase ASCII letters (A–Z). |  |  |
| `--incident-description` | Incident description — Optional plain text, up to 600 Unicode scalar values. LF paragraph breaks are permitted. Whitespace and Unicode spelling are preserved; unsafe controls and directional formatting are refused. |  |  |
| `--decision` | Payment decision — Keep the decision, update selected facts, or clear it. Amount and currency must be asserted together. | keep, update, clear |  |
| `--payment-decision-date` | Payment decision date — Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--payable-amount` | Amount to be paid — Use up to 18 digits before the decimal point and up to 4 after it. Use ASCII digits and a dot; no spaces, signs, commas or redundant leading zeros. |  |  |
| `--payable-currency` | Currency of amount to be paid — 3 uppercase ASCII letters (A–Z). |  |  |
| `--payment` | Payment — Keep, replace, reaffirm or clear the recorded payment date. Reaffirmation retains the date; clearing it does not refund money. | keep, replace, reaffirm, clear |  |
| `--payment-date` | Payment date — Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |

## case decide

Record payment decision

Record or replace the decided amount and currency. This does not send money.

```sh
claimcore case decide [--operation-id VALUE] CASE-REFERENCE --expected-revision N --payment-decision-date VALUE --payable-amount VALUE --payable-currency VALUE [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case revision on which this request is based. |  |  |
| `--payment-decision-date` | Payment decision date — Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |
| `--payable-amount` | Amount to be paid — Use up to 18 digits before the decimal point and up to 4 after it. Use ASCII digits and a dot; no spaces, signs, commas or redundant leading zeros. |  |  |
| `--payable-currency` | Currency of amount to be paid — 3 uppercase ASCII letters (A–Z). |  |  |

## case withdraw-decision

Withdraw payment decision

Clear the unpaid decision while preserving its history.

```sh
claimcore case withdraw-decision [--operation-id VALUE] CASE-REFERENCE --expected-revision N [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case revision on which this request is based. |  |  |

## case record-payment

Record actual payment

Record the date the full decided amount was actually paid. This does not execute or verify a transfer.

```sh
claimcore case record-payment [--operation-id VALUE] CASE-REFERENCE --expected-revision N --payment-date VALUE [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case revision on which this request is based. |  |  |
| `--payment-date` | Payment date — Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06. |  |  |

## case clear-payment

Correct an erroneous payment record

Remove an erroneously recorded payment date. This is not a refund or reversal of an actual transfer. Earlier history remains.

```sh
claimcore case clear-payment [--operation-id VALUE] CASE-REFERENCE --expected-revision N [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case revision on which this request is based. |  |  |

## case close

Close the case

Close the case. Closing does not mean that payment was made.

```sh
claimcore case close [--operation-id VALUE] CASE-REFERENCE --expected-revision N [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case revision on which this request is based. |  |  |

## case reopen

Reopen the case

Reopen the case, preserving its details and history.

```sh
claimcore case reopen [--operation-id VALUE] CASE-REFERENCE --expected-revision N [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case revision on which this request is based. |  |  |

## case void

Void case record

Block business acts while retaining the case and its history.

```sh
claimcore case void [--operation-id VALUE] CASE-REFERENCE --expected-revision N --reason VALUE [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case revision on which this request is based. |  |  |
| `--reason` | Reason — Up to 200 characters. |  |  |

## case reinstate

Reinstate case record

Restore ordinary case work on a voided record.

```sh
claimcore case reinstate [--operation-id VALUE] CASE-REFERENCE --expected-revision N --reason VALUE [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case revision on which this request is based. |  |  |
| `--reason` | Reason — Up to 200 characters. |  |  |

## case erase

Erase case

Remove this case and its details from ClaimCore. Copies held elsewhere remain.

```sh
claimcore case erase [--operation-id VALUE] CASE-REFERENCE --expected-revision N [--format text|json] [--language en|lv|ar] [--dry-run] [--confirm TARGET] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case revision on which this request is based. |  |  |

## authority register

Register account

Add a registered account with a name and optional contact details. Roles can be assigned separately.

```sh
claimcore authority register [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE --name VALUE [--contact VALUE] [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |
| `--name` | Name — Up to 200 characters. |  |  |
| `--contact` | Contact details — Up to 200 characters. |  |  |

## authority grant

Grant role

Assign the selected role to this account.

```sh
claimcore authority grant [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE --role owner|case-reader|case-registrar|case-handler|payment-recorder|data-steward|auditor [--case-reference VALUE] [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |
| `--role` | Role — The capability role to grant or revoke. | owner, case-reader, case-registrar, case-handler, payment-recorder, data-steward, auditor |  |
| `--case-reference` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |

## authority revoke

Revoke role

Remove the selected role assignment.

```sh
claimcore authority revoke [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE --role owner|case-reader|case-registrar|case-handler|payment-recorder|data-steward|auditor [--case-reference VALUE] [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |
| `--role` | Role — The capability role to grant or revoke. | owner, case-reader, case-registrar, case-handler, payment-recorder, data-steward, auditor |  |
| `--case-reference` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |

## authority enable

Enable account

Enable this account with its existing roles.

```sh
claimcore authority enable [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |

## authority disable

Disable account

Disable this account. Its recorded changes remain.

```sh
claimcore authority disable [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |

## authority set-profile

Update profile

Record a display name and optional contact details for an account. This does not change its identity or access permissions.

```sh
claimcore authority set-profile [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE --name VALUE [--contact VALUE] [--clear-contact] [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |
| `--name` | Name — Up to 200 characters. |  |  |
| `--contact` | Contact details — Up to 200 characters. |  |  |

## authority erase-profile

Erase profile

Permanently remove the saved display name and contact details for this account. Its identity, access permissions and recorded work remain.

```sh
claimcore authority erase-profile [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE [--format text|json] [--language en|lv|ar] [--dry-run] [--confirm TARGET] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |

## authority admit

Add person

Add an account with its name and initial roles together.

```sh
claimcore authority admit [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE --name VALUE [--contact VALUE] --role ROLE[@CASE-REF]... [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |
| `--name` | Name — Up to 200 characters. |  |  |
| `--contact` | Contact details — Up to 200 characters. |  |  |
| `--role ROLE[@CASE-REF]...` | Access grants — Provide 1 to 48 distinct role-and-scope pairs. Case references are explicit references, including UUID-looking references. | 1–48 | |

## work update

Update case work

Change who is responsible or update the handling summary.

```sh
claimcore work update [--operation-id VALUE] CASE-REFERENCE --expected-work-revision N [--responsible-actor-action clear|set] [--responsible-actor VALUE] [--handling-summary VALUE] [--handling-summary-file PATH | --handling-summary-stdin | --handling-summary-editor] [--clear-handling-summary] [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-work-revision` | Reviewed work version — The work revision you reviewed. Financial fact revisions are independent. |  |  |
| `--responsible-actor-action` | Responsibility change — Choose set or clear; omit to keep the current responsible account. | clear, set |  |
| `--responsible-actor` | Responsible — Use the exact registered account UUID. |  |  |
| `--handling-summary` | Handling summary — Optional plain text, up to 600 Unicode scalar values. LF paragraph breaks are permitted. Whitespace and Unicode spelling are preserved; unsafe controls and directional formatting are refused. |  |  |

## reminder create

Add reminder

Plan the next action for this case.

```sh
claimcore reminder create [--operation-id VALUE] CASE-REFERENCE --action VALUE --due VALUE [--action-file PATH | --action-stdin | --action-editor] [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--action` | Action — Use one line of plain text, up to 200 Unicode scalar values. |  |  |
| `--due` | Due date — Use a Gregorian date, YYYY-MM-DD. Past and future dates are allowed. |  |  |

## reminder update

Update reminder

Change the action or date of this pending reminder. Earlier versions remain in history.

```sh
claimcore reminder update [--operation-id VALUE] CASE-REFERENCE --reminder-id VALUE --expected-reminder-revision N --action VALUE --due VALUE [--action-file PATH | --action-stdin | --action-editor] [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--reminder-id` | Reminder ID — The immutable identity of one reminder in this case. |  |  |
| `--expected-reminder-revision` | Reviewed reminder version — The revision of this exact reminder that you reviewed. Other reminders have independent revisions. |  |  |
| `--action` | Action — Use one line of plain text, up to 200 Unicode scalar values. |  |  |
| `--due` | Due date — Use a Gregorian date, YYYY-MM-DD. Past and future dates are allowed. |  |  |

## reminder complete

Complete reminder

Record that you marked the pending action complete. Completing the last reminder is allowed and may leave the case needing a next action.

```sh
claimcore reminder complete [--operation-id VALUE] CASE-REFERENCE --reminder-id VALUE --expected-reminder-revision N [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--reminder-id` | Reminder ID — The immutable identity of one reminder in this case. |  |  |
| `--expected-reminder-revision` | Reviewed reminder version — The revision of this exact reminder that you reviewed. Other reminders have independent revisions. |  |  |

## reminder cancel

Cancel reminder

Cancel this pending action without deleting its history. Cancellation is distinct from completion.

```sh
claimcore reminder cancel [--operation-id VALUE] CASE-REFERENCE --reminder-id VALUE --expected-reminder-revision N [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--reminder-id` | Reminder ID — The immutable identity of one reminder in this case. |  |  |
| `--expected-reminder-revision` | Reviewed reminder version — The revision of this exact reminder that you reviewed. Other reminders have independent revisions. |  |  |

## reminder reopen

Reopen reminder

Reopen this reminder with a new due date.

```sh
claimcore reminder reopen [--operation-id VALUE] CASE-REFERENCE --reminder-id VALUE --expected-reminder-revision N --due VALUE [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--reminder-id` | Reminder ID — The immutable identity of one reminder in this case. |  |  |
| `--expected-reminder-revision` | Reviewed reminder version — The revision of this exact reminder that you reviewed. Other reminders have independent revisions. |  |  |
| `--due` | Due date — Use a Gregorian date, YYYY-MM-DD. Past and future dates are allowed. |  |  |

## reminder complete-and-plan-next

Complete and plan next action

Complete this reminder and plan the next action together.

```sh
claimcore reminder complete-and-plan-next [--operation-id VALUE] CASE-REFERENCE --reminder-id VALUE --expected-reminder-revision N --action VALUE --due VALUE [--action-file PATH | --action-stdin | --action-editor] [--format text|json] [--language en|lv|ar] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--reminder-id` | Reminder ID — The immutable identity of one reminder in this case. |  |  |
| `--expected-reminder-revision` | Reviewed reminder version — The revision of this exact reminder that you reviewed. Other reminders have independent revisions. |  |  |
| `--action` | Action — Use one line of plain text, up to 200 Unicode scalar values. |  |  |
| `--due` | Due date — Use a Gregorian date, YYYY-MM-DD. Past and future dates are allowed. |  |  |

## case timeline

Case timeline

Read case facts, work and reminder changes with the access you have now.

```sh
claimcore case timeline CASE-REFERENCE [--filter facts|work] [--cursor VALUE] [--limit N] [--format text|json|csv] [--language en|lv|ar] [--dry-run] [--all]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--filter` | Timeline entries — Show case facts or work and reminders. Leave unset to show both. | facts, work |  |
| `--cursor` | Cursor — Continue the same query from the opaque cursor returned by the service. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## reminder get

Show reminder

Read this current reminder by its parent and immutable ID, including terminal records. Earlier payload versions require full history access.

```sh
claimcore reminder get CASE-REFERENCE --reminder-id VALUE [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--reminder-id` | Reminder ID — The immutable identity of one reminder in this case. |  |  |

## reminder list

Reminder queue

Bounded accessible reminder pages. By default, pending actions on active open cases are ordered by due date and stable identity.

```sh
claimcore reminder list [--case-reference VALUE] [--state pending|completed|cancelled] [--view pending|mine|overdue|today|upcoming|completed|cancelled] [--due overdue|today|upcoming] [--assignment mine|unassigned] [--text VALUE] [--cursor VALUE] [--limit N] [--include-inactive true|false] [--format text|json|csv] [--language en|lv|ar] [--dry-run] [--all]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--case-reference` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--state` | Reminder state — Select current pending, completed or cancelled records. Omission selects pending reminders. | pending, completed, cancelled |  |
| `--view` | Reminder view — Choose a standard reminder view. Explicit state, assignment, date, text and parent filters further restrict its results. | pending, mine, overdue, today, upcoming, completed, cancelled |  |
| `--due` | Reminder due — Filter individual reminder dates against the installation business date; this differs from the case minimum next due. | overdue, today, upcoming |  |
| `--assignment` | Responsibility — Filter factual responsibility after current visibility. A case-scoped read grant alone is not an assignment. | mine, unassigned |  |
| `--text` | Search text — Up to 100 characters. |  |  |
| `--cursor` | Cursor — Continue the same query from the opaque cursor returned by the service. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |
| `--include-inactive` | Include inactive case work — Explicitly include work on closed or voided cases. Pending reminders retain their dates and state. |  |  |

## case views

Case views

Count accessible cases in every case view using one authorized snapshot and business date.

```sh
claimcore case views [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |

## reminder views

Reminder views

Count accessible reminders in every reminder view using one authorized snapshot and business date.

```sh
claimcore reminder views [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |

## work overview

Work overview

Read-only overview of accessible actionable cases, independent of current page and search, with the installation date and observation basis.

```sh
claimcore work overview [--mine true|false] [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--mine` | Mine — Cases currently assigned to you within your current read access. Assignment never grants access. |  |  |

## case assignees

Eligible responsible accounts

List enabled human accounts currently eligible for assignment to this case. Selection uses the exact account ID.

```sh
claimcore case assignees CASE-REFERENCE [--cursor VALUE] [--limit N] [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--cursor` | Cursor — Continue the same query from the opaque cursor returned by the service. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## case get

Read a case

Read the current accepted case view and available acts.

```sh
claimcore case get CASE-REFERENCE [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |

## case list

List cases

Read one page of cases visible to the actor.

```sh
claimcore case list [--cursor VALUE] [--limit N] [--lifecycle active|voided|erased]... [--status opened|closed]... [--progress undecided|decided|paid]... [--text VALUE] [--order reference|recent|notified|follow-up] [--assignment mine|unassigned] [--due overdue|today|upcoming|unscheduled|inactive] [--view needs-attention|mine|overdue|due-today|no-next-action|unassigned|undecided|decided-unpaid|closed|voided|all] [--responsible-actor VALUE] [--format text|json|csv] [--language en|lv|ar] [--dry-run] [--all]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--cursor` | Cursor — Continue the same query from the opaque cursor returned by the service. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |
| `--lifecycle` | Lifecycle — Restrict results to the selected lifecycles. Omit this filter to include every lifecycle. | active, voided, erased |  |
| `--status` | Status filter — Restrict results to the selected open or closed statuses. Erased cases have no status. | opened, closed |  |
| `--progress` | Payment progress — Restrict results to undecided, decided or paid cases. Erased cases have no payment progress. | undecided, decided, paid |  |
| `--text` | Search text — Up to 100 characters. |  |  |
| `--order` | Order — Order by reference, latest case-fact change, notification date or earliest pending reminder date. Dates sort oldest first; reference breaks ties. | reference, recent, notified, follow-up | REFERENCE |
| `--assignment` | Responsibility — Filter factual responsibility after current visibility. A case-scoped read grant alone is not an assignment. | mine, unassigned |  |
| `--due` | Follow-up state — Classify each case by its earliest pending reminder: overdue, today, upcoming, no pending reminder or inactive. Individual reminder queue dates use a separate filter. | overdue, today, upcoming, unscheduled, inactive |  |
| `--view` | Case view — Choose a standard case view. Other filters further restrict its results. | needs-attention, mine, overdue, due-today, no-next-action, unassigned, undecided, decided-unpaid, closed, voided, all |  |
| `--responsible-actor` | Responsible account filter — Restrict cases to the selected responsible account within your readable scope. Other filters further restrict its results. |  |  |

## case summary

Case summary

Count matching visible cases and sum claimed and payable amounts separately for each currency.

```sh
claimcore case summary [--lifecycle active|voided|erased]... [--status opened|closed]... [--progress undecided|decided|paid]... [--text VALUE] [--order reference|recent|notified|follow-up] [--assignment mine|unassigned] [--due overdue|today|upcoming|unscheduled|inactive] [--view needs-attention|mine|overdue|due-today|no-next-action|unassigned|undecided|decided-unpaid|closed|voided|all] [--responsible-actor VALUE] [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--lifecycle` | Lifecycle — Restrict results to the selected lifecycles. Omit this filter to include every lifecycle. | active, voided, erased |  |
| `--status` | Status filter — Restrict results to the selected open or closed statuses. Erased cases have no status. | opened, closed |  |
| `--progress` | Payment progress — Restrict results to undecided, decided or paid cases. Erased cases have no payment progress. | undecided, decided, paid |  |
| `--text` | Search text — Up to 100 characters. |  |  |
| `--order` | Order — Order by reference, latest case-fact change, notification date or earliest pending reminder date. Dates sort oldest first; reference breaks ties. | reference, recent, notified, follow-up | REFERENCE |
| `--assignment` | Responsibility — Filter factual responsibility after current visibility. A case-scoped read grant alone is not an assignment. | mine, unassigned |  |
| `--due` | Follow-up state — Classify each case by its earliest pending reminder: overdue, today, upcoming, no pending reminder or inactive. Individual reminder queue dates use a separate filter. | overdue, today, upcoming, unscheduled, inactive |  |
| `--view` | Case view — Choose a standard case view. Other filters further restrict its results. | needs-attention, mine, overdue, due-today, no-next-action, unassigned, undecided, decided-unpaid, closed, voided, all |  |
| `--responsible-actor` | Responsible account filter — Restrict cases to the selected responsible account within your readable scope. Other filters further restrict its results. |  |  |

## operation observe

Observe an operation

Read an accepted operation receipt when it is visible to the actor.

```sh
claimcore operation observe OPERATION-ID [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `OPERATION-ID` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |

## operation status

Check operation status

Read this actor’s known outcome without blocking the operation or changing the register.

```sh
claimcore operation status OPERATION-ID [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `OPERATION-ID` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |

## operation settle

Settle operation permanently

Return this account’s accepted receipt, or permanently block its operation ID if it has not been accepted for this account. This cannot be undone. Checking status is a separate read.

```sh
claimcore operation settle OPERATION-ID [--format text|json] [--language en|lv|ar] [--dry-run] [--confirm TARGET]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `OPERATION-ID` | Operation ID — An ID for this operation. Reuse it only when retrying the same request. |  |  |

## authority list

List actors

Read registered actors and available authority acts.

```sh
claimcore authority list [--text VALUE] [--role owner|case-reader|case-registrar|case-handler|payment-recorder|data-steward|auditor] [--cursor VALUE] [--limit N] [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--text` | Search people — Use up to 200 characters. Capitalization does not matter. |  |  |
| `--role` | Role filter — Match recognized role assignments across the installation or individual cases. This filter does not test current capabilities or whether the account is enabled. | owner, case-reader, case-registrar, case-handler, payment-recorder, data-steward, auditor |  |
| `--cursor` | Cursor — Continue the same query from the opaque cursor returned by the service. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## authority grants

Read actor grants

Read one page of grants for the selected actor.

```sh
claimcore authority grants ACTOR-ID [--cursor VALUE] [--limit N] [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `ACTOR-ID` | Actor ID — The registered actor selected for grant review or authority history. |  |  |
| `--cursor` | Cursor — Continue the same query from the opaque cursor returned by the service. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## authority history

Authority history

Review authority changes, newest first, optionally filtered by target actor.

```sh
claimcore authority history [--actor-id VALUE] [--cursor VALUE] [--limit N] [--format text|json|csv] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--actor-id` | Actor ID — The registered actor selected for grant review or authority history. |  |  |
| `--cursor` | Cursor — Continue the same query from the opaque cursor returned by the service. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## authority requests

Access requests

Read one page of requests awaiting admission.

```sh
claimcore authority requests [--cursor VALUE] [--limit N] [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--cursor` | Cursor — Continue the same query from the opaque cursor returned by the service. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## authority requests dismiss

Dismiss access request

Remove this request without granting access.

```sh
claimcore authority requests dismiss --principal-kind human|service --issuer VALUE --subject VALUE [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--principal-kind` | Type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Identity issuer — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Subject or client ID — The immutable subject or client ID of the target principal. |  |  |

## context

Read context

Read the authenticated actor’s capabilities and available resources.

```sh
claimcore context [--format text|json] [--language en|lv|ar] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |

