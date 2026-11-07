# CLI reference

Generated from the semantic Protocol catalog and CLI command grammar. Regenerate with `dotnet run --project eng/ClaimCore.ContractGenerator -- --output contracts`; generator `--check` also checks this file.

A synopsis shows required inputs and brackets optional inputs. Choices use lowercase kebab spelling; exact uppercase wire tokens are also accepted. Mutation operations generate an operation ID when omitted. Keep that ID and the exact prepared frame for retries.

Use `--dry-run` to print the prepared JSON frame without authentication. This checks protocol shape and byte limits; domain admission, authorization and current revision are checked by the service. Use `--preview` with any case, work, reminder or authority act to request a review without recording it.

Value-taking verb options also accept `--name=value`; use `--text=--literal` when search text begins with `--`. Switches keep their valueless spelling.

Case worklist filters `--lifecycle`, `--status` and `--progress` may repeat to select a set. Omit a filter to include all values. `--text` searches current claimant, responsible insurer, case reference, incident description and handling summary; `--assignment mine|unassigned` selects responsibility within current access; `--due` selects actionable due state using the returned business-date basis; `--order reference|recent|notified|follow-up` selects the page order. Cursors bind all filters and order.
`case list --format csv`, `reminder list --format csv`, `case timeline --format csv` and `authority history --format csv` emit UTF-8 RFC 4180 CSV without a BOM, with wire-path headers and CRLF record endings. Null values are empty cells. `case list --all`, `case timeline --all` and `reminder list --all` follow continuation cursors until the end; each page is its own authorized snapshot. Concurrent ordering/filter changes can move, duplicate or omit rows; `--all` does not pin a snapshot export. Case-list text and CSV use one header; JSON emits one frame per page. A refusal or failure stops traversal and reports the cursor reached.
Timeline CSV emits one row per disclosed changed field, or a metadata-only row when no changes are disclosed. `changes.kind` distinguishes `DISCLOSED`, `WITHHELD` and `ERASED`. `field` uses core semantic names; reminder fields include the reminder ID. `before` and `after` contain canonical scalars: money is `amount CURRENCY`, and actor changes use ActorIds. Recorded actor names and name kinds are separate columns.
CSV is a spreadsheet-safe export: cells beginning with `=`, `+`, `-`, `@`, TAB or CR, including formula triggers after leading spaces/TABs, are prefixed with an apostrophe before quoting. This alters affected cells; use `--format json` for exact values.
Interactive text commands check authored syntax before any read. For existing-case changes, omitting `--expected-revision`, `--expected-work-revision` or `--expected-reminder-revision` reads the current entity and previews with its own revision. New-case OPEN previews without an existing-case lookup. Ordinary authority verbs preview after reading the authenticated context. A supplied operation ID is retained; otherwise one ID is allocated after valid syntax. Confirmation executes the exact reviewed request; declining prints Not recorded and exits 0. The service rechecks current permissions and state. Omitted correction groups mean KEEP; the review shows changed values and one Unchanged summary.

For existing-case changes, `--dry-run`, redirected input/output and `--format json` require an explicit revision. `--dry-run` stays offline and `--preview` remains preview-only; irreversible writes require exact target confirmation. Type the case reference for case ERASE, or the operation ID for ERASE_PROFILE. Redirected commands require `--confirm TARGET`; a mismatching target is a usage error. This local option is never sent in the wire request.

