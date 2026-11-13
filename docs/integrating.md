# Integrating

Choose human verbs for interactive work, JSON calls for automation, or HTTPS for a client. Start with
`claimcore help case open` and `claimcore help exit-codes`; use `--format json` for machine discovery.
Use matching service, browser and CLI builds. [OpenAPI](../contracts/openapi.json) owns the 21 HTTP
endpoint shapes; the [catalog](../contracts/catalog.json) owns meanings, bindings and limits. The
output of `claimcore help` derives verbs, flags and choices from the current grammar.
[Case work](case-work.md) explains the recorded business facts.

## Supported country and currency codes

[Core's authored snapshots](../src/ClaimCore.Domain/CodeVocabularies.fs) own eligibility and record their
sources/as-of dates. [Generated code vocabularies](../contracts/codes.ts) publish the same sets to clients;
`claimcore help schema` lists them offline. Intl supplies display names only, falling back to the code.

Countries are officially assigned ISO 3166-1 alpha-2 codes plus XK (Kosovo) and ZZ (unknown/international).
Reserved, transitional and other user-assigned codes are not accepted. Currencies are active ISO 4217
codes from SIX, excluding fund codes and X-prefixed non-national codes; XAF, XCD, XOF, XPF and XCG remain
supported. This does not impose per-currency minor-unit admission rules. Picker search ranks exact code,
code prefix, localized name prefix, then substring, using locale-aware case folding.

Unsupported values refuse with a field diagnostic. Use a fresh installation for these code contracts;
there are no migrations or compatibility readers.

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

The structural profile also checks disclosed highlight references and renderability: unique message
argument keys, unique table column/row keys, matching row widths, disjoint covering row groups, and
nonrepeating screen roles except FACTS. Generated client validation includes these checks. Structural
admission does not establish financial equations or authorize a mutation; core admission and semantic
replay own those rules.

Business refusals use HTTP 200 and typed codes/parameters. Structural and transport failures use
`{"hostFailure":{"code":"…","phase":"…"}}` with the status declared by
[host failure policy](../src/ClaimCore.Wire/Wire/HostFailures.fs). A transport failure does not establish
whether an earlier uncertain change committed.

An input refusal has nonempty ordered `issues`, for example:

```json
{"code":"INVALID_INPUT","action":"CORRECT_INPUT","issues":[{"field":"claimantName","code":"TOO_LONG","maximumCharacters":200}]}
```

Show every issue. Case preparation retains parsed scalars for syntax and current admission. Incomplete
input still reports independent syntax, business-date and chronology failures in one round. Complete
canonical intent reaches exact accepted replay before current revision, lifetime and date restrictions.
Authority preparation likewise preserves typed profile values for accepted comparison; fresh writes
then apply current profile policy to those values and accumulate field issues once.
Protected Application entry points check the current account before early business refusals. Unknown
or disabled accounts receive the non-disclosing `RESOURCE_UNAVAILABLE` result.

Authority grant input issues may carry `grantOrdinal`, a zero-based ordinal in the submitted collection.
Keep the immutable submitted ordinal-to-local-row-key map with that preview. Reordering or removing a
row must not attach a late issue to a different current row. Local row keys never enter business bytes.

Preview, execution and operation lookup echo a typed `operationId`. Clients check that echo and trust
the server's typed contents. They do not reconstruct acceptance by comparing family, act, parent,
entity versions, fields or effects with their input. Authentication and current disclosure remain server
responsibilities. Contract identity derives from canonical OpenAPI; locale-only wording changes preserve it.

## Presentation and discovery

Actor endpoints negotiate `ClaimCore-Representation: HUMAN|DATA`; an omitted header selects DATA.
Unknown, empty or conflicting selections refuse before dispatch. Responses require the matching
`representation` discriminator. Reject another representation even if its body is otherwise valid.
Host session, catalog and health contracts do not negotiate these representations.
The generated TypeScript `ResponseFor<Endpoint, Representation>` and `isResponseFor` retain the
selected channel in types and structural admission. `HumanResponseByEndpoint` and
`DataResponseByEndpoint` provide the corresponding endpoint maps.

DATA carries typed machine results without a Screen. HUMAN carries locale-neutral documents:
`screen`, a closed `context` of interaction handles and permitted input seeds, `basis` and `disclosure`.
A successful HUMAN read binds its context kind to the requested endpoint; for example, `case.get`
requires CASE and `case.assignees` requires ASSIGNEES. Generated types and structural validators retain
that distinction. Refusals and failures do not require a successful resource context.
List navigation uses CASES (`caseIds`), REMINDERS (parent/reminder identity pairs), and PEOPLE
(`actorIds`) contexts. Their ordered handles bind the exact supplied Screen row keys. Use handles for
destinations and supplied Screen values for captions; a displayed reference or name is not a route ID.
ACCESS_REQUESTS controls bind each displayed row to a typed principal and an unverified editable
profile seed. The seed prefers the name hint, then username, and may include the email hint as contact.
Hints confer no authority; preview and execute independently admit the entered account/profile.
PERSON controls bind `authority.grants` to the reviewed actor and principal. Tasks follow current
management permission and maintenance state. A saved profile becomes an editable seed only when the
profile task is offered; the Screen remains authoritative for saved display. Offers are advisory and
execution independently checks the command, including last-owner protection.
CASE and NOTE_PAGE contexts bind displayed note rows to typed note identities and current removal
permissions. Removal requires the offered note task; a withheld or erased document carries no note
controls. Use the supplied narrative in the NOTES region for saved display.
Choosing a representation never changes authorization or performs a second use-case query. Include
representation in transport cache/coalescing keys, but never in authored operation bytes, operation
identity, ledger commitments or query cursor binding. Native text and the browser request HUMAN;
JSON and CSV use DATA.

Render saved human business content from supplied Screen regions, in order and at their declared role
placements. The seven content kinds are ENTRIES, TABLE, STAGES, CODES, ACTIONS, NOTICE and TIMELINE.
TABLE rows have stable keys; core-selected groups and row details retain their meaning across
interaction. Only FACTS may repeat a role. Keep IDs, versions and copy controls in DETAILS, closed by default.
For offered correction updates, `screen.currentGroups` supplies keyed `registration`/`decision`
sections for the saved “Before” display. Render their supplied title and entries; editable seeds are
for drafting. Groups disappear when unoffered or erased and are invalid under tightened disclosure.
The CASE context's closed `history` hint follows current read permission so a client can discard earlier,
more-permissive history as soon as that observation tightens. Each history query independently admits
and discloses its own content.
Use document context for interaction and permitted editable seeds. Do not reconstruct business display,
action eligibility, due classes or differences from a DATA projection. Money carries exact values and
`displayScale`; formatting must retain meaningful digits. Regional formats and interface language are
independent. [Screen presenters](../src/ClaimCore.Presentation/Presentation/Screens) own human composition.

A HUMAN reviewed preview supplies `outcome.review.presentation.changes` and `unchanged`, plus
`outcome.review.document` for identity, effects, technical details, observation and reviewed bindings.
It carries no duplicate before/after snapshots. Core consequence and confirmation-target metadata select
the client's confirmation control; retain the exact reviewed command bytes and one consent. A preview
is advisory: execution checks current permission, state, versions and business dates under the write lock.
Accepted HUMAN outcomes supply `outcome.document`; DATA acceptance supplies the typed receipt in
`outcome.data`. Input refusals in HUMAN use semantic input identities and optional submitted row ordinals;
DATA diagnostics retain machine targets. Neither channel grants greater disclosure.

HUMAN receipts supply `context.caseLink` for optional case navigation. A null handle offers no link;
receipt target IDs alone are not navigation controls. `context.refreshCase` separately selects the CLI's
follow-up work view. Both handles must match the receipt's case, and every subsequent case read is
authorized again.

`claimcore help case open` and `schema ENDPOINT request|response` work offline. Semantic task
identities select the operation and its inputs; form values do not select an operation. Use typed
assembly for selected updates, money and submitted collections. Native mutation acquisition retains
its typed intent through interactive preparation and command assembly. Incomplete drafts never gain
an invented operation identity or reviewed revision. Every mutation dry-run is an offline intent:
supplied metadata stays exact and omitted operation IDs remain omitted. An offline JSON syntax refusal
contains `delivery: NOT_SENT` and the core `diagnostic`, without an invented operation reply or ID.
Labels, hints, rules and supported choices come
from declared identities, rather than inferred member paths. JSON command help exposes the semantic
`task` with input identities, sources, modes, choices and collection bounds; it contains no transport
path or branch instructions.

Roles declare allowed principal kinds and scopes. Capabilities separately declare their applicability.
Opening a case requires suitable access for all cases; a case-scoped grant does not permit creation.
An Owner manages access and gains no case rights solely from that role. Recognized assignments remain
revocable even when no longer admissible for a new grant. Offered choices never replace execution checks.

English is the message source. Non-English authored entries use `text` and the twelve-digit English
`source` hash. Presentation resolves stale/missing text to English at runtime. Coverage gates apply to en, lv
and ar; other languages warn. GUI diagnostic wording and precise rules have separate registers.
`ScalarGuidance` supplies arguments per input, so shared wording can retain different text limits.
Fetch public `GET /catalogs/{language}.json` for the runtime core/chrome catalog. It returns a strong
ETag with no-cache, no cookies, and 404 for an unknown language, under host ingress admission.
See [generation](contributing.md#generated-contracts) and [language status](glossary.md#language-review).

## Worklist queries and summary rows

`case.list` combines optional lifecycle, status, progress, view, responsibility, assignment, due and text
filters. `limit` is 1–50; omitted order is REFERENCE. A view intersects explicit filters. MINE describes
actual responsibility within current visibility; viewing rights do not assign work or grant access.

Search is a literal substring of reference, policy number, insurer claim reference, claimant, insurer and narratives. Current admission precedes
NFKC/invariant-lower normalization. `%`, `_` and backslash remain literal; accents are not removed.
REFERENCE sorts folded references; RECENT sorts fact changes newest first; NOTIFIED sorts notification
dates oldest first; FOLLOW_UP sorts the earliest pending reminder. Missing dates sort last, with stable
reference ties. Each page authorizes again and has its own snapshot. Concurrent changes can move,
repeat or omit rows during traversal.

Rows carry CaseId/reference, fact revision, `workRevision`, reassignment eligibility, headline, attention,
stages, recommendations, payment count/paid amount/last date and current work facts.
Erased states have no business fields. `ActorRef` supplies current name or explicit erasure, kind and
availability without exposing sign-in identity or contact information. `exactReference` is a separate
nullable match under current read rights.

`case.summary` accepts the same filters without page controls. Counts and claimed/payable/paid totals use
that read's snapshot. Totals are exact decimal strings per currency, with no conversion or rounding.
`work.views` returns both case and reminder view counts from one read and one business-date basis.
Separate calls can observe different writes.

`case.get` advice supplies offered acts, correction choices, history access NONE/SUMMARY/FULL and a
closed partition of blocked acts with their core refusal. Advice never authorizes later execution.

## Identity and records

A CaseId is the UUID of its accepted OPEN operation. The original reference is immutable,
ASCII-case-insensitively unique and retained after erasure. A selector explicitly distinguishes
`{"kind":"REFERENCE","caseReference":"DEMO-0001"}` from
`{"kind":"ID","caseId":"10000000-0000-7000-8000-000000000001"}`. UUID-shaped references remain references.
Selectors resolve in the transaction used for authorization and execution. Grants for one case retain
both its CaseId and original reference.

[InstallationFormat](../src/ClaimCore.RecordFormat/InstallationFormat.fs) owns the single installation
format, stored with the installation and each ledger entry. Payloads have no format tags. A durable
change requires a fresh installation. Initialization accepts only the current baseline and an empty database.

Registration and decision inputs use flat amount/currency pairs, including amendments and corrections:
`claimedAmount` with `claimedCurrency`, and `payableAmount` with `payableCurrency`. Updates require both
members together. Optional registration facts may be omitted or explicitly cleared with `null`.

## Changes, work and reminders

OPEN and DECIDE supply complete groups. AMEND_REGISTRATION changes selected registration facts.
A money assertion always contains amount and currency together. Omission preserves a partial value;
explicit null clears only an optional value. Required nulls, half money and unknown members refuse.

CORRECT selects KEEP/UPDATE registration and KEEP/UPDATE/CLEAR decision. It cannot create a missing
decision or an unchanged correction. Payments are separate entries identified by their accepting
operation UUID. RECORD_PAYMENT needs date, positive paidAmount and paidCurrency; REMOVE_PAYMENT needs
paymentId. There is no total-50 payment limit. The exact-integer count range remains a technical bound;
PAYMENT_COUNT_LIMIT refuses saturation without overflow. Remove an entry only to correct a recording
error. Entries use the decision currency, summing to at most payable, with newly asserted dates on/after
decision and no later than business date. Previously accepted dates survive calendar rollback.
No CLEAR_PAYMENT, reaffirmation or payment correction branch remains. Changing a decision preserves entries and must satisfy their constraints;
withdrawal/clearing requires zero active entries. Zero payable with no entries is Paid.

`case.payments` is an authorized current-entry read, default 20 and maximum 50 entries per page. It
excludes removed entries and orders accepting effects newest first by operation sequence and effect
index. The opaque continuation binds installation, case, active selection and order; it grants no rights.
Each page supplies the observed case revision, financial summary and permitted per-entry removal handles.
Case observations include only the latest three entries plus total count. Receipts and retained decisions
carry a bounded changed/removed entry and resulting summary, never the entire payment history.

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

## Add and page case notes

Execute NOTE/ADD_NOTE with text, or NOTE/REMOVE_NOTE with noteId and the parent selector. Neither needs
an expected revision. ReadCase + ManageCaseWork permits changes on an active open or closed case.
Text permits 1–2,000 Unicode scalar values and at most 8,192 UTF-8 bytes, with LF line breaks.

`case.get` supplies the latest three current notes and their count. `case.notes` requires ReadCase and
returns a parent-scoped cursor page of at most 20. Follow `nextCursor`; each page authorizes again.
Removed text leaves current reads but remains in FULL history until parent erasure. SUMMARY history
retains the event without text. Note writes observe maintenance and do not advance fact/work revisions.

## Disclosure and cursors

Current access governs every reply. Receipts distinguish DISCLOSED, WITHHELD and ERASED contents;
being entitled to acceptance metadata does not imply access to its business data. Session/access changes
clear disclosed views; an obsolete response cannot populate a replacement context.

`case.timeline` combines case, work, reminder and note changes by descending ledger sequence. Optional FACTS, WORK
or NOTES filtering applies in Core. Disclosed differences include full `items` and a core event/overview.
Timeline and initial conflict pages return at most ten entries, even when `limit` requests up to 50,
to retain full details within the 4 MiB response envelope. Follow issued cursors for the remaining entries.

WITHHELD and ERASED contain no difference payload. Overview highlights refer to supplied items/facets;
integration consumers validate their relationships. Human clients render the supplied highlights and full
detail sections rather than selecting or reconstructing business fields.

A revision conflict can supply WITHHELD or a bounded page of ten timeline entries. Its continuation
uses `case.timeline` for the same target without a filter and retains the observed version range.
Every timeline page (including an empty page) carries `disclosure`: FULL, SUMMARY or ERASED, computed
from its current authorization/lifecycle snapshot. Initial conflict pages carry the same field. On a
changed disclosure level, discard previously loaded items/views, keep the accepted continuation and its
issued cursor, and refresh current-case disclosure. Every continuation rechecks disclosure. Other cursors are opaque positions for their issuing endpoint,
resource and filters. Follow non-null `nextCursor` even after a short page. A page is not a fixed export.

`authority.list` filters saved names/contacts and recognized raw roles. Current assignment state differs
from effective authorization. Row role summaries are bounded summaries, not full grant inventories.
`authority.grants` pages the selected account's assignments. `authority.history` pages access changes
newest first and optionally filters their target `actorId`; it requires installation-wide ReadAuthority. Case-only read grants do not permit access review.
REGISTER/PROVISION_OWNER retain principal facts, GRANT/REVOKE retain scope, and profile history can be erased.

REGISTER requires an entered profile. ADMIT records the account, profile and 1–48 roles atomically;
its receipt position and hash identify the complete operation; each effect has its own ordinal. Profile erasure removes saved name/contact and
previous profile payloads, while identity, roles and recorded work remain. Current names do not come
from identity-provider hints. Pending access requests are a separate, expiring cache and grant no rights.

## Delivery recovery

Operation IDs are UUIDv7. First execution is allowed from issue time minus two minutes through issue
time plus ten minutes, using the database instant under the head lock. Replay lookup precedes the window
check. Expiry alone does not prevent replay. A committed `expiredBefore` frontier also refuses unseen
IDs strictly older than that cutoff; equality is not expired. Out-of-window refusal includes `issuedAt`,
`observedAt` and `phase: NOT_STARTED`. `OPERATION_CLOCK_UNAVAILABLE` identifies the durable cutoff and
current observation; check acceptance and ask the operator to check clock state for a just-prepared ID.
Keep the dispatched ID and exact content while delivery is uncertain.

A delivered business refusal is not final for an operation ID: rejected verdicts do not append or
reserve it. CLI recovery therefore retains such attempts as **refused, awaiting final confirmation**;
it removes them only after acceptance or final `NOT_ACCEPTED`. A subsequent actual send of the same
attempt resets its local delivery knowledge to unconfirmed. No business payload is stored.

CLI recovery format 3 stores identity and delivery knowledge and requires fresh CLI state.
There are no migrations or compatibility readers. Browser journal format and business installation
format are separate contracts.

`operation.get` takes the operation ID with the authenticated installation binding:

- DATA ACCEPTED carries `data.receipt` and `data.byRequester`; HUMAN carries `document` and `byRequester`. Foreign receipt content requires current disclosure rights.
- PENDING carries `data.finalAt` in DATA, or `finalAt` and a core document in HUMAN; the deadline is issue time plus ten minutes.
- NOT_ACCEPTED is final for the current account. Confirmation commits the monotonic expiry cutoff under the execution lock before replying, without a business append. A committed cutoff supports read-only confirmation.

Enabled registered accounts can recover their own acceptance metadata without business roles. Erasure
and current rights still govern content; a hidden foreign acceptance is never the requester's acceptance.
Final absence relies on the [database clock contract](operating.md#database-clock).

Unconfirmed changes are checked automatically in the browser. The journal stores origin, installation,
account and attempt identity before dispatch, with no business payload or credentials. Reload can check
identity but cannot recreate the submission. Recovery cards show pending deadlines and final Recorded
or Not recorded answers. Final cards are held only in this workspace's memory and clear when the
account, installation, session or authoritative read cache changes. A disclosed recorded case offers
Open case. Saved identities cannot offer Send again or recover a case reference/request body; exact
resending remains available only while the original prepared request is retained. Automatic undispatched IDs older than nine minutes are
refreshed before dispatch; dispatched requests retain their IDs for every retry.

CLI request assembly and recovery records share Core subjects and each subject’s action vocabulary,
including NOTE/ADD_NOTE and NOTE/REMOVE_NOTE. The state directory follows `XDG_STATE_HOME`, or
`HOME/.local/state` when unset; recovery failures refer to that configured location neutrally.

`claimcore operation get OPERATION_ID` checks an ID. `recovery list` and `recovery check ATTEMPT_ID`
use scoped local records. Interactive checks repeat until final; automation prints PENDING/finalAt and
exit 4 with delivery DELIVERED. An uncertain mutation instead has delivery UNCERTAIN and prints its exact
retry frame on stderr. Other failures do not disprove an earlier uncertain commit.

## CLI use

A closed stdout pipe ends discovery and read output quietly. Mutation output loss retains exit 4 and its recovery instructions; other I/O failures remain errors.

Keep the complete publish directory and run its apphost. Help, schema and dry-run work offline. `schema` without a name and `help schema` list the available exports.
Command help shows direct execution and offline dry-run forms. Operation IDs are optional for mutations; direct execution requires the declared revision and destructive confirmation.
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
confirmation, default No. `--preview` stops after review. `--dry-run` validates other input offline even without a version;
its incomplete draft cannot be dispatched. Supply a version and operation ID for an executable frame. Automation, redirected streams and JSON output require
explicit expected versions. Confirmation preserves the reviewed facts; the CLI never silently rebases.
ERASE and ERASE_PROFILE retain explicit destructive confirmation.

Configure public connection settings, then inspect their effective sources:

```sh
claimcore configure --service-url https://claims.example/ --issuer https://identity.example/ \
  --client-id claimcore-cli --auth-mode interactive
claimcore configure --show
claimcore case list
```

The profile is `$XDG_CONFIG_HOME/claimcore/profile.json`, or `~/.config/claimcore/profile.json`.
Environment variables override members individually. Language follows explicit `--language`,
CLAIMCORE_LANGUAGE, profile, LC_ALL/LC_MESSAGES/LANG, then English. A refused existing profile remains
an error even with environment overrides. See `claimcore help configuration` for every setting and bound.

Use an exact canonical HTTPS service root ending in `/`, without a path, user info, query or fragment.
Issuer addresses may have provider paths. Use interactive browser PKCE for people; automation uses client
credentials and CLAIMCORE_OIDC_CLIENT_SECRET_FILE pointing to a private UTF-8 file. Secrets are never
stored in the public profile. [Private-file admission](operating.md#private-configuration) applies to
profiles, secrets and explicit PEM trust roots. Trust overrides are limited to loopback/`.localhost`
and retain hostname and validity checks. `configure --show` never opens secret contents.

`shell` requires interactive mode and terminal stdin. Quotes/backslashes group arguments; metacharacters
do not execute or expand. Tokens and arrow-key history remain in memory, with no history or token-cache
file. Fifteen idle minutes at a command/confirmation prompt discard credentials; exit, EOF and SIGTERM
also discard them. Refresh failure starts a new sign-in. Ctrl-C cancels the command; a second Ctrl-C at
an empty prompt exits. Outcomes use stdout, prompts and command exit codes use stderr. An uncertain
mutation prints its retry frame; the shell continues, while NDJSON session stops on uncertainty/cancellation.

## Exports and browser routes

Activity and authority CSV exports include `operationId`, `sequence`, `effectIndex` and
`operationHash`. Order effects by the sequence/index pair; the hash commits the complete operation.

CSV uses UTF-8 without BOM, CRLF and RFC 4180 quoting. Headers are qualified wire paths owned by
[CaseCsv](../src/ClaimCore.Cli/CaseCsv.fs), [TimelineCsv](../src/ClaimCore.Cli/TimelineCsv.fs) and
[AuthorityHistoryCsv](../src/ClaimCore.Cli/AuthorityHistoryCsv.fs). Empty cells represent absent/erased values.
Every cell starting with a formula trigger (`=`, `+`, `-`, `@`, TAB or CR), including after leading
spaces/TABs, receives an apostrophe before quoting. This alters affected cells; JSON preserves exact values.
`--all` follows cursors and prints the header once. Case CSV adds `workRevision`, `paidAmount`, `paymentCount` and `lastPaymentDate`. Human text visibly
escapes terminal controls and backslashes. Use `--details` to include technical record/review identities and full timeline/conflict change sections.
Summaries, authors and continuation guidance remain visible by default.

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
