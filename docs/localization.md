# Browser presentation and localization

## Design and pre-implementation QA

Rebuilt from exact main `1febe2f752224eae0e514da6d682dee3f49db7b9` after the earlier
unpublished workspace was lost. The original source tree is verified as
`1324a33d34ec61c3fe35662542048738088f7391`. This document records design before
product edits. Verification results must be supplied by the resulting candidate;
prior-session test numbers are not evidence for this rebuild.

The browser owns presentation; the core owns facts, validation, command availability,
operation identity, admission, execution and recovery. Preferences do not enter
CaseFields, requests, request headers, canonical records, recovery artifacts or database
configuration. Native CLI and administration output remain unchanged.

### Design pass

A stable, synchronous provider above the application owns only validated presentation
preferences. UI languages are English (`en`), Latvian (`lv`), Arabic (`ar`) and explicitly
labelled expanded English (`en-XA`, a pseudolocale). Display format is independently
selected from `en-GB`, `lv-LV`, `ar-EG`. Defaults are `en` and `en-GB`, independent of
navigator language, operating-system locale, country, IP, timezone and business calendar.
Regional language tags resolve to a supported base; unsupported or malformed selections
fall back to the explicit defaults. ICU plural rules follow UI language, while numeric
formatting follows display locale.

Only `{version: 1, language, displayLocale}` may be persisted in the browser-local
`claimcore.presentation.v1` record. Oversized, malformed or additional-key records are
refused. Storage denial leaves working in-memory preferences and an accessible status.
There is no cross-tab listener, external translation service, reload, locale-keyed tree,
language-loading request or reducer language action. Controls remain available inside
modal dialogs; they do not submit, dismiss, replace or reopen those dialogs.

Catalogs have stable keys and typed ICU arguments. English is the key/argument authority;
Latvian and Arabic must be complete. Pseudolocalization changes literal AST nodes only.
Catalog validation covers metadata keys, reachable diagnostics, exact parameter roles,
plural categories, duplicate JSON keys, unsafe markup and directional controls. No UI
translates by parsing server English. Async state retains closed notices/validated
diagnostics and renders them at the current language, including delayed results.

Authoring preserves canonical ISO date and decimal text, including invalid user input.
Accepted display amounts use BigInt integer formatting plus exact fractional digit
mapping: never binary floating-point conversion or currency-rounding. Date-only display
validates Gregorian components and formats through an explicit UTC carrier with Gregorian
calendar and four-digit years; instants retain original offset-bearing wire text.
Canonical individual copy and recovery downloads remain byte/value faithful.

Language and direction update document attributes and React Aria explicitly. Logical CSS
supports RTL and expansion. Authored text is isolated, and machine identifiers and numeric
inputs have explicit LTR direction. Native labelled selectors and status announcements,
keyboard focus, dialogs, narrow layout and real-language axe checks are included in the
same feature qualification.

### Separate design QA

| Challenge | Resolution before implementation |
|---|---|
| Translators in effect dependencies replay reads or mutations | Request hooks remain locale-blind; only rendering subscribes to preferences. |
| English strings retained by async state stay untranslated | Store immutable notice identities and safe arguments, not rendered strings. |
| A catalog remount clears review checkbox or draft | Synchronous stable provider; no language key, suspension or tree replacement. |
| Global controls are inert behind a modal | Dialog-local controls share the same provider and preserve confirmation and focus. |
| Monetary precision or calendar meaning changes | Exact digit formatting, UTC Gregorian date carrier, canonical authoring and copy. |
| Display format changes plural grammar | UI-language PluralRules; display-locale NumberFormat. |
| Pseudolocalization changes commands or authored content | Transform literals only, never arguments or request objects. |
| Missing/hostile diagnostic text implies retry authority | Generic safe explanation only; typed outcome/action authority stays unchanged. |
| Persistence error aborts a command | Preference persistence is separate, bounded, exception-safe and data-minimal. |
| File-read rejection leaves import busy forever | Adapter classifies unreadable file; state receives a closed local notice. |
| Translated errors refocus an input unnecessarily | Field-error identity remains stable; rendering does not change the error record. |
| Prior test counts are mistaken for current results | Rebuild and run qualification; record only executed evidence. |

## Implementation QA refinements

Consent is tied to the exact prepared identity and the specific received review instance, not
an editor-wide boolean. Language changes preserve that instance and its confirmation. A new
preparation requires fresh confirmation, even when it describes the same idempotent request.
Submitting checks that consent in the locale-blind coordinator, and the in-flight checkbox
cannot be changed. Correction-field focus uses canonical targets, including group-qualified
names, without using translated labels. Neither refinement changes operation bytes or core rules.

Unknown message/diagnostic keys are checked as own properties: prototype names such as
`constructor` cannot be interpreted as catalog entries. Closed rendered token coverage is
checked against generated type families, including attempt settlement and import artifact kinds.
The native free-form revocation reason is not used as a translation key or shown as a localized
explanation; the view renders the stable revocation state and its unchanged identifier/timestamp.

## Extension and qualification

All shipped catalogs are checked in and validated in the existing contract/lint pipeline.
A language addition requires full key/argument parity and locale/state/accessibility tests;
defensive English fallback is not permission to ship an incomplete translation. Translation
source review is agent review, not native-speaker certification or independent owner approval.

Regression cases cover switching while editing, preparing, reviewing, submitting, uncertain,
resolving and importing. Assert unchanged input nodes/values, operation IDs, expected
revisions, request digests, exact request bodies, confirmation, file identity and request
counts. Switching must not abort or start recovery. Test translated and pseudo copy,
Arabic RTL, canonical copying, denied/malformed storage, exact value extremes and plural
rules. Published-browser scenarios run under the existing three-browser qualification.

Catalog changes are made in `web/src/presentation/catalogs/`. Run
`npm --prefix web run localization:generate` and `npm --prefix web run localization:check`
together with the ordinary tests. Checked-in AST catalogs and argument declarations are derived,
not separate sources of truth. Coordinator source checks reject presentation imports and ambient
locale/storage access in API, domain, operation/session hooks and recovery-state modules. These
checks prevent ordinary drift; they do not constitute a sandbox against rewriting the checks.

### Layout and size policy

The review confirmation has an explicit checked/unchecked visual indicator, a keyboard-visible
focus outline and a disabled state. Browser automation activates its visible label rather than
trying to click React Aria's visually hidden input. Language/format controls in a modal are selected
within that modal; background controls are not used to drive an open review.

Catalogs are synchronous static imports in a separate production chunk: no language switch loads
code or reinitializes the tree. This feature deliberately revises the aggregate gzip JavaScript
allowance from 200 KiB to 256 KiB. The per-chunk 600-KiB, initial-load 900-KiB and stylesheet 64-KiB
allowances are unchanged. The increase pays for the complete real-language catalogs, pseudolocale
and ICU runtime rather than deleting translations or making safety-critical wording asynchronous.
The final handoff records the measured candidate size. This is an explicit size-policy decision
requiring owner review, not an unchanged-budget claim. Per-file coverage floors are unchanged.

No version/tag bump, SQL migration or historical recovery-format change belongs to this
package. The fresh-baseline database/recovery clean break remains the final agreed package.
Repository-administration activation remains separate from this browser feature.

## Design references

- [React state lifetime](https://react.dev/learn/preserving-and-resetting-state)
- [FormatJS ICU message formatting](https://formatjs.github.io/docs/intl-messageformat/)
- [W3C bidirectional isolation](https://www.w3.org/International/questions/qa-bidi-controls.en.html)
- [ECMA-402 mathematical numeric values](https://tc39.es/ecma402/)