Human data text visibly escapes terminal controls, line/paragraph separators and directional formatting as `\u{XXXX}` (uppercase hexadecimal). Literal backslashes are doubled, so displayed escapes are distinct from exact stored values. Arabic text and joining characters remain readable. This applies to redirected text too; JSON/NDJSON retain exact decoded values and CSV keeps its export rules.
Narrative LF paragraph breaks are trusted layout around independently escaped data segments. Optional incident description and handling summary each admit 600 Unicode scalar values, preserving authored whitespace and spelling. Reminder action text is a separate 200-scalar single-line value. Empty or whitespace-only supplied text is invalid; omission keeps an existing value and `--clear-incident-description` or `--clear-handling-summary` explicitly clears it.
Use the field's `-file PATH`, `-stdin` or `-editor` option for longer text, for example `--handling-summary-file summary.txt` or `--action-file action.txt`. Sources are exclusive. File/stdin/editor input converts CRLF to LF before preparation; direct protocol CR is refused. User-supplied files are preserved. Editor entry requires a terminal and `VISUAL` or `EDITOR` naming one executable, without shell expressions or arguments. Owned temporary files/directories use 0600/0700 permissions; cleanup failure prevents request preparation and reports a diagnostic.
Work starts empty at revision zero after OPEN. Work edits preserve financial fact revisions; reminder edits preserve both fact and work revisions. `reminder get` reads one current record, including completed/cancelled records, using parent and reminder identities. `reminder list` defaults to pending actions on active open accessible cases. Due filtering there selects individual reminder dates; case `--due` uses the earliest pending reminder. Past-due reminders remain pending. No-plan attention is a warning, not a refusal or automatic reminder.
`reminder complete-and-plan-next` is one atomic two-effect operation: complete one pending reminder and create exactly one next pending reminder whose ID equals the operation ID. Human output refreshes the core work view after acceptance, including missing-plan attention; JSON/NDJSON contain only the requested structured reply. Initial OPEN and subsequent reminder planning are explicitly separate operations. `work views` returns case and reminder counts from one authorized snapshot, independently of page/search, with their server date, zone and observation instant; suspended clients do not deliver alarms.
Human operations default to text. Add `--format json` for structured output or `--language LANGUAGE` to select interface text. Supported languages: `en`, `lv`, `ar`, `ko`, `zh-Hans`, `zh-Hant`, `ja`, `es`, `de`. Display language does not rewrite authored values. CLI display cultures: `en` → `en-GB`, `lv` → `lv-LV`, `ar` → `ar-AE`, `ko` → `ko-KR`, `zh-Hans` → `zh-CN`, `zh-Hant` → `zh-TW`, `ja` → `ja-JP`, `es` → `es-ES`, `de` → `de-DE`. Dates use the Gregorian calendar; Arabic CLI output retains Latin digits. Canonical JSON and CSV values are unchanged.

Top-level `claimcore` lists command families. `claimcore describe case open` describes an operation offline; `claimcore schema operation.execute request` shows its wire schema. `claimcore call` reads one JSON frame from stdin; `claimcore session` reads NDJSON.

`claimcore configure` atomically saves only public connection settings in `$XDG_CONFIG_HOME/claimcore/profile.json` (default `~/.config/claimcore/profile.json` on Unix). Files are 0600 and the profile directory is 0700. Every read uses private-file admission and refuses unknown members. Environment values override profile values individually; `CLAIMCORE_OIDC_CLIENT_SECRET_FILE` is environment-only. `claimcore configure --show` displays effective values and their sources without reading secret content.

`claimcore shell` requires interactive authentication mode and terminal stdin. It shares one process-local access/refresh-token session across commands, refreshes on expiry and signs in again on refresh failure. Fifteen idle minutes, exit, EOF and SIGTERM discard tokens. Arrow-key history stays in memory. Quotes and backslashes group arguments; metacharacters do not execute or expand. Each command prints its exit code and the shell continues after exit 4 with the frozen retry frame and recovery guidance. Ctrl-C cancels a command; a second Ctrl-C at an empty prompt exits. Interactive review still requires terminal stdout.

## Discovery, configuration and frame commands

Help, version, describe and schema commands work offline. Call and session dispatch frames through the configured HTTPS service and authentication mode.

- `claimcore help [topic | family [verb...]] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de]`
- `claimcore version [--json]`
- `claimcore --version [--json]`
- `claimcore describe [family verb] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de]`
- `claimcore schema name`
- `claimcore schema endpoint request|response`
- `claimcore call`
- `claimcore session`
- `claimcore shell`
- `claimcore recovery list [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de]`
- `claimcore recovery check ATTEMPT_ID [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de]`
- `claimcore configure --show`
- `claimcore configure --language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de`
- `claimcore configure --service-url URL --issuer URL --client-id ID --auth-mode interactive|automation [--service-trust-root-file PATH] [--oidc-trust-root-file PATH] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de]`

Help topics: `overview`, `help`, `version`, `describe`, `schema`, `call`, `session`, `shell`, `recovery`, `configure`, `configuration`, `delivery`, `examples`, `verbs`, `CLI_PROTOCOL_GUIDANCE`, `CLI_CONFIGURATION_ISSUE`.

## Exit codes

Both executables use the shared Protocol exit-code table. `claimcore-database help` reports its owner-command grammar without opening credentials or the database. Database refusals include a diagnostic; failures include a fault. Usage errors include the applicable synopsis.

