# Integrating

Regenerate HUMAN clients when changing builds. Use supplied region empty messages, row recommendations,
view and column prominence, and typed state details. The generated client also provides review modes,
collection labels and the presentation tone table. Creation reviews supply their missing-field labels.

Use the human CLI for interactive work, structured calls for automation, or HTTPS for your own client.
The [OpenAPI contract](../contracts/openapi.json) specifies exact requests, replies and byte bounds.
The [catalog](../contracts/catalog.json) supplies meanings and choices. `claimcore help` derives the
current verbs and flags from task declarations; use these sources instead of copying field lists.

Use matching server, browser and CLI builds. Regenerate integrations when the contract identity changes.
`claimcore version --json` includes `contractIdentity`, the lowercase SHA-256 identity used in
the CLI's HTTPS requests. A typed HTTP 409 `CONTRACT_MISMATCH` can include
`ClaimCore-Host-Contract` with the responding host's identity, including before account admission.
The CLI reports both identities in human diagnostics when it admits that header.
The generated [TypeScript client](../contracts/client.ts) provides types, endpoint/task metadata and
transport constants and structural response guards derived from Wire schemas. Invoke them after strict
byte admission; interpretation, ownership and operation correlation remain consumer checks. They preserve
strings and check declared Gregorian dates, scalar/UTF-8 bounds and complete pattern consumption. The
generator refuses unsupported scalar formats and regex syntax rather than emitting an unchecked browser
pattern. Its supported syntax is deliberately finite; it does not translate arbitrary native regexes.
The browser checks response structure before observing dates or starting recovery. Its JSON parser
does not preserve duplicate-member and numeric-lexeme evidence. Timestamp strings retain their
precision. The native CLI decodes complete replies with its codecs.

## Authenticate

An administrator registers the exact principal: issuer, account kind and immutable subject/client ID.
A display name or email address grants no access. See [People and access](people-and-access.md).

For the CLI, start with `claimcore help configuration` and `claimcore configure`. Keep public connection
settings in the profile and supply credentials through the documented private-file settings. Use
`claimcore help shell` and the chosen command’s help for sign-in flows and failures.

