# Product diagnostics

Ordinary rejections, core faults, and recovery refusals expose machine-readable causes separately
from their human explanations. The browser presents these causes in English, Latvian and Arabic. The domain still owns
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
or consults ambient UI culture. CLI and Web share the same rejection codec. Contracts supplies build-only English literal and declared-hole parts through the locked generator, shared with invariant native rendering. The browser compiler constructs literal/argument ICU nodes directly from these parts, preserving apostrophe/brace text without an encoding/parser round trip. Catalog bounds, unsafe-literal checks and exact argument roles still apply. Domain and Application have no localization runtime dependency.

This applies wherever an ordinary `Rejection` appears, including rejection inside a definite recovery
execution result. `CoreFault` and `RecoveryRejection` are also closed reasons with derived `Code` and
`Action`, without writable messages or arbitrary argument bags. `CoreFaults` and `RecoveryRejections` declare their closed fault and refusal inventories. Their arguments are exactly
empty objects: none needs submitted values or provider details. Recovery input causes distinguish
operation IDs, digests, page limits, invalid and wrongly bound cursors, and dismissal confirmation.
Source-artifact digest mismatch is distinct from a retained request digest mismatch.

`CoreFaultPresentation` and `RecoveryRejectionPresentation` own default English in Contracts. Those
faults are projected everywhere they occur, including nested preparation, execution, settlement,
dismissal, and import outcomes. The outer outcome still owns commit knowledge. Both clients preserve
`RECOVER_EXACT` guidance inside refusal payloads as well as faults: denial of a new action does not settle an earlier attempt.
Revocation ends future authority without asserting historical nonexecution; exhausted attempts require
exact evidence reconciliation before new work. Browser cancellation notices distinguish this request's
admission/attempt boundary from ordinary refusal and uncertainty, without settling earlier attempts.
An access-specific `RECOVERY_ACCESS_UNAVAILABLE` fault is separate from invalid storage responses.
Before admission, the invocation retains its failed-before-attempt phase. After admission, it
retains the identified unresolved attempt; an access refusal never settles an earlier unknown
attempt as rejected. Preserve the exact operation ID, digest and request bytes and inspect
when authorized access is available.
Retained canonical or Domain-shape validation failure after a confirmed or historical attempt
start likewise preserves that exact unresolved attempt and its specific retained fault. A failure
to decode the returned summary retains previously validated identity and known admission state.
An unexpectedly faulted or cancelled `Start` invocation leaves admission unknown; only its typed
pre-commit cancellation result proves cancellation before an attempt.
A fault diagnostic alone does not prove non-commit or authorize retry. Defensive store-cancellation
projections retain their existing meaning; ordinary cancellation paths still return their cancellation outcomes.

Host/protocol admission failures and client-local failures remain separate from the core. CLI-v4 configuration, authentication, invalid service replies, and private-file refusals produce a distinct `localFailure` (exit 3), not a fabricated `CoreFault`; uncertain delivery is preserved as such. Web host and owner-only administration diagnostics remain separate from business and recovery outcomes. No local failure proves that an already dispatched mutation did not commit.

## Machine contract

Every ordinary Web-v3 service rejection includes `diagnostic.id` and the exact `diagnostic.parameters`
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

The diagnostic vocabulary is part of the current fresh-installation contract. It does not authorize reading an old database with this build or relabeling an old recovery artifact.

## Presentation and qualification

Generated service and CLI corpora exercise valid and hostile diagnostic shapes, including absent or extra parameters, cross-family identities, and contradictory code/action pairs. Browser validators and native codecs must agree on those shapes; an English sentence is never a machine identity. [Development](development.md) owns the exact current test commands and evidence inventory.

English, Latvian, and Arabic presentation catalogs are separate from semantic admission. UI language, display locale, installation business calendar, currency, and jurisdiction are independent. A language change cannot alter `CaseFields`, canonical records, an authored draft, a prepared operation ID, or recovery authority. The [database baseline](database.md) refuses old storage without changing it; current recovery formats refuse older artifacts without conversion.

