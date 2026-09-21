# Core outcome diagnostics

Ordinary rejections, core faults, and recovery refusals expose machine-readable causes separately
from their human explanations. This is a localization foundation, not a multilingual user interface. The domain still owns
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
execution result. `CoreFault` and `RecoveryRejection` are also closed reasons with derived `Code` and
`Action`, without writable messages or arbitrary argument bags. `CoreFaults` declares 22 explicit
fault identities; `RecoveryRejections` declares 20 refusal identities. Their arguments are exactly
empty objects: none needs submitted values or provider details. Recovery input causes distinguish
operation IDs, digests, page limits, invalid and wrongly bound cursors, and dismissal confirmation.
Source-artifact digest mismatch is distinct from a retained request digest mismatch.

`CoreFaultPresentation` and `RecoveryRejectionPresentation` own default English in Contracts. Those
faults are projected everywhere they occur, including nested preparation, execution, settlement,
dismissal, and import outcomes. The outer outcome still owns commit knowledge. A fault diagnostic
alone does not prove non-commit or authorize retry. Defensive store-cancellation projections retain
their existing meaning; ordinary cancellation paths still return their cancellation outcomes.

Host/protocol admission failures and local adapter failures remain separate from the core. CLI
runtime opening, endpoint-shape mismatches and private-export write failures use a closed
`CliLocalFault` and distinct `localFailure` result (exit 3, stop-and-investigate), not a fabricated
`CoreFault`. These eight local identities have no input-derived arguments. Raw CLI protocol errors,
Web host failures and database administration diagnostics remain outside this completed core slice.

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

Semantic discovery publishes `rejectionDiagnostics`, `faultDiagnostics`, and `recoveryDiagnostics`,
including each stable identity and its exact parameter names and integer bounds. The generated semantic schema fixes that inventory. Diagnostic
metadata participates in the semantic and wire fingerprints; presentation sentences do not.
Consumers must use the matching generated schemas and types. No old-shape decoder or English-message
fallback is retained: rebuild the host and browser together, and update native consumers that used
the old `Rejection`, `CoreFault`, or `RecoveryRejection` records or `DomainError.InvalidInput` string
payload. The shared metadata type is `DiagnosticDefinition`; no old-name alias is retained. Fault
and recovery-refusal schemas additionally correlate identity, coarse code, and recommended action.
A contradictory pair is invalid even when both values are separately known. CLI `localFailure` has
its own exact schema; it cannot admit old core fault shapes as a compatibility fallback.

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

The follow-up design was recorded against merged baseline `19d64a45` before product edits. It
selected core faults and lifecycle refusals end-to-end, rather than a universal string dictionary.
Separate design QA rejected merging faults into business rejection, interpreting English to find a
cause, and retaining CLI-invented core faults. It required unchanged admission ordering: resolution
checks identity and digest before cancellation, whereas recovery list, inspect, dismiss and export
check cancellation first. Tests exercise both orders and prove no recovery attempt is started.

Implementation QA separated discovery validation into its own lazy chunk instead of increasing the
existing per-chunk, initial-load or aggregate-compressed size limits. Positive and negative controls
qualify endpoint ownership, missing groups and lazy artifact loading; the same corpora exercise all
runtime and standalone validators. This changes loading representation, not accepted wire data or
editor/recovery state. Two synthetic browser refusal fixtures now use a typed, valid dismissed-
preparation diagnostic; malformed responses remain rejected rather than tolerated by the UI.

Before merging changes to this policy, the owner reviews the identities and parameters, privacy,
unchanged outcome/authority semantics, and the deliberate wire/native break. Passing CI or a modified
review hash is not a substitute for that decision. [Development](development.md) owns verification
commands and evidence registration.

## Qualification and next boundaries

Native tests cover the closed vocabulary and targets, safe arguments, specific causes emitted by
real Domain and Application admission, native/CLI/Web parity, fingerprint sensitivity and ambient
culture independence. Generated CLI and Web corpora include every ordinary, fault, and recovery diagnostic, translated
explanations, missing or extraneous arguments, cross-family IDs and contradictory code/action
variants. The CLI corpus also exercises every local failure on every runtime endpoint. The existing runtime and standalone validators must
agree on these examples; missing or substituted test evidence is still refused by the common gate.

The subsequent localization work must cover transport/host/administration failures and establish
presentation-owned catalogs with an explicit locale/fallback policy. UI language, display locale,
installation business calendar, currency and jurisdiction are independent. Locale must not enter
`CaseFields`, canonical records or operation authority. Language switching needs real-locale,
pseudolocale, RTL and accessibility qualification without losing drafts, reminting operation IDs,
rebasing revisions, changing authored content or triggering recovery. Database/recovery fresh-baseline
work remains a separate coherent change, not a deletion of migration files in this PR.