A browser obtains its session through the configured identity provider. Cookie writes require the
antiforgery token; bearer clients follow their own authentication gates. Origin, session, installation
and contract checks remain server-owned. Hosting and credential admission are described in
[Operating](operating.md#identity-and-hosting).

For a direct HTTPS client:

1. Use the endpoint's declared method and representation header.
2. Send the current contract identity and installation binding where the endpoint requires them.
3. Complete the declared authentication and antiforgery requirements before dispatch.
4. Keep credentials out of URLs, entered business facts, logs and exported files.

Host failures are separate from Core outcomes. Their HTTP status and execution phase determine whether
an operation could have started. Do not infer delivery from a generic HTTP error or an interrupted
connection; use the recovery procedure below.

## Read

Choose a read by the question you need answered: a case, a work view, reminders, notes, attachments, payments,
people, access history or the status of an operation. Read the catalog and command help for available
filters and choices.

```sh
claimcore help case list
claimcore case list
claimcore case get DEMO-0001
claimcore people history
```

Every HUMAN read uses the shared `HumanReadReply` and `HumanDocument` grammar. Operation outcomes
embed that same document. Its closed subject identifies the resource; screen regions, actions, links,
continuation, observation basis, disclosure and history describe the current presentation. DATA replies
retain endpoint-specific automation types.

An empty initial `case.list` DATA page sets `authorizedScopeEmpty` to true when there is no
authorized exact-reference result and no case exists in the requester's readable scope with display
filters removed. HUMAN supplies the corresponding empty-state message. Derive clear-filter controls
from the active selection.

An `Affordance` carries `task`, `labelId`, `binding`, scalar-or-null `seeds`, `editable`, contextual
`choices` and `blocked`. The task-discriminated union associates each operation with its binding
shape and its declared editable input IDs. A particular control may offer a subset. Read task kind,
endpoint, consequence, creation and input metadata from `taskDefinitions`.
Operation bindings, creation basis, revision bounds, fixed-seed policy, correction-field activation and
scope-selection defaults are supplied by shared declarations. Native acquisition uses the pure
`SubmissionAssembly` facade; browser acquisition interprets generated `taskDefinitions.binding`,
`defaults`, input policies and `submissionSelection`. Explicit authored atoms, including null, false,
zero and empty text, retain their presence. UPDATE seeds stay display hints until explicitly authored;
noneditable fixed inputs remain immutable. Missing scope may acquire a default; contradictory selectors
or explicit installation scope with a case selector refuse instead of widening the grant.
Operation controls preview and execute. A command control posts the complete validated request from
its `COMMAND` binding to the declared endpoint, with empty seeds, editable fields and choices.
Collection rows start from the declaration's minimum rows and defaults. Recommendations live in
`RECOMMENDATION` screen regions.
A `CONTEXT` region associates form context with its supplied actions; show it for the selected task,
rather than presenting it as another set of current facts. Other section and row actions retain their
supplied bindings.

Input `choicesFrom` names a Core lookup. In the HUMAN results table, each
selectable row carries `choice: { value, label }`. The `label` is a core `PresentedMessage`
with named arguments. Keep a submitted value when it is missing from the loaded choices and show
`label.choiceNotListed`. An operation that requires an entry declares `selection: { promptId, from }`.
Use the prompt and the case-scoped list named by `from`, and offer rows that carry the operation. `choicesWhen` is an array of
`{input, value, choices, messageId}` conditions; intersect every matching condition with the offered
choices. The optional Core message ID explains a restriction. Choice hints do not replace server
admission. Removing a recognized stored role remains possible even if assigning it would now be refused.

A `Link` carries `endpoint` and an object `input`. Follow its supplied input through that endpoint's
typed request boundary. Generic HUMAN arrays use shared structural bounds; response byte bounds still
apply. Bindings share schemas by target, creation and reviewed-family shape; submissions keep typed
per-task inputs, with empty inputs referencing `Empty`.
An operation lookup link with empty input opens the ID-entry form; actual `operation.get` calls require `operationId`.
Choose the representation your consumer needs; neither grants additional permissions.

Check the document's subject and observation basis before using resource-specific fields; the generic
HUMAN grammar does not make an endpoint-specific subject or a snapshot implicit. DATA case timelines
identify each effect by `subject.{partKind,partId,revision}` and `task`. A payment change can produce
both case and payment entries. People history carries `task`, one compound person change description,
`privateAvailable` and references; it does not invent separate profile, role and enablement positions.

Keep the observation basis with the data you display. It identifies the server observation and the
business date/time zone. Changing a display locale does not change a recorded date or amount.

Follow supplied continuation cursors for the same selection and installation. Do not interpret or edit
cursor contents. CLI `--all` traverses supported page commands while preserving their filters; help
shows where that option is available.

Current access governs each read, including receipts and history. Discard cached private data when a
new observation tightens disclosure or changes the account/installation. A previously visible action
or document does not authorize a later write.

### Case reports

The [OpenAPI contract](../contracts/openapi.json) defines the `case.summary` selection and page
fields. Each DATA page returns the observation, whole-selection counts, report rows and a
continuation. Grouped pages contain one row per group and currency, with a blank-currency row for
groups without money. Clear grouping to page through the overall currency breakdown. Each page
reflects current read access and facts. Same-day changes can move results between pages. A
continuation binds the requester, installation, selection and business date. Restart after a
change to any of those inputs.

Use `claimcore case summary --format=csv` for the current report page. Add `--all` to stream
its continuation pages with one header. HTTP offers a bounded complete download.

### Reminder query selections

`reminder.list` distinguishes an omitted state from explicit `state: "ANY"`. With no standard view,
omission retains the Pending default; `ANY` adds no state constraint and includes Pending,
Completed and Cancelled rows permitted by the remaining selection. A standard view retains its own
predicates, so an explicit state filter intersects rather than replaces the view.

Omitted `includeInactive` defaults to false without a view and true with a view. Explicit false and
true always remain explicit, including in Core links, URL serialization, page requests and CSV
export selections. The browser renders effective defaults from generated owner metadata rather than
reimplementing those rules. Case reminder links supply explicit `ANY` and `includeInactive: true`.
The query uses `reminderStateSelection`. Each recorded reminder is Pending, Completed or Cancelled.

## Submit a change

Start with the task's help and the current target. Use the offered versions and identities, and author
only the fields that the task declares. Send `{operationId, task, binding, inputs}` with verbatim input
tokens. Omit Update inputs to keep values; use `null` only where `clearable` permits it. Existing-person
changes select an actor ID and require `binding.expectedRevision`. Acquire the positive account
`revision` from the DATA `people.grants` reply (or use the supplied HUMAN action binding). Creation
of a case or person uses an explicit zero revision and no new-target selector. For `case.open`, omit
`field:caseReference` to request assignment at acceptance. Only `CUSTOM_ALLOWED` installations
permit a caller to supply it. The raw scalar schema allows null so the server can identify the
invalid field. Admission rejects null, an empty string and malformed input. The same rules apply to
people and service accounts. Creating a reminder or note selects its parent case, without a
child identity or child revision. Binding schemas distinguish case, reviewed-family case, payment
selection, work, reminder, note and person shapes. A nonledger command control supplies its typed
request directly.
CLI automation supplies reviewed version flags where the task declares them; interactive commands can
acquire and freeze those preconditions for review.

Case erasure additionally requires `binding.expectedFamilySequence`, acquired from DATA `case.get`'s
`familySequence` or the supplied HUMAN erase action. CLI automation supplies
`--expected-family-sequence`. Keep the case selection, installation, root version and family sequence
from the same review. `ACTOR_CHANGED` and `FAMILY_CHANGED` require explicit fresh review, preserving
authored input; never refresh and execute automatically. Exact accepted retries retain their original
binding, including these preconditions. The exact operation request is specified by OpenAPI; use
`claimcore schema operation.preview request` or `claimcore schema operation.execute request` to inspect it.

The DATA opening preview identifies an `OPENING` target and supplies typed `opening` changes.
There is no Case ID or case link to use during preview. A custom request can appear as
`requestedReference`; reservation is checked only when the operation is submitted. Preserve the
same inputs and operation ID from review to submission. The accepted receipt contains the issued
reference and Case ID. Use the ID to navigate to the current record. Access to the original opening
snapshot determines whether a replay or operation lookup returns the historical opening reference.

Generated references use `CC-XXXX-XXXX-XXXX-XXXX-DD`, with sixteen Crockford Base32 payload symbols
and two decimal check digits. Exact selectors require the hyphens and validate the check digits.
ASCII letter case is ignored for exact reference identity. Partial search accepts fragments.
Custom values use 1–32 ASCII letters, digits and single hyphens between groups, starting with a
letter. The whole `CC-` prefix is reserved. For CLI callers, replace the former positional opening
reference with `--case-reference REF` when using custom assignment.

Typed offline checks apply the same ranges and applicability rules to supplied preconditions.
Draft syntax may omit metadata for later explicit acquisition; complete erasure input requires its
family basis. Offline syntax does not establish current state, authorization or acceptance.

```sh
claimcore help case record-payment
claimcore help work update
claimcore help reminder complete-and-plan-next
claimcore help people grant
```

Review before committing. A preview checks the proposed change without appending it. A review is an
observation, not a reservation: execution checks access, versions, dates and eligibility again against
the state used for the write.

Use one operation ID for one intended change. When a request might have been sent, preserve its exact
bytes and identity. Resending those same bytes can confirm or replay the accepted operation; changing
its intent while keeping the identity is an idempotency conflict. A new intent requires a new identity.
The CLI allocates an omitted identity where supported.

Optional updates distinguish omission from an explicit clear. Use the task's declared controls and
help rather than treating blank text, null and absence as interchangeable. Collection issues identify
the input token and submitted row ordinal; correlate that ordinal with the immutable submitted rows.

Disclosed case receipts include nullable `disposition` from that accepted operation. DATA timeline
changes use `DISPOSITION` for the same typed void/reinstate reason. Later case updates do not replace
the reason recorded by each accepted operation. Current permissions and owner erasure control
disclosure of these values in both representations.

Successful payloads vary by endpoint. Rejections use the shared representation-specific rejection
shape, and failures use the common fault shape with a Core message ID. Input issues target declared
input tokens. Use structured codes and actions for automation; do not parse translated prose.

### Case and payment contracts

Case receipts expose `payments`, an ordered list of entry differences. `case.correctPayment` binds
the selected `paymentId` and one case revision, then returns both removed and recorded differences
in one accepted operation. DATA payment-page items expose `actions`, the list of task IDs available
for the selected entry. Removal and correction are independently available.

Use `receivedDate` for the date the office received the claim. Registration facts other than the
reference are optional. Claimed money is an optional pair. Party rows and edits use the supplied
collection and binding declarations. Payment entries carry direction, their own money,
evidence reference and payee ID. Reference queries resolve earlier spellings under current read access.

The case handling status is open or closed. Close takes a required outcome and an optional note.
Reopen takes a reason. Observation-time case summaries support business date filters, grouping and
signed net-paid totals with contributing-case counts. Reminder kinds and deadline completion outcomes
are available through list and export contracts. Use the published shape for stage values and
operator or actor attribution rather than inferring it from a display name.

### Case attachment transfers

Use `case.attachments` to obtain current metadata and typed transfer controls with their Core labels,
request inputs and expected filenames. The HUMAN list, attachment receipts and timeline expose an
`attachmentObservation` with the case ID and sequence. Use it to discard older list, receipt and
history views after a newer attachment change. A file always belongs to the selected case.

Review `attachment.add` with the normal preview endpoint. Its input includes the operation ID,
tagged case selector, filename candidate, decimal byte count and SHA-256. Present the returned
portable name and reattachment consequence before confirmation. A blocked suffix produces an
`ATTACHMENT_TYPE_REFUSED` violation on the attachment name at review and execution. Freeze the
selected file across review and delivery. The protected multipart endpoint `attachment.upload`
accepts the operation envelope as `manifest.json` in the `manifest` part, then the exact bytes as
`payload.bin` in the `file` part. The standard JSON execution path refuses attachment addition. Use standard review
and execution for `attachment.withdraw`. If delivery is uncertain, use `operation.get` with the
original operation ID.

Core declares the binary `attachment.download` and `attachment.preview` actions. Their protected
POST endpoints take a tagged case selector and attachment ID. Verify the installation, media type,
full byte count and SHA-256 in `Content-Digest` before giving the file to the caller. Downloads use
`application/octet-stream` and `Content-Disposition: attachment; filename*=UTF-8''<encoded>`.
Compare the decoded name with the expected canonical name from the authorized attachment list.
Eligible previews use `image/png`, `image/jpeg` or `application/pdf` according to the verified bytes.
The DATA field `previewCandidate` and the HUMAN Preview control reflect the media profile of the
verified bytes and canonical filename. Browser decoding can still fail. Keep Download available.
Use the returned attachment ID to distinguish same-name files. HUMAN row details provide the full
ID.
If an accessible file lacks a supported preview, the endpoint returns the typed
`ATTACHMENT_PREVIEW_UNAVAILABLE` refusal in JSON. The file remains available for download. Use the
generated OpenAPI contract for the complete request and response grammar.

A known oversize local file produces CLI usage exit 64 before review or upload. The
[usage schema](../src/ClaimCore.CommandLine/UsageError.fs) represents this with
`ATTACHMENT_FILE_TOO_LARGE`, an integer `maximumBytes` and a decimal-string `actualBytes`.
Server upload admission independently enforces the applicable byte and framing limits.

## Post a command

Post the request from a `COMMAND` binding directly to the endpoint named in its task definition.
This submission uses no operation ID or preview. For example, `people.requests.dismiss` accepts
a kind-aware principal with the issuer and subject from the request:

```json
{
  "input:principal.kind": "HUMAN",
  "input:principal.issuer": "https://issuer.example/",
  "input:principal.subject": "waiting-account"
}
```

Command requests admit only their declared semantic input keys. Render the returned HUMAN document
for command feedback. Do not repeat an uncertain operational write automatically.

### Browser-session termination transport

`session.terminateOwn` accepts `{}`. `people.terminateSessions` accepts the selected registered person
as `{"input:actorId":"UUID"}` and requires current ManageAccess. Both are authenticated operational
writes, with the existing origin/antiforgery/contract/body gates. They do not require an installation
header, but any supplied `ClaimCore-Installation` header must be one valid matching UUID. The same
optional binding rule applies to preview and other actor routes; absent optional preview bindings
remain valid. OpenAPI declares required versus optional installation headers.

DATA returns `SUCCEEDED` with `data.count` and `data.self`, or typed `REJECTED`, `FAILED` or
`OUTCOME_UNKNOWN`. HUMAN carries the supplied completion/uncertainty document; confirmed success also
has a required boolean `self` so clients clear the right current browser ownership. Unknown results
have no count or operation identity. They are not safe automatic retries relative to later sessions.

## Recover an uncertain change

Before sending a mutation, retain enough identity to confirm it after interruption. The built-in
clients maintain a local recovery journal without storing the request body or entered personal facts.
Recovery records belong to a specific service, installation and account. The CLI journal uses format 4
and stores the task token with operation/attempt identity and ownership scope. Journal admission
requires the supported format; incompatible records are refused without conversion.

If delivery is uncertain:

1. Keep the original operation ID and frozen request bytes.
2. Query `operation.get` or use the client's Unconfirmed changes view.
3. Follow the returned status and expiry information.
4. Resend only the exact original request when the status permits it.
5. Start a changed intent only after resolving the original outcome.

An accepted operation remains accepted even if its receipt is later partly or wholly withheld by
current access or erasure. A final `NOT_ACCEPTED` is conclusive; an earlier `PENDING` or transport
failure is not. Do not create a replacement operation merely because a reply was lost.

See `claimcore help recovery`, `claimcore help delivery` and [Operating](operating.md) for local journal,
checkpoint and restored-installation procedures. Restore admission changes session validity; clients
must establish their current account and installation again.

## Export

Use DATA JSON when you need typed facts and proof fields. CSV is available on the commands advertised
by help. Wire owns its columns, canonical scalar formatting and RFC 4180 serialization; the CLI
streams records and supported continuations.

CSV cells beginning with formula-trigger characters are prefixed, including after leading spaces.
Preserve that prefix when passing exports to spreadsheet software. CSV is not a new authorization or
retention boundary: exported copies remain the recipient's responsibility after source erasure.

### Bounded CSV download

`POST /api/export` is a separate media contract. Its closed `query` selects `case.list`,
`case.summary`, `case.timeline`, `reminder.list` or `people.history`; `selection` uses that query's existing filters
without `cursor` or `limit`. It always starts a fresh traversal. Supply the normal contract,
authentication/origin/antiforgery and installation headers. CSV is not a representation token.

A success is complete UTF-8 `text/csv` with identity content encoding, exact `Content-Length`, a fixed
safe attachment filename and the confirmed `ClaimCore-Installation`. JSON failures retain typed
Core refusal/fault meaning or bounded-export resource advice. OpenAPI describes both media bodies.
The complete CSV is buffered before success delivery, bounded by the shared response-byte limit.
The Server cancels processing after 15 seconds, independently of the browser's response deadline;
request abort and host shutdown also cancel it. Read admission remains held through actual cleanup
and delivery. A later-page refusal, storage failure, impossible continuation or size overflow does
not produce a successful partial file. Narrow the selection or use existing CLI traversal when the
bounded download is insufficient.

Every page reauthorizes through the existing Core query in a fresh snapshot. An export is not a
cross-page database snapshot and does not promise retroactive withdrawal of bytes authorized in
an earlier snapshot. CSV scalar formatting is canonical; shared fallback account captions use the
source English catalog. Browser downloads validate media, identity encoding, complete declared byte
length and owner/installation scope before creating a Blob, then release temporary object URLs.

## CLI use

```sh
claimcore help
claimcore help case open --format json
claimcore schema invocation
claimcore schema response
```

Human verbs, `call`, `batch`, `shell` and recovery commands are documented by their own help topics.
For structured calls, inspect the invocation and response schemas before writing an adapter. Keep
stdout machine frames separate from stderr guidance, and consume complete frames rather than partial
chunks. A shell pipeline closing its output does not establish whether a write was accepted.

Third-party clients should validate their own inputs and admit complete replies against the current
contracts. Preserve integer spelling/precision and the declared UTF-8 byte profile; the OpenAPI
`x-claimcore-maxUtf8Bytes` annotations and generated transport constants are authoritative. The browser
trusts the same server that supplies its JavaScript and keeps transport identity, echo and byte checks.

For implementation and verification commands, see [Contributing](contributing.md#verification).
For the business workflow and terminology, see [Case work](case-work.md) and the [Glossary](glossary.md).

### Option syntax

CLI existing-case reads and actions accept `--case-id UUID` as an alternative to a reference operand.
References that look like UUIDs remain references. Grant/revoke ID selectors select case scope;
`ROLE@REFERENCE` retains reference semantics. ID-based erasure previews the authoritative reference
and confirms that reference before sending the identical prepared operation.

Copied ledger action hints use `--preview`; operational hints use `--dry-run`. Hints retain the
supplied binding and do not allocate an operation ID or refresh a reviewed version. Private text is
represented by acquisition placeholders. The CLI renders the supplied semantic receipt.

Named option values can use `--flag=value`. Use this spelling when a value begins with `--`;
shell quoting alone does not change option classification. A separate named value such as
`--text -h` remains text; `-h` and `--help` request command help only when not consumed as a
value and before the `--` marker. For example,
`claimcore people list --text='--urgent'` preserves the exact text filter. Repeated choices and
collection rows use repeated assigned options, such as `--lifecycle=ACTIVE --lifecycle=VOIDED`.

`--` ends option parsing. Put options before the marker. Tokens after it are operands, and each
operand still has to satisfy its normal validation. The CLI inserts profile defaults before the
marker. These dry runs build a read request and a close preview without contacting the service.

```sh
claimcore case get --format=json --dry-run -- CC-7K3M-Q8T2-PW6D-X9HR-49
claimcore case close --expected-revision=7 --outcome settled --preview --dry-run -- CC-7K3M-Q8T2-PW6D-X9HR-49
```

Text acquired from files, stdin or an editor is passed as an assigned value and cannot become an
option. Contextual read-form hints include required acquisition placeholders such as
`<operation-id>`. Replace the placeholder with the intended value.

### CLI language and display locale

Human CLI output selects wording independently from dates, times, numbers and money. Use
`--display-locale` on a human verb or recovery command, `CLAIMCORE_DISPLAY_LOCALE`, or
`claimcore configure --display-locale de-DE`. The finite shared display-locale catalog is printed
in command help. Selection uses flag, environment, profile `displayLocale`, then the selected
language's regional default. An explicit unsupported selected locale refuses visibly, including
offline preparation. `claimcore configure --show` reports the effective display locale and its source.

The wording selection uses `--language`, `CLAIMCORE_LANGUAGE`, profile `language`, then `LC_ALL`,
`LC_MESSAGES` and `LANG` through supported language matching. For example,
`claimcore case get REF --language en --display-locale lv-LV` uses English wording with Latvian
date and numeric conventions. Each shell
command resolves a fresh immutable presentation context; review, paging, receipts and recovery keep
that command's context. If the client's timezone database lacks the supplied installation zone,
observation output shows the same instant explicitly labelled UTC; the server's business date is
retained. Changing a preference does not reinterpret a previously authored amount or
date. JSON requests/replies, DATA output, CSV, identifiers and protocol tokens remain canonical.

A ledger request's identity and installation are frozen at its first preview. A long confirmation
cannot silently allocate another operation or switch installations. Expiry requires an explicit new
review. Operational commands do not acquire ledger recovery identities; uncertain delivery is
reported without a journal retry frame or automatic repeat.
