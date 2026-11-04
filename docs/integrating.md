# Integrating

Use matching service, browser and CLI builds. [OpenAPI](../contracts/openapi.json) defines request and
response shapes, endpoint effects and host statuses. The [generated CLI reference](cli.md) owns verbs,
flags and choices. [Case work](case-work.md) explains the business record; the [glossary](glossary.md)
defines terms.

Case snapshots, receipts and list rows expose a stable `caseId`: the UUID of the accepted OPEN operation.
References remain immutable and ASCII-case-insensitively unique, including after erasure. The original
reference is retained in the OPEN ledger metadata independently of erasable request and snapshot bytes.
Case request, snapshot and ledger entry records use format 4. Work/reminder records use format 1;
authority and control records use format 2. Installation initialization requires the declared SQL baseline;
schema conversion and compatibility readers are not supported.

Case reads/history and case operations use a tagged `target`: either
`{"kind":"REFERENCE","caseReference":"Demo-0001"}` or
`{"kind":"ID","caseId":"10000000-0000-4000-8000-000000000001"}`. UUID-shaped references stay
references when tagged REFERENCE. OPEN supplies a new reference. Existing-case selectors resolve in the
same read snapshot or locked write used for authorization and execution. Accepted-operation identity
uses the resolved CaseId; changing selector form cannot redirect an existing operation.

Case-scoped authority inputs use `scope: {"kind":"CASE","target": SELECTOR}`. Published grant scopes
and retained authority records carry both CaseId and immutable creation reference. CLI/browser lookup
forms accept references; case links use CaseIds. Dynamic `call` and HTTP inputs accept either selector.

Disclosed case fields include the core-supplied `progress` value. Clients render it directly, along with
core review changes and correction choices. CLI reviews consume the supplied changes and lifecycle
states; authority reviews render the supplied before/after states.

## Discover inputs

`claimcore describe case open` describes one operation offline. `describe --format json` exposes catalog
entries; `schema ENDPOINT request|response` exposes a wire schema. The generated
[catalog](../contracts/catalog.json) describes operation meaning and input bindings without CLI synopsis
text. CLI grammar derives command words, flags and usage separately.

Catalog `CHOICE`, `CHOICES` and `GROUP` input kinds supply `vocabulary` and `options`; use the vocabulary
name to render option labels. `CHOICES` permits a collection of selected tokens, as used by worklist facets. Each constant supplies nullable `vocabulary`, and each input supplies nullable
`adviceGroup`. A non-null `adviceGroup` identifies the registration, decision or payment correction group
whose current advice constrains the input's choices. These identities come from the catalog, not member
path spellings.

Keep the selected entry's `subject` and `kind` as operation identity. Assemble its applicable constants
and inputs using their declared `path` and `branches`; form values do not select the operation. Constants
remain part of the submitted wire request. The browser follows this model; custom clients can use the
[catalog model](../src/ClaimCore.Protocol/Catalog/Metadata.fs) and generated contracts as references.

Dates and amounts travel as strings. Wire clients keep authored names, reasons, amount spelling and references intact;
use the supplied input descriptors and current server guidance when preparing changes. A preview is advisory.

Case advice contains act kinds, optional `correction` choices exactly when `CORRECT` is offered, and
history modes. Offers contain no `expectedRevision`; use the accompanying snapshot revision, or revision
zero when opening. Treat advice as presentation: execution checks current state and authority again.

Core vocabulary labels use qualified keys such as `status.OPENED.label`; generated browser terms require
the vocabulary name. Scalar guidance comes from core messages and Domain-owned bounds. Country format
metadata publishes `unknownCode`; consumers use that value rather than choosing their own unknown sentinel.
CLI human output uses translated labels; machine frames retain exact wire tokens.

Each catalog role definition has a required `meaningId` for its core-owned grant explanation. Render
that message directly; role names are not a client-side permission engine. The meaning identifies
opening and human-only work restrictions, while the actual scope and principal-kind metadata still
controls the offered selection. Execution independently checks the current facts.

The catalog's `roles` declares `admittedPrincipalKinds` and `admittedScopes` from Domain admission policy.
Its `capabilities` separately declares principal-kind and scope applicability. OWNER admits installation
scope only; a role is meaningful at a scope when at least one of its capabilities applies there.
Use this metadata for grant choices and require an explicit role. Recognized grants remain revocable
even when their role/scope combination is not admissible for a grant. Disabled targets and overlapping
meaningful grants remain admissible. Execution independently checks current authority and action state.

Authority previews include `accessScope` and before/after `access` explanations. Each capability identifies
its direct and installation-derived role sources and whether it is currently effective. Disabled actors
retain dormant sources; removing an overlapping grant need not remove inherited access. These explanations
cover the supplied resource snapshot and do not predict whether a later business act will pass its rules.