## Transport, host and administration boundaries

The transport follow-up completes the remaining product failure boundaries. CLI `ProtocolFailure`
is a private value of a closed `ProtocolProblem` and known-member location; it carries no writable
message. Unknown JSON property names collapse to the known containing object or root. Oversized
NDJSON lines terminate the process with exit 2 and one `FRAME_TOO_LARGE` response immediately
after the first excess byte, without waiting for newline or EOF. Restart before sending another
frame. Complete malformed frames and wrong scalar kinds return frame-local typed refusals;
the refusal does not settle earlier operation uncertainty. CLI-v4 `protocolFailure` frames use
flat `diagnosticId`, `code`, and known-member `path`, separately from core outcome objects.
A local `CLI_INVALID_SCALAR` additionally carries exact `scalarDiagnostic.id` and `parameters`
from the shared scalar diagnostic owner. Missing, excess, unknown and nonlocal causes are refused.
This is transport admission, not a manufactured Domain rejection or proof about an earlier attempt.
`claimcore describe diagnostics` publishes the protocol and process schemas without opening a store.

HTTP readers return `HttpInputProblem`, not English sentences. The host policy binds each identity
to its code, HTTP status and execution phase; the serialized `status` must agree with actual HTTP
status. Framework method refusals also use this policy. Runtime and standalone browser validators
reject contradictions and old shapes. Security admission remains intentionally bounded and does
not expose credentials, unexpected property names or provider information.

The request-local failure boundary is installed before connection, authentication and rate-limit
middleware. It distinguishes pre-dispatch failure, unconfirmed dispatch, and failed delivery after
a returned result. Typed admission and input refusals declare `NOT_STARTED`, as does pre-dispatch
failure. Null phase after returned-result delivery failure or invalid export metadata does not prove rollback. Read-only
CLI endpoints exit 3 for host failure; mutations without `NOT_STARTED` exit 4. Matching host/client
artifacts are required: older null-phase admission bodies fail the current correlated schemas. Once a
response has started, the host aborts instead of appending another JSON document. A stopped host or
broken response is never proof that a command rolled back. Preserve exact authored operation identity for recovery.

CLI-v4 frames are decoded before authentication or HTTPS dispatch. Frame-local state records the exact operation identity, whether remote dispatch may have begun, and whether a validated response was flushed. It is reset before each frame. A failed write or flush makes one bounded stderr attempt, never a second stdout frame or implicit replay; a potentially state-changing frame with lost delivery exits 4 even if the service may have committed.

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
implicitly initializes storage; only explicit fresh initialization may create an absent ClaimCore
namespace. Existing unsupported schemas are refused untouched.

Administration stdout/stderr is structured JSON. Completed maintenance and failed output are
separate outcomes: output failure cannot claim the action failed. There is one bounded reporting
attempt, no automatic maintenance retry. Large terminal counts and byte totals are canonical
nonnegative Int64 strings, not lossy JSON numbers. Owner-only administration remains isolated from
case-work hosts, and a diagnostic does not grant permission to change stored state.

## Consumer compatibility and limits

Protocol and host failures use structured diagnostics; old string constructors, writable message records, CLI-v3 envelopes, and the retired Web-v2 route are not supported. Rebuild browser, CLI, and service from matching generated contracts. Native maintenance callers must handle every `AdministrationOutcome` case. These diagnostics describe known categories without exposing claimant payloads, tokens, private paths, or provider exceptions; they do not certify recovery, authorization, backup freshness, or erasure completion.

The browser loads its strict host, discovery, core, and recovery validator groups on demand. Generated schema and validator checks, size limits, and complete tests are owned by [Development](development.md). The fresh database boundary is non-destructive refusal of old storage, not an automatic migration or reset.