| Exit | Meaning | claimcore | claimcore-database |
| --- | --- | --- | --- |
| 0 | Requested work completed. | yes | yes |
| 2 | Input, usage or action was refused. | yes | yes |
| 3 | A known failure; check any earlier unconfirmed change separately. | yes | yes |
| 4 | An operation lookup is pending, or commit may have happened; retain an uncertain mutation's exact frame for recovery. | yes | yes |
| 5 | Ledger verification found divergence. | — | yes |
| 64 | CLI command usage was refused before dispatch. | yes | — |
| 70 | Unexpected CLI defect. | yes | — |
| 130 | CLI interrupted or cancelled. | yes | — |

## case open

Open a case

Register the incident, claimant, insurer and amount claimed.

```sh
claimcore case open [--operation-id VALUE] CASE-REFERENCE --incident-date VALUE --incident-notification-date VALUE --incident-country VALUE --claimant-name VALUE --insurer-name VALUE --claimed-amount VALUE --claimed-currency VALUE [--incident-description VALUE] [--incident-description-file PATH | --incident-description-stdin | --incident-description-editor] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--incident-date` | Incident date — Use YYYY-MM-DD, for example 2026-10-06. |  |  |
| `--incident-notification-date` | Notification date — Use YYYY-MM-DD, for example 2026-10-06. |  |  |
| `--incident-country` | Country — Use two uppercase letters. ZZ means unknown or international. |  |  |
| `--claimant-name` | Claimant name — Up to 200 characters. |  |  |
| `--insurer-name` | Insurer — Up to 200 characters. |  |  |
| `--claimed-amount` | Claimed amount — Use up to 18 digits before the decimal point and up to 4 after it. Use ASCII digits and a dot; no spaces, signs, commas or redundant leading zeros. |  |  |
| `--claimed-currency` | Claimed currency — 3 uppercase ASCII letters (A–Z). |  |  |
| `--incident-description` | Incident description — Optional. Up to 600 characters; line breaks allowed. Spaces and spelling are kept as typed; control and text-direction characters are not allowed. |  |  |

## case amend-registration

Update claim details

Change selected claim details. Details you leave out and the case reference stay as they are.

```sh
claimcore case amend-registration [--operation-id VALUE] CASE-REFERENCE --expected-revision N [--incident-date VALUE] [--incident-notification-date VALUE] [--incident-country VALUE] [--claimant-name VALUE] [--insurer-name VALUE] [--claimed-amount VALUE] [--claimed-currency VALUE] [--incident-description VALUE] [--incident-description-file PATH | --incident-description-stdin | --incident-description-editor] [--clear-incident-description] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case version you reviewed. |  |  |
| `--incident-date` | Incident date — Use YYYY-MM-DD, for example 2026-10-06. |  |  |
| `--incident-notification-date` | Notification date — Use YYYY-MM-DD, for example 2026-10-06. |  |  |
| `--incident-country` | Country — Use two uppercase letters. ZZ means unknown or international. |  |  |
| `--claimant-name` | Claimant name — Up to 200 characters. |  |  |
| `--insurer-name` | Insurer — Up to 200 characters. |  |  |
| `--claimed-amount` | Claimed amount — Use up to 18 digits before the decimal point and up to 4 after it. Use ASCII digits and a dot; no spaces, signs, commas or redundant leading zeros. |  |  |
| `--claimed-currency` | Claimed currency — 3 uppercase ASCII letters (A–Z). |  |  |
| `--incident-description` | Incident description — Optional. Up to 600 characters; line breaks allowed. Spaces and spelling are kept as typed; control and text-direction characters are not allowed. |  |  |

## case correct

Correct case facts

Correct details that were recorded wrongly. If the case is already paid and you change the decision, also confirm, replace or clear the payment date. Clearing a payment date does not refund money.

```sh
claimcore case correct [--operation-id VALUE] CASE-REFERENCE --expected-revision N --registration keep|update [--incident-date VALUE] [--incident-notification-date VALUE] [--incident-country VALUE] [--claimant-name VALUE] [--insurer-name VALUE] [--claimed-amount VALUE] [--claimed-currency VALUE] [--incident-description VALUE] --decision keep|update|clear [--payment-decision-date VALUE] [--payable-amount VALUE] [--payable-currency VALUE] --payment keep|replace|reaffirm|clear [--payment-date VALUE] [--incident-description-file PATH | --incident-description-stdin | --incident-description-editor] [--clear-incident-description] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case version you reviewed. |  |  |
| `--registration` | Claim — Keep registration facts or update selected facts. Omitted facts stay unchanged; amount and currency are asserted together. | keep, update |  |
| `--incident-date` | Incident date — Use YYYY-MM-DD, for example 2026-10-06. |  |  |
| `--incident-notification-date` | Notification date — Use YYYY-MM-DD, for example 2026-10-06. |  |  |
| `--incident-country` | Country — Use two uppercase letters. ZZ means unknown or international. |  |  |
| `--claimant-name` | Claimant name — Up to 200 characters. |  |  |
| `--insurer-name` | Insurer — Up to 200 characters. |  |  |
| `--claimed-amount` | Claimed amount — Use up to 18 digits before the decimal point and up to 4 after it. Use ASCII digits and a dot; no spaces, signs, commas or redundant leading zeros. |  |  |
| `--claimed-currency` | Claimed currency — 3 uppercase ASCII letters (A–Z). |  |  |
| `--incident-description` | Incident description — Optional. Up to 600 characters; line breaks allowed. Spaces and spelling are kept as typed; control and text-direction characters are not allowed. |  |  |
| `--decision` | Payment decision — Keep the decision, update selected facts, or clear it. Amount and currency must be asserted together. | keep, update, clear |  |
| `--payment-decision-date` | Payment decision date — Use YYYY-MM-DD, for example 2026-10-06. |  |  |
| `--payable-amount` | Payable amount — Use up to 18 digits before the decimal point and up to 4 after it. Use ASCII digits and a dot; no spaces, signs, commas or redundant leading zeros. |  |  |
| `--payable-currency` | Payable currency — 3 uppercase ASCII letters (A–Z). |  |  |
| `--payment` | Payment — Keep, replace, reaffirm or clear the recorded payment date. Reaffirmation retains the date; clearing it does not refund money. | keep, replace, reaffirm, clear |  |
| `--payment-date` | Payment date — Use YYYY-MM-DD, for example 2026-10-06. |  |  |