Field and technical-target labels use `field.*`; group labels use `group.*`. Scope, actor-ID and
history-detail labels use `input.scope.*`, `input.actorId.*` and `input.detail.*`; worklist facet/order
labels also use their catalog-supplied `input.*` keys. Always consume the declared label identity rather
than guessing it from a wire path. Principal input
identities are `principal.kind`, `principal.issuer` and `principal.subject`; their wire paths remain
`target.kind`, `target.issuer` and `target.subject`. CLI grammar owns the flag spellings.

Protected Application entry points check the current caller before returning early business input
refusals. Unknown or disabled callers receive `RESOURCE_UNAVAILABLE`, including for malformed business
values. The error belongs to one authorized read or write transaction; rejected writes allocate no
payload nonce or ledger entry. Malformed calls can therefore require available storage. Valid request
paths retain their target/capability checks, and registered roleless callers retain their own delivery
recovery. Offline CLI syntax and malformed-wire transport checks are separate boundaries.

## HTTPS contract

Session bootstrap uses GET; other endpoints use POST `/api/<endpoint>`, retaining literal dots in the name.
Send the input itself as `application/json`, with the matching `ClaimCore-Contract` identity. Cookie POSTs
also require antiforgery; bearer authentication is separate. Follow the
[hosting configuration](operating.md#identity-and-hosting) for issuer, audience and client restrictions.

The [contract limits](../src/ClaimCore.Protocol/Api/Limits.fs) and generated byte profile own bounds.
Clients admit complete replies through their schema or codec before using them. Tagged objects are
closed; malformed UTF-8, duplicate decoded members, unsafe numbers and excessive depth or bytes refuse.
Business-value guidance is a typed rejection with HTTP 200; structural failures use host responses.
Servers return codes and parameters for local rendering. Input refusals use a nonempty issues array,
for example `{ "code": "INVALID_INPUT", "action": "CORRECT_INPUT", "issues": [{ "field": "claimantName", "code": "TOO_LONG", "maximumCharacters": 200 }] }`.
The array is bounded by the largest declared act input count. Issues follow catalog input order, and one
input may have several distinct issues. Independent checks accumulate within each phase. Cross-field
checks require parsed operands; current-state and business-date admission require a parsed request and
the authoritative state. A non-input refusal reports one state or transition cause. Render every issue
rather than retrying one field at a time. Other diagnostics/faults retain their flat code-tagged shape;
`actualRevision` appears only for revision conflicts. Their `changes` are either WITHHELD or a PAGE of at most 50 typed timeline entries. A returned continuation is used with `case.timeline` for the same target and no filter; it retains the observed revision range and rechecks current history disclosure. Unknown role/principal-kind tokens are structural
protocol faults and cannot enter Domain as invalid enum values.

The generated diagnostic registry maps unique codes directly to message keys and parameter contracts;
consumers do not infer a family by trying prefixes. Authored principal fields are distinct from admitted
principal outputs. Stored identity-token decoding belongs to storage adapters; corruption is an integrity
fault, never a request-correction refusal.

HTTP replies carry `outcome`; preview, execution, observe, status and resolve echo `operationId`. Receipts contain shared
acceptance metadata and a tagged case or authority `subject`. HTTP does not use the CLI frame wrapper.
A host response has `{ "hostFailure": { "code": ..., "phase": ... } }`; its status and phase are defined by
[host failure policy](../src/ClaimCore.Protocol/Wire/HostFailures.fs).

Contract identity hashes canonical OpenAPI. Locale/help-only edits preserve it. Tracked
[client validators](../contracts/client.ts) and [message contracts](../contracts/messages.ts) accompany the
catalog and OpenAPI. See [generation](contributing.md#generated-contracts) when maintaining a consumer.

The command-line tools share version/help codecs and one typed usage-error vocabulary. Each executable
owns its supported exit-code subset. Database `USAGE_ERROR.issue` is a structured issue with applicable
syntax in `usage`; scripts must read the issue kind and its declared fields. HELP and VERSION use kind tags.
The [exit-code reference](cli.md#exit-codes) lists the numeric codes supported by each executable.
Execute `OUTCOME_UNKNOWN` is a distinct outcome with exit 4; preserve its exact retry frame and use passive
status or deliberate settlement as described in [delivery recovery](#delivery-recovery).

## CLI use

Keep the complete CLI publish directory together and run its apphost, or add that directory to `PATH`.
Offline `help`, `help configuration`, `help call` and `help delivery` need no credentials. For example:

```sh
claimcore case open DEMO-0001 --incident-date 2026-09-01 --incident-notification-date 2026-09-02 \
  --incident-country LV --claimant-name 'Synthetic claimant' --insurer-name 'Synthetic insurer' \
  --claimed-amount 100.00 --claimed-currency EUR --dry-run
claimcore describe case open
claimcore authority list
```

Case and authority mutation verbs generate an ID unless `--operation-id` is supplied. When stdin and
stdout are terminals, a text-format existing-case mutation without `--preview` or `--dry-run` can omit
the expected revision. The CLI fetches the case, previews that revision with the prepared operation ID,
shows the localized review and asks for confirmation with a default of No. Confirmation executes the
same ID and revision. Declining does not execute. A concurrent change still causes the normal revision
refusal; the CLI does not rebase the request. Omitted correction groups default to KEEP in this path and
the review shows their actions.

Explicit `--preview` in a terminal can fetch an omitted revision, prints review and stops without prompting
or executing. `--dry-run` remains offline and requires the revision for an existing-case change. A declined
or empty confirmation does not execute.

Automation, redirected input/output and `--format json` require `--expected-revision` for existing-case
changes. Interactive review has no skip-confirmation flag. `--preview` requests review, and `--dry-run`
prints the prepared frame without authentication. Verbs default to text; `--format json` selects a
structured reply and `--language` selects interface language. Authored business values remain unchanged.

`call` reads one `{ "endpoint": ..., "input": ... }` document. `session` reads NDJSON frames and reuses
process-local authentication. Reply frames contain nullable `endpoint` and `operationId`, a `delivery` tag,
and `result`. Delivery tags describe transport progress; inspect the typed result to determine acceptance.

Save public connection settings with `claimcore configure`; `claimcore help configure` lists its flags.
The Unix profile is `$XDG_CONFIG_HOME/claimcore/profile.json`, defaulting to
`~/.config/claimcore/profile.json`. Its string members are `serviceUrl`, `issuer`, `clientId`, `authMode`
and optional `serviceTrustRootFile` and `oidcTrustRootFile`. The writer uses atomic replacement, 0600
files and a 0700 profile directory. Reads use [private-file admission](operating.md#private-configuration)
and refuse malformed documents and unknown members, including secret or token members. Environment
variables override profile values individually; `CLAIMCORE_OIDC_CLIENT_SECRET_FILE` remains
environment-only. `claimcore configure --show` prints effective values and their sources without opening
the secret file. Changing the profile takes effect in the next process.

`claimcore shell` requires interactive authentication mode and terminal stdin. Existing verbs, `help`
and `describe` run in one process and share one sign-in. Quotes and backslashes group arguments;
metacharacters stay literal, with no expansion or command execution. Arrow-key history, access tokens
and refresh tokens remain in memory. After 15 idle minutes at a command or confirmation prompt, all
tokens are discarded; the next authenticated action signs in again. Refresh failure also starts browser
sign-in again. `exit`, EOF and SIGTERM discard tokens. Ctrl-C cancels the current command and returns
to the prompt; a second Ctrl-C at an empty prompt exits. Outcomes use stdout; prompts and each command's
exit code use stderr. Exit 4 prints the frozen retry frame and recovery guidance, then the shell continues.
Interactive review requires terminal stdout. `call` returns one result; NDJSON `session` stops on
uncertain delivery or cancellation.

Interactive login uses PKCE and a one-use loopback callback. Automation uses a registered service actor,
client credentials and its private secret file. Optional explicit service/IdP trust-root files replace
system roots while retaining hostname and validity checks. The
[walkthrough](getting-started.md#optional-cli-setup) shows local setup.

## Authority history

`authority.history` accepts `limit` (1–50), optional `cursor` and optional `actorId` selecting the
target actor. It requires installation `ReadAuthority` within the same snapshot used for the page.
Entries are newest first by ledger sequence, with `sequence`, `recordedAt`, `recordedBy`,
`targetActorId` and a tagged `act`. REGISTER and PROVISION_OWNER carry `principal`; GRANT and REVOKE
carry `role` and `scope`; ENABLE and DISABLE carry no additional facts. These are decoded from the
permanent authority payload and checked against its commitment and metadata.

The response has `entries` and nullable `nextCursor`. A cursor binds the target filter; restarting
with a different actor selection requires a fresh request. The CLI equivalent is
`claimcore authority history [--actor-id ID] [--limit N] [--cursor TOKEN] --format text|json|csv`.
CSV has canonical column headers and neutralizes formula-leading values before RFC 4180 quoting.
A continuation is printed separately on stderr; JSON carries it in the typed result.

## Worklist queries and summary rows

`case.list` uses one page request with optional facets and order. Omitted facets are unfiltered; omitted
order is `REFERENCE`. For example:

```json
{
  "limit": 50,
  "lifecycle": ["ACTIVE"],
  "status": ["OPENED"],
  "progress": ["UNDECIDED"],
  "text": "Rīgas",
  "order": "RECENT"
}
```

| Member | Meaning |
| --- | --- |
| `lifecycle` | Set of ACTIVE, VOIDED and ERASED. |
| `status` | Set of OPENED and CLOSED; erased cases have no status. |
| `progress` | Set of UNDECIDED, DECIDED and PAID; erased cases have no progress. |
| `text` | A 1–100-Unicode-scalar substring of reference, claimant or insurer. |
| `order` | REFERENCE, RECENT for last business-fact change first, NOTIFIED for oldest notification, or FOLLOW_UP for dated work first. |
| `view` | A closed core case view, intersected with all other filters. |
| `responsibleActor` | Exact responsible ActorId selection within the viewer's current case visibility. |
| `assignment` | MINE selects factual responsibility for this actor; UNASSIGNED selects no responsible actor. Both apply within current visibility. |
| `due` | OVERDUE, TODAY, UPCOMING, UNSCHEDULED or INACTIVE, evaluated at the returned database/business-date basis. |
| `cursor` | Opaque continuation bound to the admitted filters and order. |
| `limit` | Bounded page size from the catalog and contract. |

Search admission follows names' whitespace/control/format-character rules before NFKC normalization and
invariant lowercase. Stored searchable values use the same normalization, joined with U+001F. Percent,
underscore and backslash match literally. Search does not remove accents or use date/amount ranges.

REFERENCE orders by the ASCII-case-folded reference. RECENT orders by the latest accepted case ledger
entry's `recorded_at` descending, then folded reference ascending. NOTIFIED orders by notification date
ascending, then folded reference; erased rows with no date sort last. A continuation encodes those keyset
positions and its canonical filters/order. Use only continuations supported by the issuing endpoint.
After request admission and authorization, unsupported cursor formats or invalid endpoint/filter/resource
bindings are rejected with `INVALID_CURSOR`. Every page authorizes under current rights and uses one snapshot. Concurrent changes to ordering or filter
values can move, duplicate or omit rows during traversal. `--all` is a live traversal, not a pinned snapshot export.

Case summaries use root `caseId`, `caseReference`, `revision`, `changedAt`, `headline`, `attention`,
`stages`, `nextAction`, `responsible` and `pendingReminders`. ACTIVE/VOIDED `state.fields` is generated
from Domain declarations marked `Listed`; it is not a second hand-maintained business projection.
Complete money pairs are canonical amount/currency strings; both payable members are null when absent.
An ERASED state has no fields. Reusable `ActorRef` values expose current names or an explicit erased
name, principal kind and availability, without issuer, subject or contact details.

Pages include their authoritative `basis` and a separate nullable `exactReference` match under current
read rights. `view` intersects every explicit filter; `responsibleActor` selects another actor's factual
assignments within the caller's own visibility. Case cursor format 6 binds both selections. Changed
state or authority can still move rows during live traversal. Typed stages and recommendations come
from core policy; clients format them instead of reconstructing business state.

`case.summary` accepts the same filter members, without `cursor` or `limit`. Its `counts` contain
`total` and `lifecycle`, `status`, `progress`, `assignment` and `due` value/count arrays. `claimed` and `payable` contain
currency/amount totals. `DecimalTotal` is an unsigned canonical decimal string with at most 40 integer
and four fractional digits; sums never round. There is no currency conversion or self-excluding facet
count. Visibility, assignment and forbidden-role safeguards match `case.list`, within the summary's
own read snapshot. Separate list and summary requests can observe different accepted writes.
`case.get` reports `mine` from the responsible ActorId. `context.hasScopedCases` reports grant-based
visibility, independently of responsibility. A case-scoped read grant does not make a case Mine;
assignment does not grant access. `context.actorId` identifies the current requester.

Case work has an independent revision and optional `responsibleActor` and `handlingSummary` values.
The `WORK` operation family carries `UPDATE_WORK` and its expected work revision; OPEN establishes an
empty work root at revision zero. Omission preserves, explicit clear removes and supplied text sets a
value. Empty or unchanged work refuses. Summary edits work on active open/closed cases; responsibility
changes require active open state. Only current human ReadCase + ManageCaseWork rights permit writes.
Service CASE_HANDLER financial/read rights remain valid and do not include ManageCaseWork.

`case.assignees` is a bounded case-scoped read of eligible human ActorIds/subjects, without issuer
or grant-directory disclosure. Explicitly selected accounts are rechecked at execution. Later disable or
revocation retains factual responsibility and reports the account unavailable; assignment grants no
access. Every reminder inherits current responsibility. Case/work views distinguish work and financial
revisions and last-change attribution.

The `REMINDER` family supports CREATE, UPDATE, COMPLETE, CANCEL, REOPEN and COMPLETE_AND_PLAN_NEXT.
Each reminder has an immutable parent/ID, independent positive revision, action text, planned date and
PENDING/COMPLETED/CANCELLED state. Creation uses its operation UUID as ReminderId. The compound operation
completes exactly one current Pending reminder and creates exactly one replacement under that same
operation UUID; its correlated receipt contains the fixed pair. Stale revision or invalid replacement
changes neither. Terminal records must reopen explicitly with a supplied date before another edit.

Create/update/reopen and compound planning require active open parents. Complete/cancel can tidy Pending
reminders on active closed parents. Close/void preserves inactive reminders and their exact dates;
reopening/reinstatement reactivates them. Erasure clears all related erasable payloads and projections
while reserving identities. Work/reminder writes do not advance case revisions.

Next follow-up is the minimum Pending due date, with stable ReminderId ties. `followUpDate` is derived
and has no independent write operation. Dates may be past/future and use the query's database instant and installation zone.
Case filters classify that minimum as OVERDUE, TODAY, UPCOMING, UNSCHEDULED or INACTIVE. Reminder queue
filters classify individual actions, so a case classified Overdue can still have a Today reminder.
`FOLLOW_UP` orders case minima with absent dates last and stable reference ties. Live continuations bind
filters/order/scope, each rechecking current visibility; they do not promise a pinned export.

Bounded reminder pages default to actionable Pending work; terminal/inactive records require explicit
selection. Reminder get/list items include required nullable `timing`: `OVERDUE`, `TODAY` or `UPCOMING`
only for active Pending work, otherwise null. `active` describes the current parent; terminal state is
independent. Timing uses the enclosing read/page's server `basis`, not a consumer clock. Retain each
page's basis when accumulating rows. Date filters compare the retained due date even when explicit state/include-inactive selections inspect
terminal or inactive work. Such a record can match an OVERDUE date filter while its `timing` is null.
Generate strict consumers from the matching schema; `timing` is required even when null. Historical
receipts have no current timing classification.

The work overview ignores case page/search filters and reports accessible or Mine actionable
cases with observation/date/zone. Missing-plan, overdue, unassigned and unavailable dimensions overlap;
attentionCases counts their distinct union, including due-today and ready-to-close cases. Counts distinguish cases from reminders. Zero Pending on an
active open case is monitored attention, not a hard validity refusal. Date passage writes no ledger event.
Case financial aggregates count each selected case exactly once, regardless of reminder cardinality.

Incident description and Handling summary are independently optional LF plain text up to 600 Unicode
scalars, with separate 4096-byte permanent UTF-8 bounds. Reminder action is single-line, up to 200 scalars
and 1024 permanent bytes. Empty/whitespace-only assertions, CR, unsafe controls and directional formatting
refuse; ordinary Arabic/joiners remain supported. No trimming, Unicode normalization or markup evaluation
changes stored content. On OPEN omitted/null description is absent; partial omission preserves and
explicit null clears. Required registration null and half-money assertions remain invalid. File/editor
CRLF conversion happens before review/freezing, never after prepared request confirmation.

Typed case activity orders fact/work/reminder events by ledger sequence. Dedicated fact history
compares consecutive case revisions; current terminal reminder access is distinct from prior-version
ReadHistoryFull disclosure. Recovery correlates family, parent, entity IDs/revisions and both compound
effects. Native/IndexedDB journals remain identity-only and never reconstruct business text after restart.

OPEN and DECIDE retain complete inputs. AMEND_REGISTRATION takes typed optional facts, with
`claimed: {amount,currency}` as a complete money assertion. CORRECT uses registration KEEP/UPDATE,
decision KEEP/UPDATE/CLEAR and payment KEEP/REPLACE/REAFFIRM/CLEAR. A paid decision update needs explicit
payment intent and its capability. Reaffirmation requires an existing payment and retains its date;
it cannot create a payment. Omitted and unchanged accepted values retain their meaning; changed values
use current admission. Empty partial updates, null required facts, half money and unknown members
refuse. A correction cannot add missing groups or create a no-op revision. Revision conflicts require
fresh intent. Chronology diagnostics identify the asserted field and carry typed counterpart context.

Case-list continuations use format 6; actor-directory continuations use format 4; assignee, reminder
and activity continuations use format 1; other continuations use format 2. All bind InstallationId. History and assignee continuations bind the retained CaseId. Case-list
positions bind admitted filters and ordering. Unsupported formats are refused.

```sh
claimcore case list --lifecycle active --status opened --progress undecided --order recent
claimcore case list --lifecycle active --lifecycle voided --text 'Rīgas'
claimcore case list --format csv --all
```

Facet flags are repeatable and derive their choices from the catalog. CSV is UTF-8 without a BOM, uses
CRLF line endings and RFC 4180 quoting, and writes one header row of actual qualified wire field paths
(such as `state.claimed.amount` and `state.progress`). Null/unrecorded/erased values use empty cells.
CSV is a spreadsheet-safe export: a cell beginning with `=`, `+`, `-`, `@`, TAB or CR, or with one of
those triggers after leading spaces/TABs, receives an apostrophe before RFC 4180 quoting. This rule
applies to every column and alters affected cells. Use `--format json` when exact values are required;
JSON preserves exact authored values. Human text visibly escapes terminal controls, line separators,
directional formatting and literal backslashes; see [CLI text output](cli.md).
The header order is:

```text
caseReference,revision,state.kind,state.status,state.claimantName,state.insurerName,state.claimed.amount,state.claimed.currency,state.payable.amount,state.payable.currency,state.progress,state.incidentNotificationDate,state.work.responsible.actorId,state.work.responsible.displayName,state.work.followUpDate,state.work.due,state.work.basis.businessDate,state.work.basis.timeZone,changedAt
```

Work columns export the responsible ActorId, planned date, due token and that row's business-date/zone basis.
They remain empty for erased rows; current account subjects are available through authorized JSON/detail reads.
Amounts and timestamps use their canonical wire values; tokens and references are not localized. `--all` follows continuations to the end, emits the header once and stops on refusal, reporting the cursor
reached. Each page is its own snapshot; the complete export is not an installation-wide snapshot.

`context.maintenance` and case `advice.maintenance` contain the observed `active` flag and monotonic
interval `generation`. Active maintenance removes register-write offers while retaining authorized reads.
The server returns `REGISTER_MAINTENANCE` when an otherwise admissible new register/control write reaches
the actual paused action state. Retained accepted operations and committed nonacceptance observations do
not need another append. Advice never substitutes for the execution guard, and resumption never
implicitly retries a prepared client request.

## Profiles, admission and presentation metadata

REGISTER requires an authored profile; ADMIT adds the account, profile and 1–48 grants atomically.
Durable effects are separate REGISTER, SET_PROFILE and GRANT entries in one complete operation group;
ADMIT is its logical operation kind. Receipts and checkpoint proofs identify the final effect. Profile
erasure clears prior SET_PROFILE payloads and salts while retaining identity, grants and accepted
existence. Current names must not be recovered from IdP claims or stored name hashes.

Work review before/proposed values and disclosed work receipts use a read projection with current
responsible ActorRef and handling summary, keeping Domain work identity records separate. Before and
after can name different people on the same case without overwriting a relation. Authority receipt
`targetActor` is DISCLOSED with a current ActorRef under current authority-read rights, or WITHHELD
with the already-known ActorId. Losing authority does not remove previously permitted own-receipt
metadata or reveal another account's current name. No client directory scan supplies missing names.

`authority.list` accepts optional `text` matching current saved name or contact and optional typed
`role` matching recognized raw assignments. Text is a literal NFKC/invariant-lower search; both predicates
compose with paging. ACTORS v4 cursors bind normalized text and role under the unchanged 512-character
cursor bound. Profile erasure clears both private normalized search columns.

Actor-directory rows carry explicit assignment `state` and at most seven role summaries with an
installation flag and case-grant count. This assignment state is separate from authorization and
case-work eligibility. Optional `responsibleCases` carries a count plus an executable ALL/responsibleActor
selection; its absence is not zero. Related authority details may include principal/contact information
under their existing authority-read policy; a reusable ActorRef does not.

`operation.preview` now carries required `review.presentation` beside its typed `subject`. Its
`changes` entries supply key, label identity and typed before/after values; `unchanged` supplies the
remaining field identities for one summary. CASE subjects retain before/after/context and remove the
old raw-string `changes` member. Work, reminder and authority subjects retain their typed effects.
The server builds presentation once from core meaning; clients render the supplied values rather than
compare snapshots, infer unchanged groups or reconstruct profile names.

Presented money and totals retain exact values plus `displayScale`. Readers preserve the supplied
scale; formatters may pad to it but must never truncate meaningful fractional digits. Producers derive
the correct currency minimum and meaningful scale from Domain. Native, generated JavaScript and JSON
Schema validate the same structural contract; structural admission does not certify arbitrary server
business assertions or recompute them in clients. Typed case changes have one Application owner, which
also derives the canonical history differences; the protocol does not run a competing case comparison.

Current `CaseWorkView` separates aggregate `changedAt`/`recordedBy` from required nullable
`summaryChangedAt`/`summaryRecordedBy`. Summary metadata follows the last actual summary text change,
not a responsibility-only update or an unchanged text reaffirmation. Clearing the summary or erasing the
case clears both fields. Actor references reflect current profile disclosure, including profile erasure;
ledger verification and projection rebuild derive summary attribution from the original text edit.

Core presentation fields and inputs publish `labelId`, `hintId` and `ruleId`. `ruleArguments` supplies
Domain-owned bounds for precise rules. GUI diagnostics use `diagnosticGuiMessageKeys`, with the same
validated arguments as canonical diagnostics. Catalog entries retain their act/query `meaningId`,
provide `consequence` and `consequenceId`, and use `doneId` for past-tense outcomes. No old meaning or
checkbox-confirmation aliases are published for fields/inputs.

ACTIVE context adds bounded own `roles` from the same raw-assignment summary owner as People;
capability labels must not be inverted to invent role assignments. Limited requester states remain limited.
`AuthorityReview.basis` and authority-list/grant/history/access-request page bases come from their own
authorized transaction, including when no rows are returned.

`RuntimeContext` retains flat business date/zone fields and adds the authoritative `observedAt`.
Receipt `basis` describes the current authorized observation; its `recordedAt` remains the historical
accepted-event timestamp, including on replay and later observation. Never substitute a client clock.

`case.get` advice includes `blocked`: one typed core availability refusal for every act absent from
`acts`. Native admission, JSON Schema and generated JavaScript enforce that closed partition and the
bounded availability-reason subset. Requested-action callouts use that reason and the offered
recommendation. The core rechecks permission, state and version when executing; advice is not authority.

## Browser routes and presentation

The browser exposes Cases (`/cases`), Reminders (`/reminders`), People & access (`/people`), Authority
history (`/authority-history`), Recovery (`/recovery`) and Settings (`/settings`) according to the current
core context. Canonical case and exact-reference links remain `/cases/id/CASE-ID` and
`/cases/reference/REFERENCE`; editors append `/acts/ACT`. `/cases/new` opens the case drawer within the
worklist. `/people/ACTOR-ID` opens the person panel. `/recovery/OPERATION-ID` opens recovery for one change.
Old `/work`, `/authority` and `/operations` routes return the localized missing page. Settings is the sole preference
editor, also available as a dialog in the sign-in footer.

Worklist query parameters carry selected view, filters, search and order; page cursors are not stored in
the URL. Back/forward, reload and sign-in return preserve the same-origin path. Returning from case
inspection restores the live work query, scroll position and invoking control when available; a missing
row falls back to the area heading. Navigation state stores no business payload.

The Reminders area lists accessible Pending reminders by due date and provides explicit state, assignment,
text and inactive-work filters. Each loaded row retains its own response's timing and business-date
basis. Loading another page does not reclassify previously loaded rows against the newer page's basis.
Refresh starts a new observation.

Browser read admission permits two concurrent responses per document. Cancellation prevents queued
reads from dispatching; a dispatched obsolete read drains under its own bounded deadline and its result
is discarded. Caller deadlines include queue time. This does not coordinate tabs or replace the server's
[per-process admission policy](operating.md#request-admission).

`/authority-history` shows authority history; its actor filter selects the target whose changes are reviewed. Unknown routes keep their URL and render a localized not-found page without an API
request. Browser history uses the installation zone and selected locale, retaining exact UTC and IDs in
entry details. Links to authority details appear only for authority readers.

Use the client's link builder for exact references. Ordinary reference segments use `encodeURIComponent`:
`A/B` becomes `/cases/reference/A%2FB`. Exact `.` and `..`, and references beginning with `~`, use a
`~` prefix followed by unpadded base64url of the UTF-8 reference: `/cases/reference/~Lg`,
`/cases/reference/~Li4` and `/cases/reference/~fg`. The reference `new` uses `/cases/reference/new`.
This avoids dot-segment normalization and prefix collisions. A UUID-shaped reference stays a reference;
only the explicit `/cases/id/` route selects by CaseId.

The actor directory uses a labelled table on desktop and compact cards on mobile, with kind, subject,
enabled state and a count from the selected actor's fully loaded grant pages. Partial pages, reloads
and refused disclosures withhold the count; closing the detail discards it. Each offered row link
opens the actor detail; listing does not fetch individual grant pages.

Authority actor links bind offered actions to the disclosed actor. A retained request keeps its original
principal and operation identity until the user explicitly discards it to begin the selected actor's
action. Preview and exact-request confirmation still precede execution.

Language, display locale and SYSTEM/LIGHT/DARK theme are independent preferences. Preferences use stored
format version 2. Unsupported versions or invalid record shapes use defaults; unsupported preference values
fall back individually. Locale date/amount controls prepare canonical wire values for preview;
the server remains responsible for admission. Unambiguous locale amounts normalize before preview;
ambiguous text reaches server admission as authored. Country/currency suggestions allow free entry under
the wire grammar. The review formats money and dates for display while preserving exact authored and admitted values for execution.

## Delivery recovery

Keep the exact prepared frame and its original ID until a submitted change is settled. Resend it unchanged
with `claimcore call`. `ACCEPTED` or `ALREADY_ACCEPTED` in the reply to the resent frame confirms acceptance.
A refusal, fault or `NOT_STARTED` response cannot disprove a prior uncertain commit. `OUTCOME_UNKNOWN`
means commit confirmation was lost after commit could have started.

`operation.observe` is a read-only audit lookup under current resource rights, including authority receipt
lookup for auditors. Its absence proves nothing about an uncertain submission, and metadata alone cannot
prove that submitted content matched it.

`operation.status` is a separate actor-scoped passive read. Every enabled valid registered actor, including
one with no remaining business or authority roles, can recover its own accepted metadata, known final
`NOT_ACCEPTED`, or `UNRESOLVED`. Current rights still govern case-content disclosure. A foreign actor's same
raw operation UUID is not disclosed as this actor's acceptance. Status appends nothing, creates no fence,
and takes no ledger-head write lock. An unresolved status never establishes nonacceptance or business eligibility.

`operation.resolve` is the deliberate ordered settlement write: it returns the author's accepted receipt
with current disclosure or records permanent actor/ID nonacceptance. The CLI names this command
`operation settle OPERATION_ID`. Final settlement blocks
delayed acceptance by that actor; foreign acceptance is neither revealed nor changed. After a lost reply,
inspect status before deciding whether to settle again. After final nonacceptance, load current state and
prepare a new operation with a new ID. Human interactive settlement identifies the exact operation and
requires explicit confirmation; status checking, navigation and login never settle automatically.

Status, execution and final settlement validate the retained control node's identity, payload and hash,
and agreement with the fence projection's recorded instant, using bounded indexed lookup in the action
snapshot. Missing, fabricated, mismatched or contradictory projections fail integrity; they are not repaired
by status or settlement. This check does not replace whole-history verification.

For ordinary verbs, call and session, uncertain exit 4 leaves the exact prepared call frame on the last stderr line; retain it privately. In the shell it appears before the next prompt.
Session processing stops on exit 4 or 130, or an oversized frame. Output loss after dispatch preserves
uncertainty. A complete OS write and flush do not prove downstream persistence.

The generated [exit-code reference](cli.md#exit-codes) covers both executables, including usage and cancellation.

Authenticated context includes `installationId`. Execute, passive status and final resolve requests carry its canonical UUID in
the required `ClaimCore-Installation` header; a mismatch returns `INSTALLATION_MISMATCH / NOT_STARTED`
before an acceptance or fence. All operation replies echo their operation ID; clients also check receipt
and target identities after decoding. A schema-valid mismatched reply remains uncertain.

Both clients commit an identity-only journal before mutation dispatch and recheck credential, actor, target and
installation context after storage completion. Failed initial storage is `NOT_SENT`; it blocks sending.
Records contain format, service origin, installation, actor, local attempt, operation, subject and act.
They contain no references, business values, credentials, secret paths or retry bodies. Separate attempts
can share an operation ID; clearing one never clears another attempt.

The browser uses IndexedDB transaction completion. A requester-context refresh with the same identity
keeps an in-flight recovery check valid. Actor, installation, credential or session-epoch transitions
invalidate its callbacks, including switchbacks; obsolete completions cannot unlock a newer check.
Busy state lasts through journal removal, and a failed removal permits explicit retry.

Send again uses the in-memory frozen request; after reload or tab closure, recovery can only check the
known status or deliberately settle, without reconstructing a request. Storage denial, quota, clearing, eviction,
device loss and universal crash/power-loss durability are outside the retained-storage guarantee. There
is no automatic storage-permission prompt or reconstructed resubmission.

The native client publishes private per-attempt files under `$XDG_STATE_HOME/claimcore`, defaulting to
`~/.local/state/claimcore`, with 0700 directories and 0600 files. `claimcore recovery list` lists only the
current authenticated service/installation/actor scope. `claimcore recovery check ATTEMPT_ID` reads status;
`claimcore recovery settle ATTEMPT_ID` explicitly requests permanent settlement. Text is the default;
`--format json` preserves machine output, and `--language en|lv|ar` selects text. Interactive mode requires
terminal confirmation for settlement; automation must explicitly choose `settle`. Only complete
correlated accepted/final results can clear the selected owned record. Unresolved/error/stale replies and storage failures retain identity; a cleanup failure does not
hide the known server result. `claimcore schema recovery` describes the identity-only listing. Prepared
retry frames retain their
installation binding. Clients refuse a prepared request whose installation binding differs from
authenticated context.

## Disclosure and cursors

Current rights govern every disclosure. Current snapshots need case read; superseded snapshots need full
history. Receipts distinguish `DISCLOSED`, `WITHHELD` and `ERASED`. Access/session changes clear disclosed
views, and stale replies cannot populate a different actor or resource context.

`case.history` returns newest revisions first. Start without a cursor, then follow `nextCursor` for older
revisions. Each continuation selects revisions older than its last entry. The browser loads
bounded pages. Core supplies each full entry's nullable `difference`, including lifecycle and field
changes, using the predecessor already fetched in the same LIMIT+1 query. That context row is not an
extra visible entry. Clients do no business comparison. Withheld or erased fields are never inferred;
a missing comparison remains unavailable. This does not pin later pages or fetch the whole history.

Treat continuation strings as opaque positions for their issuing endpoint and resource. Follow non-null
`nextCursor`, even after a short page. Each page authorizes again before decoding a cursor and uses its own
snapshot. Actor summaries are not grant inventories; inspect the selected actor's grant pages separately.
