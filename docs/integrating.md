# Integrating

Use the human CLI for interactive work, structured calls for automation, or HTTPS for your own client.
The [OpenAPI contract](../contracts/openapi.json) specifies exact requests, replies and byte bounds.
The [catalog](../contracts/catalog.json) supplies meanings and choices. `claimcore help` derives the
current verbs and flags from task declarations; use these sources instead of copying field lists.

Use matching server, browser and CLI builds. Regenerate integrations when the contract identity changes.
The generated [TypeScript client](../contracts/client.ts) provides types, endpoint/task metadata and
transport constants. It does not provide request or response validators. The native CLI decodes complete
replies with its codecs.

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

Choose a read by the question you need answered: a case, a work view, reminders, notes, payments,
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

An `Affordance` carries `task`, `binding`, scalar-or-null `seeds`, `editable`, contextual `choices` and
`blocked`. Read task kind, endpoint, consequence, creation and input metadata from `taskDefinitions`.
Operation controls preview and execute; command controls post seeds plus edits to their declared
endpoint. Collection rows start from the declaration's minimum rows and defaults. Recommendations live in
`RECOMMENDATION` screen regions.
A `CONTEXT` region associates form context with its supplied actions; show it for the selected task,
rather than presenting it as another set of current facts. Other section and row actions retain their
supplied bindings.

Input `choicesFrom` names a Core lookup. `choicesWhen` is an array of
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

## Submit a change

Start with the task's help and the current target. Use the offered versions and identities, and author
only the fields that the task declares. Send `{operationId, task, binding, inputs}` with verbatim input
tokens. Omit Update inputs to keep values; use `null` only where `clearable` permits it. Existing-person
changes select an actor ID and require `binding.expectedRevision`. Acquire the positive account
`revision` from the DATA `people.grants` reply (or use the supplied HUMAN action binding). Creation
of a case or person uses an explicit zero revision and no new-target selector. `case.open` authors
`field:caseReference` in `inputs`. Creating a reminder or note selects its parent case, without a
child identity or child revision. Binding schemas distinguish case, reviewed-family case, payment
selection, work, reminder, note and person shapes; command controls use the shared empty binding.
CLI automation supplies reviewed version flags where the task declares them; interactive commands can
acquire and freeze those preconditions for review.

Case erasure additionally requires `binding.expectedFamilySequence`, acquired from DATA `case.get`'s
`familySequence` or the supplied HUMAN erase action. CLI automation supplies
`--expected-family-sequence`. Keep the case selection, installation, root version and family sequence
from the same review. `ACTOR_CHANGED` and `FAMILY_CHANGED` require explicit fresh review, preserving
authored input; never refresh and execute automatically. Exact accepted retries retain their original
binding, including these preconditions. The exact operation request is specified by OpenAPI; use
`claimcore schema operation.preview request` or `claimcore schema operation.execute request` to inspect it.

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

## Post a command

A COMMAND control posts its declared semantic inputs to the definition's endpoint, without an
operation ID or operation preview. Merge its seeds with authored edits and use the endpoint's typed
request schema. For example, `people.requests.dismiss` accepts:

```json
{
  "input:principal.kind": "HUMAN",
  "input:principal.issuer": "https://issuer.example/",
  "input:principal.subject": "waiting-account"
}
```

Command requests admit only their declared semantic input keys. Render the returned HUMAN document
for command feedback. Do not repeat an uncertain operational write automatically.

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

## CLI use

```sh
claimcore help
claimcore help case open --format json
claimcore schema invocation
claimcore schema response
```

Human verbs, `call`, `session`, `shell` and recovery commands are documented by their own help topics.
For structured calls, inspect the invocation and response schemas before writing an adapter. Keep
stdout machine frames separate from stderr guidance, and consume complete frames rather than partial
chunks. A shell pipeline closing its output does not establish whether a write was accepted.

Third-party clients should validate their own inputs and admit complete replies against the current
contracts. Preserve integer spelling/precision and the declared UTF-8 byte profile; the OpenAPI
`x-claimcore-maxUtf8Bytes` annotations and generated transport constants are authoritative. The browser
trusts the same server that supplies its JavaScript and keeps transport identity, echo and byte checks.

For implementation and verification commands, see [Contributing](contributing.md#verification).
For the business workflow and terminology, see [Case work](case-work.md) and the [Glossary](glossary.md).

### Current foundation contracts

Use a fresh format 6 installation with matching builds. Case receipts expose `payments`, an ordered
list of entry differences; there is no singular `payment` member. `case.correctPayment` binds the
selected `paymentId` and one case revision, then returns both removed and recorded differences in
one accepted operation. HUMAN controls include their shared `labelId`. Command bindings use a
closed `COMMAND` branch containing the complete validated operational request rather than seeds.

Browser response admission is generated from the finite Wire codec schemas. It rejects malformed
structure before date observation or recovery. Structural admission does not establish every native
interpretation predicate. JSON parsing loses duplicate-member and numeric-lexeme evidence; those
byte-level guarantees are not claimed by the browser. Precise timestamp strings remain unchanged.

CLI existing-case reads and actions accept `--case-id UUID` as an alternative to a reference operand.
References that look like UUIDs remain references. Grant/revoke ID selectors select case scope;
`ROLE@REFERENCE` retains reference semantics. ID-based erasure previews the authoritative reference
and confirms that reference before sending the identical prepared operation.

Copied ledger action hints use `--preview`; operational hints use `--dry-run`. Hints retain the
supplied binding and do not allocate an operation ID or refresh a reviewed version. Private text is
represented by acquisition placeholders. The CLI renders the supplied semantic receipt.

### CLI language and display locale

Human CLI output selects wording independently from dates, times, numbers and money. Use
`--display-locale` on a human verb or recovery command, `CLAIMCORE_DISPLAY_LOCALE`, or
`claimcore configure --display-locale de-DE`. The finite shared display-locale catalog is printed
in command help. Selection uses flag, environment, profile `displayLocale`, then the selected
language's regional default. An explicit unsupported selected locale refuses visibly, including
offline preparation. `claimcore configure --show` reports the effective display locale and its source.

Language retains its existing precedence: `--language`, `CLAIMCORE_LANGUAGE`, profile `language`,
then `LC_ALL`, `LC_MESSAGES` and `LANG` through supported language matching. For example,
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

HUMAN task controls now form a closed, task-discriminated union. Each operation carries its own binding
shape; each command carries its complete validated request and empty seeds/editable/choices. Dismissal
retains distinct HUMAN/SERVICE principals with bounded issuer/subject fields and fallible decoding.
Mismatched task/command bindings fail structural admission instead of reaching a seed-map cast.
### Bounded CSV download

`POST /api/export` is a separate media contract. Its closed `query` selects `case.list`,
`case.timeline`, `reminder.list` or `people.history`; `selection` uses that query's existing filters
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