## case decide

Record payment decision

Record or replace the decided amount and currency. This does not send money.

```sh
claimcore case decide [--operation-id VALUE] CASE-REFERENCE --expected-revision N --payment-decision-date VALUE --payable-amount VALUE --payable-currency VALUE [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case version you reviewed. |  |  |
| `--payment-decision-date` | Payment decision date — Use YYYY-MM-DD, for example 2026-10-06. |  |  |
| `--payable-amount` | Payable amount — Use up to 18 digits before the decimal point and up to 4 after it. Use ASCII digits and a dot; no spaces, signs, commas or redundant leading zeros. |  |  |
| `--payable-currency` | Payable currency — 3 uppercase ASCII letters (A–Z). |  |  |

## case withdraw-decision

Withdraw payment decision

Remove the payment decision before anything is paid. Its history is kept.

```sh
claimcore case withdraw-decision [--operation-id VALUE] CASE-REFERENCE --expected-revision N [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case version you reviewed. |  |  |

## case record-payment

Record actual payment

Record the date the full decided amount was actually paid. This does not execute or verify a transfer.

```sh
claimcore case record-payment [--operation-id VALUE] CASE-REFERENCE --expected-revision N --payment-date VALUE [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case version you reviewed. |  |  |
| `--payment-date` | Payment date — Use YYYY-MM-DD, for example 2026-10-06. |  |  |

## case clear-payment

Correct an erroneous payment record

Remove an erroneously recorded payment date. This is not a refund or reversal of an actual transfer. Earlier history remains.

```sh
claimcore case clear-payment [--operation-id VALUE] CASE-REFERENCE --expected-revision N [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case version you reviewed. |  |  |

## case close

Close the case

Close the case when no more work is needed. Closing does not mean the claim was paid.

```sh
claimcore case close [--operation-id VALUE] CASE-REFERENCE --expected-revision N [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case version you reviewed. |  |  |

## case reopen

Reopen the case

Reopen the case. Its details and history are kept.

```sh
claimcore case reopen [--operation-id VALUE] CASE-REFERENCE --expected-revision N [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case version you reviewed. |  |  |

## case void

Void case record

Stop all work on this case, for example because it was entered by mistake. The case and its history are kept.

```sh
claimcore case void [--operation-id VALUE] CASE-REFERENCE --expected-revision N --reason VALUE [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case version you reviewed. |  |  |
| `--reason` | Reason — Up to 200 characters. |  |  |

## case reinstate

Reinstate case record

Allow work on this voided case again. Its open or closed status does not change.

```sh
claimcore case reinstate [--operation-id VALUE] CASE-REFERENCE --expected-revision N --reason VALUE [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case version you reviewed. |  |  |
| `--reason` | Reason — Up to 200 characters. |  |  |

## case erase

Erase case

Permanently remove this case’s details, work and reminders from ClaimCore. The case reference and the list of changes remain. Copies held elsewhere are not affected.

```sh
claimcore case erase [--operation-id VALUE] CASE-REFERENCE --expected-revision N [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--confirm TARGET] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-revision` | Reviewed case version — The case version you reviewed. |  |  |

## authority register

Register account

Add an account with a name and optional contact details. Assign roles separately.

```sh
claimcore authority register [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE --name VALUE [--contact VALUE] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `--principal-kind` | Account type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Sign-in provider — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Sign-in ID — The immutable subject or client ID of the target principal. |  |  |
| `--name` | Name — Up to 200 characters. |  |  |
| `--contact` | Contact details — Up to 200 characters. |  |  |

## authority grant

Grant role

Assign the selected role to this account.

```sh
claimcore authority grant [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE --role owner|case-reader|case-registrar|case-handler|payment-recorder|data-steward|auditor [--case-reference VALUE] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `--principal-kind` | Account type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Sign-in provider — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Sign-in ID — The immutable subject or client ID of the target principal. |  |  |
| `--role` | Role — The capability role to grant or revoke. | owner, case-reader, case-registrar, case-handler, payment-recorder, data-steward, auditor |  |
| `--case-reference` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |

## authority revoke

Revoke role

Remove the selected role assignment.

```sh
claimcore authority revoke [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE --role owner|case-reader|case-registrar|case-handler|payment-recorder|data-steward|auditor [--case-reference VALUE] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `--principal-kind` | Account type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Sign-in provider — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Sign-in ID — The immutable subject or client ID of the target principal. |  |  |
| `--role` | Role — The capability role to grant or revoke. | owner, case-reader, case-registrar, case-handler, payment-recorder, data-steward, auditor |  |
| `--case-reference` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |

## authority enable

Enable account

Let this account use ClaimCore again with its existing roles.

```sh
claimcore authority enable [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `--principal-kind` | Account type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Sign-in provider — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Sign-in ID — The immutable subject or client ID of the target principal. |  |  |

## authority disable

Disable account

Stop this account from using ClaimCore. Its recorded changes remain.

```sh
claimcore authority disable [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `--principal-kind` | Account type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Sign-in provider — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Sign-in ID — The immutable subject or client ID of the target principal. |  |  |

## authority set-profile

Update profile

Record a display name and optional contact details for an account. This does not change its identity or access permissions.

```sh
claimcore authority set-profile [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE --name VALUE [--contact VALUE] [--clear-contact] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `--principal-kind` | Account type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Sign-in provider — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Sign-in ID — The immutable subject or client ID of the target principal. |  |  |
| `--name` | Name — Up to 200 characters. |  |  |
| `--contact` | Contact details — Up to 200 characters. |  |  |

## authority erase-profile

Erase profile

Permanently remove this account’s saved name and contact details. Its sign-in identity, roles and recorded work remain. Earlier changes then show “Former account”.

```sh
claimcore authority erase-profile [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--confirm TARGET] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `--principal-kind` | Account type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Sign-in provider — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Sign-in ID — The immutable subject or client ID of the target principal. |  |  |

## authority admit

Add person

Add a person or service account with a name and its first roles.

```sh
claimcore authority admit [--operation-id VALUE] --principal-kind human|service --issuer VALUE --subject VALUE --name VALUE [--contact VALUE] --role ROLE[@CASE-REF]... [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `--principal-kind` | Account type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Sign-in provider — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Sign-in ID — The immutable subject or client ID of the target principal. |  |  |
| `--name` | Name — Up to 200 characters. |  |  |
| `--contact` | Contact details — Up to 200 characters. |  |  |
| `--role ROLE[@CASE-REF]...` | Access grants — 1 to 48 different role-and-case pairs. Name a case by its exact reference. | 1–48 | |

## work update

Update case work

Change who is responsible or update the handling summary.

```sh
claimcore work update [--operation-id VALUE] CASE-REFERENCE --expected-work-revision N [--responsible-actor-action clear|set] [--responsible-actor VALUE] [--handling-summary VALUE] [--handling-summary-file PATH | --handling-summary-stdin | --handling-summary-editor] [--clear-handling-summary] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--expected-work-revision` | Reviewed work version — The work revision you reviewed. Financial fact revisions are independent. |  |  |
| `--responsible-actor-action` | Responsibility change — Choose set or clear; omit to keep the current responsible account. | clear, set |  |
| `--responsible-actor` | Responsible person — Use the exact registered account UUID. |  |  |
| `--handling-summary` | Handling summary — Optional. Up to 600 characters; line breaks allowed. Spaces and spelling are kept as typed; control and text-direction characters are not allowed. |  |  |

## reminder create

Add reminder

Plan the next action for this case.

```sh
claimcore reminder create [--operation-id VALUE] CASE-REFERENCE --action VALUE --due VALUE [--action-file PATH | --action-stdin | --action-editor] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--action` | Action — Use one line of plain text, up to 200 Unicode scalar values. |  |  |
| `--due` | Due date — Use a Gregorian date, YYYY-MM-DD. Past and future dates are allowed. |  |  |

## reminder update

Update reminder

Change the action or date of this pending reminder. Earlier versions remain in history.

```sh
claimcore reminder update [--operation-id VALUE] CASE-REFERENCE --reminder-id VALUE --expected-reminder-revision N --action VALUE --due VALUE [--action-file PATH | --action-stdin | --action-editor] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--reminder-id` | Reminder ID — The immutable identity of one reminder in this case. |  |  |
| `--expected-reminder-revision` | Reviewed reminder version — Use the positive revision of this exact reminder that you reviewed. Other reminders have independent revisions. |  |  |
| `--action` | Action — Use one line of plain text, up to 200 Unicode scalar values. |  |  |
| `--due` | Due date — Use a Gregorian date, YYYY-MM-DD. Past and future dates are allowed. |  |  |

## reminder complete

Complete reminder

Mark this action as done. If it was the last one, the case will need a next action.

```sh
claimcore reminder complete [--operation-id VALUE] CASE-REFERENCE --reminder-id VALUE --expected-reminder-revision N [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--reminder-id` | Reminder ID — The immutable identity of one reminder in this case. |  |  |
| `--expected-reminder-revision` | Reviewed reminder version — Use the positive revision of this exact reminder that you reviewed. Other reminders have independent revisions. |  |  |

## reminder cancel

Cancel reminder

Cancel this action without marking it done. Its history is kept.

```sh
claimcore reminder cancel [--operation-id VALUE] CASE-REFERENCE --reminder-id VALUE --expected-reminder-revision N [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--reminder-id` | Reminder ID — The immutable identity of one reminder in this case. |  |  |
| `--expected-reminder-revision` | Reviewed reminder version — Use the positive revision of this exact reminder that you reviewed. Other reminders have independent revisions. |  |  |

## reminder reopen

Reopen reminder

Make this action pending again with a new due date.

```sh
claimcore reminder reopen [--operation-id VALUE] CASE-REFERENCE --reminder-id VALUE --expected-reminder-revision N --due VALUE [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--reminder-id` | Reminder ID — The immutable identity of one reminder in this case. |  |  |
| `--expected-reminder-revision` | Reviewed reminder version — Use the positive revision of this exact reminder that you reviewed. Other reminders have independent revisions. |  |  |
| `--due` | Due date — Use a Gregorian date, YYYY-MM-DD. Past and future dates are allowed. |  |  |

## reminder complete-and-plan-next

Complete and plan next action

Complete this reminder and plan the next action together.

```sh
claimcore reminder complete-and-plan-next [--operation-id VALUE] CASE-REFERENCE --reminder-id VALUE --expected-reminder-revision N --action VALUE --due VALUE [--action-file PATH | --action-stdin | --action-editor] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--preview]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--operation-id` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--reminder-id` | Reminder ID — The immutable identity of one reminder in this case. |  |  |
| `--expected-reminder-revision` | Reviewed reminder version — Use the positive revision of this exact reminder that you reviewed. Other reminders have independent revisions. |  |  |
| `--action` | Action — Use one line of plain text, up to 200 Unicode scalar values. |  |  |
| `--due` | Due date — Use a Gregorian date, YYYY-MM-DD. Past and future dates are allowed. |  |  |

## case timeline

Case activity

Changes to the case details, work and reminders that you are allowed to see.

```sh
claimcore case timeline CASE-REFERENCE [--filter facts|work] [--cursor VALUE] [--limit N] [--format text|json|csv] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--all]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--filter` | Timeline entries — Show case facts or work and reminders. Leave unset to show both. | facts, work |  |
| `--cursor` | Cursor — The next-page cursor from the previous reply. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## reminder get

View a reminder

View this reminder, including whether it is completed or cancelled.

```sh
claimcore reminder get CASE-REFERENCE --reminder-id VALUE [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--reminder-id` | Reminder ID — The immutable identity of one reminder in this case. |  |  |

## reminder list

List reminders

List the reminders you can view, starting with the earliest due date.

```sh
claimcore reminder list [--case-reference VALUE] [--state pending|completed|cancelled] [--view pending|mine|overdue|today|upcoming|completed|cancelled] [--due overdue|today|upcoming] [--assignment mine|unassigned] [--text VALUE] [--cursor VALUE] [--limit N] [--include-inactive true|false] [--format text|json|csv] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--all]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--case-reference` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--state` | Reminder state — Select current pending, completed or cancelled records. Omission selects pending reminders. | pending, completed, cancelled |  |
| `--view` | Reminder view — Choose a standard reminder view. Explicit state, assignment, date, text and parent filters further restrict its results. | pending, mine, overdue, today, upcoming, completed, cancelled |  |
| `--due` | Reminder due — Filter individual reminder dates against the installation business date; this differs from the case minimum next due. | overdue, today, upcoming |  |
| `--assignment` | Responsibility — Filter by who is responsible, among the cases you can view. Viewing access alone does not make anyone responsible. | mine, unassigned |  |
| `--text` | Search text — Up to 100 characters. |  |  |
| `--cursor` | Cursor — The next-page cursor from the previous reply. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |
| `--include-inactive` | Include inactive case work — Explicitly include work on closed or voided cases. Pending reminders retain their dates and state. |  |  |

## case assignees

Eligible responsible people

List the people who can be made responsible for this case.

```sh
claimcore case assignees CASE-REFERENCE [--cursor VALUE] [--limit N] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |
| `--cursor` | Cursor — The next-page cursor from the previous reply. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## case get

View a case

View the current case details and available actions.

```sh
claimcore case get CASE-REFERENCE [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `CASE-REFERENCE` | Case reference — Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained. |  |  |

## case list

List cases

List the cases you can view.

```sh
claimcore case list [--cursor VALUE] [--limit N] [--lifecycle active|voided|erased]... [--status opened|closed]... [--progress undecided|decided|paid]... [--text VALUE] [--order reference|recent|notified|follow-up] [--assignment mine|unassigned] [--due overdue|today|upcoming|unscheduled|inactive] [--view needs-attention|mine|overdue|due-today|no-next-action|unassigned|undecided|decided-unpaid|closed|voided|all] [--responsible-actor VALUE] [--format text|json|csv] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run] [--all]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--cursor` | Cursor — The next-page cursor from the previous reply. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |
| `--lifecycle` | Lifecycle — Restrict results to the selected lifecycles. Omit this filter to include every lifecycle. | active, voided, erased |  |
| `--status` | Status filter — Restrict results to the selected open or closed statuses. Erased cases have no status. | opened, closed |  |
| `--progress` | Payment progress — Restrict results to undecided, decided or paid cases. Erased cases have no payment progress. | undecided, decided, paid |  |
| `--text` | Search text — Up to 100 characters. |  |  |
| `--order` | Order — Order by reference, latest case-fact change, notification date or earliest pending reminder date. Fact changes sort newest first; notification and reminder dates sort oldest first, with missing dates last. Reference breaks ties. | reference, recent, notified, follow-up | REFERENCE |
| `--assignment` | Responsibility — Filter by who is responsible, among the cases you can view. Viewing access alone does not make anyone responsible. | mine, unassigned |  |
| `--due` | Follow-up state — Classify each case by its earliest pending reminder: overdue, today, upcoming, no pending reminder or inactive. Individual reminder queue dates use a separate filter. | overdue, today, upcoming, unscheduled, inactive |  |
| `--view` | Case view — Choose a standard case view. Other filters further restrict its results. | needs-attention, mine, overdue, due-today, no-next-action, unassigned, undecided, decided-unpaid, closed, voided, all |  |
| `--responsible-actor` | Responsible account filter — Restrict cases to the selected responsible account within your readable scope. Other filters further restrict its results. |  |  |

## case summary

Case summary

Count the cases you can view and total the claimed and payable amounts by currency.

```sh
claimcore case summary [--lifecycle active|voided|erased]... [--status opened|closed]... [--progress undecided|decided|paid]... [--text VALUE] [--order reference|recent|notified|follow-up] [--assignment mine|unassigned] [--due overdue|today|upcoming|unscheduled|inactive] [--view needs-attention|mine|overdue|due-today|no-next-action|unassigned|undecided|decided-unpaid|closed|voided|all] [--responsible-actor VALUE] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--lifecycle` | Lifecycle — Restrict results to the selected lifecycles. Omit this filter to include every lifecycle. | active, voided, erased |  |
| `--status` | Status filter — Restrict results to the selected open or closed statuses. Erased cases have no status. | opened, closed |  |
| `--progress` | Payment progress — Restrict results to undecided, decided or paid cases. Erased cases have no payment progress. | undecided, decided, paid |  |
| `--text` | Search text — Up to 100 characters. |  |  |
| `--order` | Order — Order by reference, latest case-fact change, notification date or earliest pending reminder date. Fact changes sort newest first; notification and reminder dates sort oldest first, with missing dates last. Reference breaks ties. | reference, recent, notified, follow-up | REFERENCE |
| `--assignment` | Responsibility — Filter by who is responsible, among the cases you can view. Viewing access alone does not make anyone responsible. | mine, unassigned |  |
| `--due` | Follow-up state — Classify each case by its earliest pending reminder: overdue, today, upcoming, no pending reminder or inactive. Individual reminder queue dates use a separate filter. | overdue, today, upcoming, unscheduled, inactive |  |
| `--view` | Case view — Choose a standard case view. Other filters further restrict its results. | needs-attention, mine, overdue, due-today, no-next-action, unassigned, undecided, decided-unpaid, closed, voided, all |  |
| `--responsible-actor` | Responsible account filter — Restrict cases to the selected responsible account within your readable scope. Other filters further restrict its results. |  |  |

## work views

Work views

How many cases and reminders are in each view today.

```sh
claimcore work views [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |

## operation get

Check a change

Shows whether a change was recorded. If it was not, the answer becomes final 10 minutes after the change was prepared.

```sh
claimcore operation get OPERATION-ID [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `OPERATION-ID` | Operation ID — A UUID version 7 that identifies this change. Reuse it only to retry the same change. |  |  |

## authority list

List people

List accounts and available access changes.

```sh
claimcore authority list [--text VALUE] [--role owner|case-reader|case-registrar|case-handler|payment-recorder|data-steward|auditor] [--cursor VALUE] [--limit N] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--text` | Search people — Use up to 200 characters. Capitalization does not matter. |  |  |
| `--role` | Role filter — Match recognized role assignments across the installation or individual cases. This filter does not test current capabilities or whether the account is enabled. | owner, case-reader, case-registrar, case-handler, payment-recorder, data-steward, auditor |  |
| `--cursor` | Cursor — The next-page cursor from the previous reply. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## authority grants

View roles

List the roles assigned to an account.

```sh
claimcore authority grants ACTOR-ID [--cursor VALUE] [--limit N] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `ACTOR-ID` | Account ID — The registered actor selected for grant review or authority history. |  |  |
| `--cursor` | Cursor — The next-page cursor from the previous reply. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## authority history

Access history

View access changes, starting with the most recent.

```sh
claimcore authority history [--actor-id VALUE] [--cursor VALUE] [--limit N] [--format text|json|csv] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--actor-id` | Account ID — The registered actor selected for grant review or authority history. |  |  |
| `--cursor` | Cursor — The next-page cursor from the previous reply. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## authority requests

Access requests

List sign-in requests waiting for access.

```sh
claimcore authority requests [--cursor VALUE] [--limit N] [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--cursor` | Cursor — The next-page cursor from the previous reply. |  |  |
| `--limit` | Page size — The maximum number of rows to return on this page. |  | 50 |

## authority requests dismiss

Dismiss access request

Remove this request without granting access.

```sh
claimcore authority requests dismiss --principal-kind human|service --issuer VALUE --subject VALUE [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |
| `--principal-kind` | Account type — Whether the target principal is a person or a service. | human, service |  |
| `--issuer` | Sign-in provider — The exact HTTPS issuer of the target principal. |  |  |
| `--subject` | Sign-in ID — The immutable subject or client ID of the target principal. |  |  |

## context

Your access

Show your permissions and available resources.

```sh
claimcore context [--format text|json] [--language en|lv|ar|ko|zh-Hans|zh-Hant|ja|es|de] [--dry-run]
```

| Input | Meaning | Choices | Default |
| --- | --- | --- | --- |

