# Integrating

Use the human CLI for interactive work, structured calls for automation, or HTTPS for your own client.
The [OpenAPI contract](../contracts/openapi.json) specifies exact requests, replies and byte bounds.
The [catalog](../contracts/catalog.json) supplies meanings and choices. `claimcore help` derives the
current verbs and flags from task declarations; use these sources instead of copying field lists.

Use matching server, browser and CLI builds. Regenerate integrations when the contract identity changes.
The generated [TypeScript client](../contracts/client.ts) provides types, endpoint/task metadata and
transport constants. It does not provide request or response validators. Native CLI decoding remains.

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

HUMAN documents carry a subject, actions, typed query links and row controls; DATA exposes automation facts.
An operation lookup link with empty input opens the ID-entry form; actual `operation.get` calls require `operationId`.
Choose the representation your consumer needs; neither grants additional permissions.

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
changes select an actor ID. The exact operation request is specified by OpenAPI; use
`claimcore schema operation.preview request` or `claimcore schema operation.execute request` to inspect it.

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

Successful payloads vary by endpoint. Rejections use the shared representation-specific rejection
shape, and failures use the common fault shape with a Core message ID. Input issues target declared
input tokens. Use structured codes and actions for automation; do not parse translated prose.

## Recover an uncertain change

Before sending a mutation, retain enough identity to confirm it after interruption. The built-in
clients maintain a local recovery journal without storing the request body or entered personal facts.
Recovery records belong to a specific service, installation and account.

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
