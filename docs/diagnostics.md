# Product diagnostics

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
`CoreFault`. These eight local identities have no input-derived arguments. Raw protocol, Web host and owner-only administration diagnostics are described below; they remain
separate from business and recovery outcomes.

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

The subsequent localization work establishes
presentation-owned catalogs with an explicit locale/fallback policy. UI language, display locale,
installation business calendar, currency and jurisdiction are independent. Locale must not enter
`CaseFields`, canonical records or operation authority. Language switching needs real-locale,
pseudolocale, RTL and accessibility qualification without losing drafts, reminting operation IDs,
rebasing revisions, changing authored content or triggering recovery. Database/recovery fresh-baseline
work remains a separate coherent change, not a deletion of migration files in this PR.

## Transport, host and administration boundaries

The transport follow-up completes the remaining product failure boundaries. CLI `ProtocolFailure`
is a private value of a closed `ProtocolProblem` and known-member location; it carries no writable
message. Unknown JSON property names collapse to the known containing object or root. Oversized
NDJSON lines are drained before the next frame, and wrong scalar kinds return typed refusals.
`claimcore describe diagnostics` publishes the protocol and process schemas without opening a store.

HTTP readers return `HttpInputProblem`, not English sentences. The host policy binds each identity
to its code, HTTP status and execution phase; the serialized `status` must agree with actual HTTP
status. Framework method refusals also use this policy. Runtime and standalone browser validators
reject contradictions and old shapes. Security admission remains intentionally bounded and does
not expose credentials, unexpected property names or provider information.

The request-local failure boundary is installed before connection, authentication and rate-limit
middleware. It distinguishes pre-dispatch failure, unconfirmed dispatch, and failed delivery after
a returned result. Only pre-dispatch failure claims `NOT_STARTED`. Once a response has started, the
host aborts instead of appending another JSON document. A stopped host or broken response is never
proof that a command rolled back. Preserve exact authored operation identity for recovery.

CLI endpoint execution returns an `EndpointReply` before it is encoded. Frame-local state records
runtime acquisition, dispatch, native-result observation and completed output flushing separately.
The state is reset before every input frame and after successful flushing; no later parse/read
failure inherits a prior operation ID. A failed write or flush makes one bounded stderr attempt,
never a second stdout frame or an implicit replay. Exact known operation context is separate from
diagnostic arguments; retained import identity is captured from its typed result. A potentially
state-changing frame with lost delivery exits 4, even if its returned result was confirmed.

`HostSecurity` owns closed private-file failures and still has no transport dependency. Consumers
map those causes into their own diagnostics. No-follow, owner-only, descriptor identity, bounded
UTF-8, exclusive creation, cleanup and buffer-zeroing rules remain in force. Web startup exposes
only known setting tokens and bounded causes; unexpected exceptions never escape as stack traces
or reflected type names.

`ClaimCore.Database` owns its administration diagnostic codec and schema; it does not depend on
Contracts. `describe diagnostics`, help and version remain configuration-free. Known option names
and fixed bounds may be diagnostic arguments, but unknown arguments, paths and connection values
are never echoed. The contract generator references Database and Postgres solely as development
tooling to reproduce this owner-owned schema and real-codec conformance corpus.

Postgres administration returns `AdministrationOutcome`: `Completed`, `NotStarted`, `NotCommitted`,
`CompletionUnknown`, or `CompletedCleanupFailed`. A commit call is marked before execution; an
exception during commit cannot prove rollback. A confirmed checkpoint survives subsequent disposal
or reporting failure. Each operation permits exactly one commit. Pruning dry-runs still write audit
evidence, so they use the same completion discipline. Current-schema qualification no longer
implicitly creates a journal; only explicit migration initializes it. Migration bytes are unchanged.

Administration stdout/stderr is structured JSON. Completed maintenance and failed output are
separate outcomes: output failure cannot claim the action failed. There is one bounded reporting
attempt, no automatic maintenance retry. Large terminal counts and byte totals are canonical
nonnegative Int64 strings, not lossy JSON numbers. Owner-only administration remains isolated from
case-work hosts, and a diagnostic does not grant permission to change stored state.

## Transport design and separate QA record

The transport design was reviewed against merged baseline `dd9fa2f` before product edits. It kept
native cause ownership at each boundary, presentation outward, and locale out of canonical records.
Separate design QA rejected arbitrary message/argument bags, English-based HTTP classification,
previous-frame delivery state and the conflation of maintenance execution with reporting. The
complete transport/host/administration work is one package, not deferred slices.

Implementation QA added real emitted-cause and hostile-schema controls, secret canaries, per-frame
write/flush/read failures, serialization after native-result observation, and administrative failure
injection before work, before commit, during commit and after confirmation. Positive controls prove
successful operation and changed display copy; negative controls prove refusal and no implicit
replay. Every added test is registered in the same reviewed evidence inventory as its producer.
These records describe agent design/source review, not independent owner approval.

## Deliberate consumer break

Protocol and host failure objects require structured diagnostics. Host objects also require exact
status correlation. Process stderr and owner-only administration use their published JSON schemas;
old string constructors, writable message records and old response shapes are not retained.
Native maintenance callers must handle every `AdministrationOutcome` case. Rebuild CLI, browser and
host from matching contracts. Semantic business identity, case fields, request bytes, revisions,
accepted history and recovery authority do not change. No case-data migration is introduced here.

The remaining release packages are presentation catalogs with fully qualified language switching;
fresh-baseline database/recovery reset; and owner-review enforcement. They remain three packages
following this one, before the future 0.5.0 release.

### Browser validation loading and size review

Host-failure validation is emitted once in a separate lazy module, not duplicated in
each discovery, core and recovery module. The existing runtime and standalone
corpora qualify the same strict schemas; this changes loading, not admission.

The expanded product diagnostics increase the aggregate compressed JavaScript
budget from 192 KiB to 200 KiB. The measured candidate is 197,683 bytes at gzip
level 9, including the shared host validator (5,141 bytes). The per-chunk 600 KiB,
initial-load 900 KiB and CSS 64 KiB limits are unchanged. This is an explicit
feature-size budget revision for owner review, not a claim of unchanged budgets
or a relaxation of diagnostic, coverage or evidence validation.
