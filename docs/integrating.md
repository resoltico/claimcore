# Integrating

Use matching service, browser and CLI builds. [OpenAPI](../contracts/openapi.json) owns the 19 HTTP
endpoint shapes; the [catalog](../contracts/catalog.json) owns meanings, bindings and limits. The
[CLI reference](cli.md) derives verbs, flags and choices from the current grammar.
[Case work](case-work.md) explains the recorded business facts.

## Identity and records

A CaseId is the UUID of its accepted OPEN operation. The original reference is immutable,
ASCII-case-insensitively unique and retained after erasure. A selector explicitly distinguishes
`{"kind":"REFERENCE","caseReference":"DEMO-0001"}` from
`{"kind":"ID","caseId":"10000000-0000-7000-8000-000000000001"}`. UUID-shaped references remain references.
Selectors resolve in the transaction used for authorization and execution. Grants for one case retain
both its CaseId and original reference.

[RecordVersions](../src/ClaimCore.RecordFormat/Versions.fs) owns the current durable tags: case request
and snapshot 4, ledger entry 6, authority 3, work and reminder 1. A durable change requires a fresh
installation. Initialization accepts only the current baseline and an empty database.

## HTTPS contract

Session bootstrap uses GET. Other endpoints use POST `/api/<endpoint>` with literal dots and the input
as `application/json`. Send the matching `ClaimCore-Contract` identity and the installation binding
where required by the endpoint. Cookie POSTs require the antiforgery token; bearer authentication has
its own gates. See [identity and hosting](operating.md#identity-and-hosting).

Admit a complete reply before using it. Objects are closed; malformed UTF-8, duplicate decoded members,
unsafe numbers and excessive depth or bytes refuse. Request bodies are bounded to 65,536 bytes and
responses to 4,194,304 bytes. [Limits](../src/ClaimCore.Wire/Api/Limits.fs) and generated schemas are authoritative.

`x-claimcore-maxUtf8Bytes` is a required wire-profile assertion on decoded strings, independent of JSON
escaping or character count. It retains byte limits for names, contacts, reminder actions and narratives.
Native codecs and generated JavaScript enforce it. A standard schema validator must implement this
keyword as an assertion; ordinary JSON Schema validation alone does not establish wire admission.
See the [independent adapter](../tests/ClaimCore.Tests/Contract/Utf8SchemaValidation.fs).

Business refusals use HTTP 200 and typed codes/parameters. Structural and transport failures use
`{"hostFailure":{"code":"…","phase":"…"}}` with the status declared by
[host failure policy](../src/ClaimCore.Wire/Wire/HostFailures.fs). A transport failure does not establish
whether an earlier uncertain change committed.

An input refusal has nonempty ordered `issues`, for example:

```json
{"code":"INVALID_INPUT","action":"CORRECT_INPUT","issues":[{"field":"claimantName","code":"TOO_LONG","maximumCharacters":200}]}
```

Show every issue. Parsing precedes state and business-date admission; independent checks accumulate
within each phase. Protected Application entry points check the current account before early business
refusals. Unknown or disabled accounts receive the non-disclosing `RESOURCE_UNAVAILABLE` result.

Preview, execution and operation lookup echo a typed `operationId`. Clients check that echo and trust
the server's typed contents. They do not reconstruct acceptance by comparing family, act, parent,
entity versions, fields or effects with their input. Authentication and current disclosure remain server
responsibilities. Contract identity derives from canonical OpenAPI; locale-only wording changes preserve it.

## Presentation and discovery

Successful read outcomes carry sibling `data` and `view`. Accepted/replayed receipts and accepted
operation lookup do likewise. A `view` is either `RECORD` with labelled sections/entries, or `TABLE` with
column definitions and equally sized rows. Empty tables retain their columns. Values distinguish exact
money, dates, instants, actors, references, counts, codes, narratives and explicit absence.

The host computes the view through Presentation.Descriptor. Render it generically using the selected
language and regional formats. Do not maintain a second per-field display list or derive eligibility,
reminder timing, history differences or recommendations. JSON and CSV consume the typed `data`.
Money carries `displayScale`; padding may increase displayed precision, but meaningful digits must not be truncated.

A reviewed preview supplies `review.presentation.changes` and `unchanged`, plus `review.view` for effects
and parent facts. Preserve the reviewed input and its consent. A preview is advisory; execution checks
current permission, state, versions and business dates under the write lock.

`claimcore describe case open` and `schema ENDPOINT request|response` work offline. Catalog inputs
supply paths, branches, constants, vocabularies and choices. Retain the selected entry's subject/kind;
form values do not select an operation. `adviceGroup` identifies correction-choice advice. Labels,
hints and rules come from declared identities, rather than inferred member paths.

Roles declare allowed principal kinds and scopes. Capabilities separately declare their applicability.
Opening a case requires suitable access for all cases; a case-scoped grant does not permit creation.
An Owner manages access and gains no case rights solely from that role. Recognized assignments remain
revocable even when no longer admissible for a new grant. Offered choices never replace execution checks.

English is the message source. Non-English authored entries use `text` and the twelve-digit English
`source` hash. Generation fills stale/missing runtime text with English. Coverage gates apply to en, lv
and ar; other languages warn. GUI diagnostic wording and precise rules have separate registers.
`ScalarGuidance` supplies arguments per input, so shared wording can retain different text limits.
See [generation](contributing.md#generated-contracts) and [language status](glossary.md#language-review).

## Worklist queries and summary rows

`case.list` combines optional lifecycle, status, progress, view, responsibility, assignment, due and text
filters. `limit` is 1–50; omitted order is REFERENCE. A view intersects explicit filters. MINE describes
actual responsibility within current visibility; viewing rights do not assign work or grant access.

Search is a literal substring of reference, claimant, insurer and narratives. Current admission precedes
NFKC/invariant-lower normalization. `%`, `_` and backslash remain literal; accents are not removed.
REFERENCE sorts folded references; RECENT sorts fact changes newest first; NOTIFIED sorts notification
dates oldest first; FOLLOW_UP sorts the earliest pending reminder. Missing dates sort last, with stable
reference ties. Each page authorizes again and has its own snapshot. Concurrent changes can move,
repeat or omit rows during traversal.

Rows carry CaseId/reference, version, headline, attention, stages, recommendations and current work facts.
Erased states have no business fields. `ActorRef` supplies current name or explicit erasure, kind and
availability without exposing sign-in identity or contact information. `exactReference` is a separate
nullable match under current read rights.

`case.summary` accepts the same filters without page controls. Counts and claimed/payable totals use
that read's snapshot. Totals are exact decimal strings per currency, with no conversion or rounding.
`work.views` returns both case and reminder view counts from one read and one business-date basis.
Separate calls can observe different writes.

`case.get` advice supplies offered acts, correction choices, history access NONE/SUMMARY/FULL and a
closed partition of blocked acts with their core refusal. Advice never authorizes later execution.

## Changes, work and reminders

OPEN and DECIDE supply complete groups. AMEND_REGISTRATION changes selected registration facts.
A money assertion always contains amount and currency together. Omission preserves a partial value;
explicit null clears only an optional value. Required nulls, half money and unknown members refuse.

CORRECT distinguishes KEEP/UPDATE registration, KEEP/UPDATE/CLEAR decision and
KEEP/REPLACE/REAFFIRM/CLEAR payment. A paid decision update requires explicit payment intent and its
capability. Reaffirmation retains an existing payment date and does not create a payment or transfer money.
Corrections cannot add missing groups or create a no-op version. Conflict requires fresh review and intent.

Incident description and handling summary are separate optional LF narratives, each bounded to 600
Unicode scalar values by current admission (shown as characters in the interface). Spaces and spelling are preserved; control/direction characters refuse.
CLI file/editor inputs normalize CRLF to LF before review; lone CR refuses. Canonical values are frozen
before confirmation. `--*-file` and interactive `--edit-*` options are mutually exclusive for one field.
The editor is one executable from VISUAL or EDITOR, without shell expressions or arguments.

Case work starts empty at version zero after OPEN and has its own expected version. UPDATE_WORK changes
responsibility and/or handling summary. Summary edits permit active open or closed cases; responsibility
changes require an active open case. Only human ReadCase + ManageCaseWork rights allow these changes.
`case.assignees` lists eligible people without disclosing the account directory. Execution checks the
selected person again. Disable/revocation retains the recorded responsibility and marks it unavailable.

Reminders have independent positive versions, immutable parent/ID, action, planned date and
PENDING/COMPLETED/CANCELLED state. CREATE uses the operation UUID as ReminderId. COMPLETE_AND_PLAN_NEXT
atomically completes one reminder and creates one replacement with that ID. A stale version or invalid
replacement changes neither effect. A terminal reminder must explicitly reopen with a new date.

Create/update/reopen/planning require an active open parent. Complete/cancel also permit cleanup on an
active closed parent. Close/void preserve reminder dates; reactivation restores their relevance.
Work/reminder changes do not advance the case-fact version. Erasure removes related erasable contents.

Follow-up is the earliest pending due date, with stable reminder-ID ties. Case due filters classify that
minimum; reminder filters classify individual reminders. Pending work can be overdue. Current timing is
OVERDUE/TODAY/UPCOMING only for active pending work, otherwise null. Each loaded row retains its own
page's `basis`; loading another page does not reclassify earlier rows. Missing next action is attention,
not a refusal or an automatic reminder. Summary attribution follows actual summary edits, not assignment changes.

## Disclosure and cursors

Current access governs every reply. Receipts distinguish DISCLOSED, WITHHELD and ERASED contents;
being entitled to acceptance metadata does not imply access to its business data. Session/access changes
clear disclosed views; an obsolete response cannot populate a replacement context.

`case.timeline` combines case, work and reminder changes by descending ledger sequence. Optional FACTS
or WORK filtering applies in Core. Disclosed differences include full `items` and a core event/overview;
WITHHELD and ERASED contain no difference payload. Overview highlights refer to supplied items/facets;
validate their relationships and use the generated helper rather than choosing other changes.

A revision conflict can supply WITHHELD or a bounded page of fifty timeline entries. Its continuation
uses `case.timeline` for the same target without a filter and retains the observed version range.
Every continuation rechecks disclosure. Other cursors are opaque positions for their issuing endpoint,
resource and filters. Follow non-null `nextCursor` even after a short page. A page is not a fixed export.

`authority.list` filters saved names/contacts and recognized raw roles. Current assignment state differs
from effective authorization. Row role summaries are bounded summaries, not full grant inventories.
`authority.grants` pages the selected account's assignments. `authority.history` pages access changes
newest first and optionally filters their target `actorId`; it requires installation-wide ReadAuthority. Case-only read grants do not permit access review.
REGISTER/PROVISION_OWNER retain principal facts, GRANT/REVOKE retain scope, and profile history can be erased.

REGISTER requires an entered profile. ADMIT records the account, profile and 1–48 roles atomically;
its receipt/checkpoint position is the final effect. Profile erasure removes saved name/contact and
previous profile payloads, while identity, roles and recorded work remain. Current names do not come
from identity-provider hints. Pending access requests are a separate, expiring cache and grant no rights.

## Delivery recovery

Operation IDs are UUIDv7. First execution is allowed from issue time minus two minutes through issue
time plus ten minutes, using the database instant under the head lock. Replay lookup precedes the window
check. Expiry alone does not prevent replay. Out-of-window refusal includes `issuedAt`, `observedAt`
and `phase: NOT_STARTED`. Keep the dispatched ID and exact content while delivery is uncertain.

`operation.get` takes the operation ID with the authenticated installation binding:

- ACCEPTED carries `data.receipt`, `data.byRequester` and `view`; foreign receipts require current disclosure rights.
- PENDING carries `data.finalAt`, the issue time plus ten minutes.
- NOT_ACCEPTED is final for the current account. After expiry, absence is confirmed under the execution lock without an append.

Enabled registered accounts can recover their own acceptance metadata without business roles. Erasure
and current rights still govern content; a hidden foreign acceptance is never the requester's acceptance.
Final absence relies on the [database clock contract](operating.md#database-clock).

Unconfirmed changes are checked automatically in the browser. The journal stores origin, installation,
account and attempt identity before dispatch, with no business payload or credentials. Reload can check
identity but cannot recreate the submission. Automatic undispatched IDs older than nine minutes are
refreshed before dispatch; dispatched requests retain their IDs for every retry.

`claimcore operation get OPERATION_ID` checks an ID. `recovery list` and `recovery check ATTEMPT_ID`
use scoped local records. Interactive checks repeat until final; automation prints PENDING/finalAt and
exit 4 with delivery DELIVERED. An uncertain mutation instead has delivery UNCERTAIN and prints its exact
retry frame on stderr. Other failures do not disprove an earlier uncertain commit.

## CLI use

Keep the complete publish directory and run its apphost. Help, describe, schema and dry-run work offline.
Verbs default to text; `--format json` selects structured replies and `--language` selects interface text.
Arabic CLI dates/numbers retain Latin digits. Business values are never translated.

`call` reads one UTF-8 JSON frame `{ "endpoint": "…", "input": {} }` from stdin, multiline allowed,
until EOF. `session` reads one complete frame per physical line (NDJSON). Invocation frames are bounded
to 131,072 bytes, embedded requests to 65,536 bytes and responses to 4,194,304 bytes.
Reply frames contain nullable endpoint/operationId, delivery NOT_SENT/DELIVERED/UNCERTAIN and a typed result.
Delivery describes transport progress; inspect the outcome for acceptance.

Verbs and ID-less call/session execution mint a UUIDv7 at dispatch and print it to stderr. A dry run
without an explicit ID prints none. For an exact retry of an ID-less file, insert the printed ID and
preserve every other value. Resending the original ID-less file creates a separate operation.

Interactive existing-case text commands can fetch an omitted expected version, preview it and ask for
confirmation, default No. `--preview` stops after review; `--dry-run` stays offline and requires the
expected version for an existing-case change. Automation, redirected streams and JSON output require
explicit expected versions. Confirmation preserves the reviewed facts; the CLI never silently rebases.
ERASE and ERASE_PROFILE retain explicit destructive confirmation.

Public settings live in `$XDG_CONFIG_HOME/claimcore/profile.json`, or
`~/.config/claimcore/profile.json` when XDG_CONFIG_HOME is unset/empty. Allowed members are serviceUrl,
issuer, clientId, authMode, language, serviceTrustRootFile and oidcTrustRootFile. The profile is at most
32,768 bytes; individual stored values are at most 8,192 UTF-16 code units. A missing profile is normal;
an existing refused profile fails even when environment variables supply settings. Writes atomically
replace a 0600 file in a 0700 directory and refuse unsafe existing directories.

| Setting | Environment override |
| --- | --- |
| serviceUrl | CLAIMCORE_SERVICE_URL |
| issuer | CLAIMCORE_OIDC_ISSUER |
| clientId | CLAIMCORE_OIDC_CLIENT_ID |
| authMode | CLAIMCORE_CLI_AUTH_MODE |
| language | CLAIMCORE_LANGUAGE |
| serviceTrustRootFile | CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE |
| oidcTrustRootFile | CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE |

Environment values override public members individually. Language precedence is `--language`,
CLAIMCORE_LANGUAGE, profile language, LC_ALL/LC_MESSAGES/LANG, then English. Secrets remain environment-only
file references through CLAIMCORE_OIDC_CLIENT_SECRET_FILE, never saved values. `configure --show` reports
values and sources without opening the secret file. A running session retains its admitted connection settings.

The service is an exact canonical absolute HTTPS root ending in `/`, without user info, query or fragment.
`https://localhost/` is admitted; `https://localhost` and `https://localhost/path` are refused. Issuers
are exact canonical HTTPS addresses and may have provider paths, such as `https://identity.example/realms/claimcore`.
A client ID is nonblank, has no surrounding whitespace and is at most 256 UTF-16 code units.
Syntax acceptance does not establish TLS, discovery or authentication success.

Authentication mode is literal `interactive` (browser PKCE) or `automation` (client credentials with a
private secret file). Private paths are absolute valid UTF-8 without NUL. The final target must be a
bounded nonempty regular file; group read is allowed, while other-user access and group write/execute
refuse. 0600 is sufficient; 0640 and 0440 can be admitted. Symlinks follow their final targets; ownership,
ACLs and filesystem administrators remain trusted. Windows private-file admission refuses.
See [private configuration](operating.md#private-configuration) for handle and replacement limits.

Secret files contain nonblank UTF-8 text, trimmed before use, at most 4,096 bytes. Explicit trust-root
files are PEM CA roots, at most 16,384 bytes; omitting them uses system trust. Overrides apply only to
HTTPS loopback or `.localhost` endpoints and retain hostname and certificate validity checks.
MISSING, INVALID and PRIVATE_INPUT_REFUSED remain distinct setup facts. A NOT_SENT setup attempt does
not establish the outcome of an earlier uncertain submission.

`shell` requires interactive mode and terminal stdin. Quotes/backslashes group arguments; metacharacters
do not execute or expand. Tokens and arrow-key history remain in memory, with no history or token-cache
file. Fifteen idle minutes at a command/confirmation prompt discard credentials; exit, EOF and SIGTERM
also discard them. Refresh failure starts a new sign-in. Ctrl-C cancels the command; a second Ctrl-C at
an empty prompt exits. Outcomes use stdout, prompts and command exit codes use stderr. An uncertain
mutation prints its retry frame; the shell continues, while NDJSON session stops on uncertainty/cancellation.

## Exports and browser routes

CSV uses UTF-8 without BOM, CRLF and RFC 4180 quoting. Headers are qualified wire paths owned by
[CaseCsv](../src/ClaimCore.Cli/CaseCsv.fs), [TimelineCsv](../src/ClaimCore.Cli/TimelineCsv.fs) and
[AuthorityHistoryCsv](../src/ClaimCore.Cli/AuthorityHistoryCsv.fs). Empty cells represent absent/erased values.
Every cell starting with a formula trigger (`=`, `+`, `-`, `@`, TAB or CR), including after leading
spaces/TABs, receives an apostrophe before quoting. This alters affected cells; JSON preserves exact values.
`--all` follows cursors and prints the header once. Human text visibly escapes terminal controls and backslashes.

The browser exposes Cases, Reminders, People and access, Access history, Unconfirmed changes and Settings.
Case links use `/cases/id/CASE-ID` or `/cases/reference/REFERENCE`; editors append `/acts/ACT`.
`/cases/new` opens creation; `/people/ACTOR-ID` opens account detail; `/recovery/OPERATION-ID` checks a change.
Worklist filters survive same-origin navigation, but navigation state stores no business payload.

Reference segments use encodeURIComponent. Exact `.`/`..` and references beginning `~` instead use a `~`
prefix plus unpadded base64url of UTF-8: `~Lg`, `~Li4`, `~fg`. This avoids path normalization collisions.
A UUID-shaped reference never becomes an ID merely because of its spelling.

Language, regional format and theme are independent preferences. Browser preference format 3 uses
`claimcore.presentation.v3`; invalid records use defaults and invalid individual values fall back.
Failed language activation retains the current working language/direction. Reads go directly to the
server and obsolete requests are canceled; the [server queue](operating.md#request-admission) owns admission.

## Wire examples

The [requests](../tests/fixtures/examples/requests.json) and
[replies](../tests/fixtures/examples/responses.json) are small hand-authored decode examples and rejection
controls. They are not captured output fixtures. Executable [synthetic examples](../examples/README.md)
omit operation IDs so the CLI can mint them when sent.
