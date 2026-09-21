# Rejection diagnostics

Ordinary core rejections expose a machine-readable cause separately from their human explanation.
This is the first localization foundation, not a multilingual user interface. The domain still owns
validation and transitions; changing an explanation cannot authorize a command or settle an uncertain
operation. [Architecture](architecture.md) owns the wider boundaries.

## Scope and ownership

`DomainError.InvalidInput` contains a closed `InputTarget` and `InputViolation`, not a field-name
string or English sentence. Violation cases distinguish text, date, amount, command and correction
causes. Length and numeric-format limits come from the existing Domain scalar constraints.

Application's `Rejection` is a closed reason. Its coarse `Code`, `Field`, `ActualVersion` and `Action`
are derived from that reason, not independently writable record properties. It has no `Message`
property. `RejectionDiagnostics.describe` projects a private diagnostic value with an explicit
`RejectionDiagnosticId` and closed `DiagnosticParameters`. Parameters contain numeric constraint
limits only; submitted names, references, dates, amounts, currency identifiers, cursor bytes,
filesystem paths and provider exceptions are not arguments. Field targets are declared tokens; the
revision targets use the public spellings `expectedRevision` and `revision`.

The Contracts adapter owns the default English renderer, `RejectionPresentation.render`. It selects
whole explanations using the diagnostic identity and typed parameters. It never parses a sentence
or consults ambient UI culture. CLI and Web share the same rejection codec. A future catalog can
replace the presentation without making Domain or Application depend on localization infrastructure.

This applies wherever an ordinary `Rejection` appears, including rejection inside a definite recovery
execution result. `CoreFault`, `RecoveryRejection`, host/protocol admission failures, cancellation and
commit uncertainty remain separate outcome families. They have not all acquired structured diagnostic
parameters in this change. Do not treat a failure, cancellation or unknown commit as a rejection.

## Machine contract

Every ordinary CLI-v3/Web-v2 rejection includes `diagnostic.id` and the exact `diagnostic.parameters`
object. For example, an invalid claimed-amount grammar has this cause:

```json
{
  "id": "INPUT_DECIMAL_FORMAT",
  "parameters": {
    "maximumIntegerDigits": 18,
    "maximumFractionalDigits": 4
  }
}
```

A parameterless cause uses `{}`, not null or an omitted property. An unknown identity, missing
required argument, argument belonging to another identity, excess argument or wrong argument type
is invalid. The retained `message` is display text, not identity or control input; two correction
refusals can share coarse code `INVALID_INPUT` and still expose different diagnostic identities.

Semantic discovery publishes `rejectionDiagnostics`, including each stable identity and its exact
parameter names and integer bounds. The generated semantic schema fixes that inventory. Diagnostic
metadata participates in the semantic and wire fingerprints; presentation sentences do not.
Consumers must use the matching generated schemas and types. No old-shape decoder or English-message
fallback is retained: rebuild the host and browser together, and update native consumers that used
the old `Rejection` record or `DomainError.InvalidInput` string payload.

Canonical request bytes, content-bound operation identity, accepted case fields, revisions, database
schema and recovery authority are unchanged. No migration is required for this contract-only break.

## Design and review record

The design was reviewed against merged baseline `97a9f23` before implementation. The chosen unit of
change was ordinary rejection meaning end-to-end, rather than a partial UI translation or a generic
message dictionary. Design QA required preservation of coarse codes, recommended actions, admission
ordering and all non-rejection outcomes; explicit tokens rather than source-name-derived identifiers;
closed safe targets and parameters; and exact positive and negative wire examples.

Implementation QA additionally factored shared parameter metadata in Web schemas to preserve the
existing standalone-validator size limit. Sharing changes representation, not the admitted data.
Internal request admission is separated from accepted case state while remaining behind the public
`Claim` facade. These are agent design and source reviews, not independent owner approval.

Before merging changes to this policy, the owner reviews the identities and parameters, privacy,
unchanged outcome/authority semantics, and the deliberate wire/native break. Passing CI or a modified
review hash is not a substitute for that decision. [Development](development.md) owns verification
commands and evidence registration.

## Qualification and next boundaries

Native tests cover the closed vocabulary and targets, safe arguments, specific causes emitted by
real Domain and Application admission, native/CLI/Web parity, fingerprint sensitivity and ambient
culture independence. Generated CLI and Web corpora include every ordinary diagnostic, translated
explanations and malformed diagnostic variants. The existing runtime and standalone validators must
agree on these examples; missing or substituted test evidence is still refused by the common gate.

The subsequent localization work must cover the remaining failure families and establish
presentation-owned catalogs with an explicit locale/fallback policy. UI language, display locale,
installation business calendar, currency and jurisdiction are independent. Locale must not enter
`CaseFields`, canonical records or operation authority. Language switching needs real-locale,
pseudolocale, RTL and accessibility qualification without losing drafts, reminting operation IDs,
rebasing revisions, changing authored content or triggering recovery. Database/recovery fresh-baseline
work remains a separate coherent change, not a deletion of migration files in this PR.
