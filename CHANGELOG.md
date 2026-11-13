# Changelog

## [Unreleased]

- Fresh installation is required for InstallationFormat 3; stored act identities now use dotted task IDs.
- Replace claimcore-database with claimcore-server subcommands; start hosting with claimcore-server serve. Missing-command usage exits 64.
- Use people.* endpoints and MANAGE_ACCESS/READ_ACCESS capabilities. CLI verbs follow dotted IDs, including case notes and case payments.
- Regenerate clients: rejected replies use rejection, failed replies share fault with messageId, and input issues use input tokens and row ordinals.
- Case, work, reminder and note decisions share execution and replay state; financial updates load the complete owned payment set.
- The browser trusts server-owned payloads while retaining contract identity, operation echo and byte bounds; generated runtime validators are removed.
- One runtime image serves and administers the installation; the human CLI is distributed separately. Compose retains status-then-initialize restart behavior.
- Wire owns CSV columns and serialization; cells starting with TAB after leading spaces now receive the formula-protection prefix.
- Native acceptance and Chromium run in isolated shards. Offline CLI checks run in-process with published smoke coverage.
- Docs-only pull requests run configuration, lint and unit; main pushes, merge groups and manual dispatch keep full verification.
- Project folders follow subject and interface responsibilities; shared command-line types live in ClaimCore.CommandLine. The store port is an internal seam.

## [0.17.0] - 2026-11-13

- Fresh installation is required for InstallationFormat 2, and CLI recovery format 3 requires fresh CLI state.
- PostgreSQL 18.7 or later in major 18 is required; evaluation and test images use 18.7. Contributors and frontend builds require Node 26.13.1.
- Contributors require SDK 11.0.100; framework-dependent CLI installs require the .NET 11 runtime. Compose images now use .NET 11.
- Original project work now uses AGPL-3.0-only; prior MPL grants remain valid. Keep source and notices with deployments; separate commercial terms are pending. See [scope](PROJECT-NOTICE.md).
- Contributions are accepted under the [contribution terms](PROJECT-NOTICE.md#contribution-terms), which also let Ervins Strauhmanis license them commercially.
- The 21 endpoints negotiate HUMAN documents and DATA projections; text and browser clients explicitly select HUMAN.
- Revision-conflict change pages add a required `basis` (observedAt, businessDate, timeZone); regenerate clients. CLI conflict times use the business zone.
- Tasks expose semantic input IDs, choices, issue targets and submitted collection rows.
- Custom stores receive `CasePageQuery.Selection` (a `CaseSelection`) instead of copies of its six fields, matching `ReminderPageQuery`.
- Custom stores use one operation root, ordered composite effect positions and independently erasable salted compartments; receipts commit the complete operation.
- Retain trusted checkpoints outside backups. Restore admission requires isolated traffic and maintenance, verifies accepted history, raises expiry and invalidates restored sessions.
- verify and rebuild use --checkpoint FILE; head emits the installation, accepted prefix/hash and durable expiry frontier.
- initialize owner flags establish the baseline, first human owner, profile and optional roles atomically.
- RECORD_PAYMENT accepts amounts/currency; REMOVE_PAYMENT targets the accepting operation ID.
- CSV exposes paidAmount, paymentCount, lastPaymentDate and workRevision; payment dates and summaries remain exact across paging and replay.
- Configure at least 15 runtime pool connections with default core permits; housekeeping reserves a connection alongside reads, writes, sessions and readiness.
- Country/currency inputs require [Core-supported codes](contracts/codes.ts); reserved/retired countries and non-national/fund currencies refuse.
- Update frontend tooling and locked dependencies, including Vite 8.3.8, Prettier 3.10.1 and native TRX reporting 2.5.3. Refresh the Keycloak 26.8.2 image pin.
- Compose restarts after `down` keep the existing installation: the initializer skips an initialized database instead of blocking Web.
- A separate Compose restart workflow builds the evaluation stack and checks `down`/`up` on deployment changes and weekly.
- Empty case views show one message without an empty page footer; unavailable cases omit retry advice, and optional open-case text fields are directly editable.
- Verb help shows the dry-run usage only where its requirements differ, labelled `With --dry-run:`.
- CLI version and installation mismatches give CLI advice instead of browser reload advice; services no longer probe the missing GSSAPI library at startup.
- CLI mutation text focuses on the outcome and recorded fields; timestamps use the business zone, page-size errors state bounds and sign-in timeout explains retry.
- Receipts distinguish case status, payment progress and voided/erased lifecycle; case summaries and inactive reminder parents use case state labels.
- INVALID_FRAME replies include a bounded reason for malformed JSON, duplicate keys, unknown endpoints, byte limits or schema failures.
- CLI usage reflects execution and dry-run requirements; schema help and missing-name errors list the available exports.
- Closed stdout pipes end discovery and read output quietly; mutation output loss retains exit 4 and recovery guidance.
- Delivered refusals remain in recovery as refused, awaiting final confirmation; only acceptance or final absence clears them.
- CLI help and web notices link licensing and the build’s source archive; modified deployments must update the offered Corresponding Source.
- Final operation absence commits expiry before replying; restored cookies require new sign-in, while bearer and CLI safety also depends on the trusted prefix and frontier.
- ClaimCore.Wire owns transport contracts, and ClaimCore.Presentation owns shared human presentation.
- Domain values and shared operation policy are independent of JSON, storage and presentation. Custom stores supply bounded financial and transactional payment context.
- Payments have no total-50 business limit. case.payments pages current entries, default 20 and maximum 50 per page; case reads retain only the latest three plus totals.
- Decision changes preserve entries and their currency and cover the paid total; withdrawal/clearing requires no entries. Zero payable is Paid without entries.
- maintenance on/off controls the operator pause; inspect status before ending it. Hosts clean expired sessions and access hints.
- Operator failures use closed categories and phase-aware outcomes without raw provider messages. Confirmed work survives cleanup failure; graceful stop drains admitted operations.
- claimcore help supports --format text|json, and help exit-codes documents command outcomes.
- operation.get reports ACCEPTED, PENDING/finalAt and final account-scoped NOT_ACCEPTED.
- work.views supplies combined workspace counts, and case.timeline supplies activity.
- Operation IDs are UUIDv7; first execution expires after ten minutes with two minutes of future allowance. Exact accepted replay is checked before expiry.
- Optional text distinguishes omission, empty text and explicit null; browser profile changes offer separate Record and Clear controls.
- Money updates select amount and currency together; task entry keeps Core meaning and erasure limits visible before review.
- Add optional searchable policy numbers and insurer claim references. Registration amendments use flat amount/currency pairs.
- Add and remove case notes without revisions; current reads page up to 20, workspace shows the latest three, and only FULL history discloses retained text.
- Reassign responsibility from filtered Cases rows with reviewed work versions; people link to active open cases within the viewer's access, including known zero.
- Browser and native text render Core Screens and recommendations; correction groups, assignee captions and authority receipts retain supplied presentation and bounded controls.
- Timeline, payment and conflict continuations discard older disclosure on tightening. Authority actions bind repeated row keys within their issuing page.
- Draft, delivery and disclosure lifetimes are separate: Settings preserve authored input, replaced sessions mask private drafts, and uncertain retries retain exact bytes.
- Accepted case feedback survives same-session read refresh and refetches its receipt; known access loss, actor, installation or session changes invalidate prior feedback.
- Accepted receipts form a named region; embedded Core sections remain groups, keeping current-case and receipt landmarks distinct.
- CLI reviews and history keep technical identities behind --details; full timeline/conflict differences are opt-in. Irreversible confirmation follows the Core consequence.
- Interactive CLI writes read omitted versions and freeze reviewed input; automation requires versions. Offline dry runs validate without inventing versions.
- Shared terminology and supported choices come from Core declarations; interface chrome keeps its own wording. Required authored en, lv and ar catalogs use source hashes.
- Runtime catalogs use English fallback and strong ETags at GET /catalogs/{language}.json. Only contracts are generated catalog outputs; human language and assistive review remain pending.
- The workspace groups facts, paged payment cards, notes and activity beside work and next actions; task drawers use one confirmation and collapsed Record details.
- Verification and rebuild share Domain replay with stored business dates and pre-operation authority; erased compartments retain structural commitments without private contents.
- Reads retain per-principal admission, bounded FIFO waits, cancellation and explicit busy recovery; clients enforce structural admission and operation-ID echoes.
- Unconfirmed changes show pending and final result cards, with links only from disclosed receipts; saved identities never reconstruct or resend requests.
- Contributor checks exercise independent semantic, byte, storage and published-consumer witnesses.
- PR CI uses Linux x64 and Chromium; main, merge groups and manual dispatch also run Linux musl, macOS ARM64, Firefox, WebKit and dependency advisories.
- Verification writes per-stage TRX and uploads CI evidence on failure. Optional timing probes have no performance or source-size gate.
- Failed browser CI retains synthetic traces, screenshots and bounded service logs for three days; authentication-state files and private fixture configuration stay excluded.
- Accepted receipt reads offer an explicit retry after admission pressure or transient network failure, without resubmitting the recorded change.
- CLI note add/remove persist recovery identities; journal validation and request assembly share Core operation subjects and action vocabularies.
- Country and currency pickers use the same authored lists on every browser and rank exact codes, code prefixes, name prefixes, then substrings.
- German, Spanish, Japanese, Korean and both Chinese catalogs are completed; release qualification refuses missing or stale entries in every offered language.
- shell now reports localized usage errors with exit 64 for redirected stdin or automation mode; use session for piped requests.

## [0.16.0] - 2026-11-06

- Recreate databases for authority format 3 and ledger entry format 5; old installations are rejected without migration.
- Supply an authored owner display name during provisioning and a profile when registering people; identity-provider claims do not supply profile names.
- Custom stores must load complete operations and generate fresh payload salts per effect; receipt and checkpoint proofs use the final effect.
- Regenerate work/reminder, assignee, receipt, activity, full case-history and authority-history readers for named ActorRefs; refs omit contacts and reflect current profile erasure.
- Authority list, grant/history and assignee pages require snapshot basis alongside items and cursors; regenerate strict readers.
- Update work review/receipt readers for responsible ActorRefs; authority receipt targets now use Disclosed/Withheld projection under current authority-read rights.
- Regenerate clients for core case headlines, attention and recommended acts; clients must use the offered recommendation instead of ranking tasks.
- Case and reminder state refusals use NOT_OFFERED and REMINDER_NOT_OFFERED; DECISION_REQUIRED is retired. Invalid corrections keep targeted input diagnostics.
- Regenerate consumers for NOT_AUTHORIZED.required and ASK_FOR_ACCESS, CHOOSE_ANOTHER_REFERENCE and REVIEW_LATEST guidance; unreadable targets remain RESOURCE_UNAVAILABLE.
- Regenerate case-list and CSV consumers: state.fields now comes from listed declarations; core work state replaces duplicate nested work and progress columns.
- Regenerate revision-conflict consumers for typed change pages of up to 50 entries; continue via case.timeline under current history permissions.
- Regenerate timeline and conflict readers for disclosure-scoped event overviews and bounded highlight references; full differences remain available without duplicating large payloads.
- Schema validators must enforce x-claimcore-maxUtf8Bytes as part of the wire profile; standard maxLength alone does not preserve permanent UTF-8 limits.
- Regenerate preview consumers for required review.presentation and retained money displayScale; CASE subjects replace raw string changes with typed core presentation.
- Regenerate catalogs for hintId/ruleId, GUI diagnostics, consequenceId/doneId and role meaningId; field meaning and checkbox confirmation identities are removed.
- Receipt basis and runtime observedAt describe the current server snapshot; recordedAt remains the accepted event time, including on replay and later checks.
- Replace CLI case history and case activity with case timeline; typed changes, actor names and canonical CSV support bounded or complete live traversal.
- Actor-directory search matches current names or contacts plus typed role filters; discard old actor cursors. Profile erasure clears both private search indexes.
- Discard case-list cursors: format 6 binds the selected core view. Standard views intersect with explicit filters and preserve read permissions.
- Protected Application calls check requester availability before input refusals; malformed calls can need storage. Offline CLI syntax checks remain local.
- Application case validation combines syntax and business-date issues against an authoritative service snapshot; Domain parsing stays storage-free.
- Reminder changes require positive expected revisions; zero returns input diagnostics instead of an encoding error. Initial work updates and case opening still accept zero.
- Irreversible human CLI commands require an exact typed target or matching --confirm; dry-run and structured protocol calls retain their wire contracts.
- Regenerate work readers and custom stores for summaryChangedAt/summaryRecordedBy; responsibility-only edits preserve the summary's actual author and time.
- Browser settings use claimcore.presentation.v3 and ignore v2; choose preferences again in Settings or the sign-in footer dialog.
- Incomplete dates survive Settings and language changes as private draft segments; canonical dates and prepared request bytes stay separate.
- Set or erase actor profiles separately from identity and grants; historical names use current profiles, while erasure preserves acceptance but can prevent exact replay.
- Case and reminder view counts share a snapshot; case timelines combine fact, work and reminder changes under current history permissions.
- ADMIT records an authored profile and up to 48 role assignments atomically; access requests distinguish human and service identities and clear on admission.
- Sign-in distinguishes unregistered, roleless and disabled accounts; owners can review unverified request hints and bind admission to the exact account.
- People show bounded role summaries and explicit assignment state; optional responsibility counts and links use the viewer's case-read access and preserve known zero.
- Unavailable case actions carry current core reasons; case trackers use recorded dates and keep absent financial stages unrecorded after closure.
- Web task drawers use one routine confirmation button; reminder completion stays beside its row, and erasure still requires the exact reference.
- Activity details stay expanded across Settings visits; case action groups follow the page heading outline.
- Use Manual filters in Reminders to include inactive work; standard views keep their own selection rules.
- Case, work, reminder and timeline text use typed core descriptors; case tables adapt to width without truncating money, and retained actor names are safely escaped.
- Work overview attention includes due-today and ready-to-close cases, matching core case views.
- Case lists identify exact visible reference matches using installation case-folding and expose the next reminder identity; unavailable cases remain undisclosed.
- CLI adds family/verb help and --version, rejects irrelevant clear flags, names escaped usage tokens and reports declined reviews.
- CLI language follows explicit flag, environment, profile and POSIX locale precedence; configure --show reports the effective choice and source.
- CLI --dry-run rejects malformed writes locally with exit 2 and ordered input issues; structured refusals retain the existing frame shape with NOT_SENT delivery.
- CLI money keeps meaningful precision and adds ISO minor units for known currencies; canonical JSON and CSV amounts stay unchanged.
- CLI human values use explicit regional cultures, including Latin-digit ar-AE, so money, dates and observation footers agree across supported platforms.
- Green and blue accents support light, dark and device modes, stronger control boundaries, OS contrast preferences and forced colours.
- First visits negotiate supported browser languages and regional formats; add en-US, ar-AE, de-DE and fr-FR display locales.
- Contributors can diagnose selected browser specs without replaying full suites; diagnostic batches collect failures but never qualify acceptance. Reuse applicable CI evidence.
- Published-client fixtures drain Web logs for their full lifetime and retain bounded tails; capture metadata identifies truncation and incomplete cleanup.
- Secondary-principal browser specs obtain real OIDC sessions at file setup, preserving authentication expiry and login admission during long suites.
- Rate-limited host responses include Retry-After: 1 without increasing authentication or admission capacity.
- CLI and web add draft Korean, Japanese, Spanish, German and Simplified/Traditional Chinese translations; Chinese selection preserves script and regional defaults.
- Choose regional date/number formats independently in the web; CLI dates use Gregorian conventions for the selected language and retain exact money values.
- Long pickers keep focused options in view instead of scrolling the page and closing before a choice can be made.
- Contributor browser and published CLI checks cover each language and interface risk without multiplying independent language, command, theme and viewport choices.
- Native units build their Debug closure; CI runs complete non-browser published acceptance once and qualifies each browser engine separately.
- Recovery guidance distinguishes unresolved status, exact in-memory retry and account-scoped permanent settlement; an existing acceptance returns its receipt.
- Correct payment acknowledgement choices, ordering directions, active/open counts and localized operator faults, milestone labels and name interpolation across catalogs.
- Case lists retain summary integrity failures and reuse validated visible exact matches; outside-page lookup and disclosure checks remain.
- Reuse immutable CLI cultures and bounded browser locale resources while preserving Gregorian dates, exact amounts and captured input formats.
- Pointer presses begun on disabled buttons, including those disabled by a fieldset, cannot trigger a delayed submission after reenabling.
- Source tooling uses npm 12.2.2, jsdom 31 and updated lint/build packages; restore locked dependencies and install the pinned Playwright browsers.
- Evaluation deployments use Keycloak 26.8.2 security fixes and refreshed pinned base images; pull/rebuild the declared Compose inputs.

## [0.15.0] - 2026-11-03

- Use a fresh database with pg_trgm 1.6: case request/snapshot/ledger format 4 and work/reminder format 1 replace the baseline. Authority/Control stay at 2; old installs refuse; no migration.
- Build with Node 26.13.0 and npm 12.2.1; source toolchains, dependencies and pinned CI/container inputs are refreshed.
- Next follow-up is derived from Pending reminders; remove followUpDate writes. Work and each reminder have independent revisions, so scheduling does not invalidate financial edits.
- Regenerate clients for WORK/REMINDER operations, entity-specific revisions, bounded pages and correlated effects. Complete-and-plan-next commits exactly two effects under one operation ID.
- Reminder get/list replies add server-derived timing; regenerate strict clients. Both clients label active Pending due buckets and inactive work without reclassifying historical receipts.
- New strict identity-only recovery journals refuse old formats without deletion or conversion. In-memory reviewed requests retain exact retry bytes; restart recovery never reconstructs business text.
- Regenerate consumers for tagged reference/ID targets, stable CaseIds, resolved grant scopes, history differences, core progress and correlated operation replies.
- Bind execute/resolve to authenticated InstallationId with ClaimCore-Installation. Stale prepared requests cannot adopt a replacement installation.
- Discard old cursors: case lists use format 5; assignee, reminder and activity pages use 1; other pages use 2. All bind installation/scope; traversal and --all exports remain live.
- Web case links use /cases/id/CASE-ID; exact reference links use /cases/reference/REFERENCE. Replace bookmarks using the former unqualified reference path.
- Remove ASSIGNED_TO_ME filters and rename context hasAssignedCases to hasScopedCases. MINE now requires explicit responsibility; scoped read grants provide visibility only.
- Regenerate clients for typed partial registration/decision UPDATE payloads, complete money pairs, explicit payment REAFFIRM, and independent case-work fields.
- Replace CLI operation resolve with operation status for passive checks or explicit operation settle for permanent nonacceptance; interactive settlement requires confirmation.
- Regenerate authority previews and catalog consumers for scoped capability-source explanations and separate role/capability scope and principal-kind admission.
- Update source callers for Application.Contracts, typed business groups and complete Payment values. Historical verification checks retained request effects independently of current admission policy.
- Web admission separates principal/read/write/passive-recovery budgets; the write queue maximum is eight. Enable pooling with at least 16 connections at defaults; limits apply per process.
- Browser reads retain admission through bounded response draining after a view becomes obsolete, reducing busy responses during rapid navigation without resending writes.
- Rebuild requires explicit maintenance and a trusted checkpoint. Use owner status and generation-bound enter/resume; register/control writes pause while reads and authentication continue.
- Case-list text and CSV add responsibility, follow-up, due state and its business-date/zone basis. Update consumers of the qualified CSV header; erased work cells remain empty.
- Publish the full web build with its verified asset inventory; restart for new bytes. Only verified public assets use immutable caching; APIs and HTML remain no-store.
- Load views and complete language pairs on demand. Failures preserve working text, direction and authored values; explicit reload guards drafts and retains recovery identity.
- Preserve operation drafts across equivalent session refreshes and isolate receipt feedback and delayed completions by requester and session generation.
- Owner prune-sessions deletes a bounded expired batch with row locking and deadline rechecks; live sessions, business history, recovery fences and keys are retained.
- CASE_HANDLER humans manage responsibility, Handling summary and reminders. Targets need current case-read/work rights; assignment grants no access and service financial rights stay intact.
- Work pages, queues and overview report scoped counts and business-date/zone observations. Closing/voiding retains inactive reminders; erasure clears related current and historical payloads.
- Partial reviews show selected facts and exact amounts/dates; chronology issues identify the asserted field and its retained counterpart. Amount entry keeps its original display-locale meaning.
- Catalogs add act family, affected sections, effect and wire-required input metadata; clients rank tasks explicitly and never select erasure as a default.
- Actor-scoped operation.status is an installation-bound read for valid roleless actors too; unresolved reads create no fence and foreign actors’ operation UUIDs remain undisclosed.
- Fence guards validate retained control-node identity/hash and projection timestamps; missing, fabricated or contradictory evidence fails integrity instead of creating a false final answer.
- The web Recovery area separates status checks, confirmed permanent settlement and general receipt lookup; cleanup failure retains identity and shows the known result.
- New case-scoped OWNER grants refuse as meaningless; owners can still revoke recognized obsolete grants. Disabled and overlapping meaningful grants remain allowed.
- CLI human text renders narrative paragraphs with escaped data segments and inert terminal controls. JSON/NDJSON remain exact and CSV keeps its export rules.
- Clients persist identity-only attempt journals before mutation dispatch. Storage failure blocks sending; restart recovery checks final outcomes without resubmitting business data.
- Recovery checks preserve ownership across same-identity refreshes and actor/epoch changes; stale completions cannot release newer checks or clear another scope’s records.
- Use CLI recovery list/check/settle with text or JSON output; retain exact retry frames and their installation binding until uncertainty is settled.
- Final nonacceptance is an ordered actor/operation control. Verification and trusted-checkpoint rebuild restore fences, live projections and erased identity without reconstructing erased content.
- Owners can rebuild projections with a trusted checkpoint and explicitly invalidate restored sessions. Keep actor traffic isolated during restore; older backups can resurrect data and cookies.
- Typed registration constructors and retained-state decoding reject incident dates after notification; input groups and snapshots must obey chronology.
- Core supplies progress and review/history differences; clients retain typed requests through preparation, review and pagination and reject mismatched reply identities.
- Storage diagnostics retain bounded safe failure context. Provider cleanup and throwing diagnostic observers preserve confirmed results and conservative commit uncertainty.
- Contributor checks allow identical shapes and wording and remove mechanical size targets; actual ownership, dependency, byte, security and complete-test controls remain enforced.
- CI retains bounded native, frontend and browser failure reports for 14 days; private fixture credentials and keys are excluded.
- Patch transitive tooling matcher/traversal dependencies; import-boundary lint loads the configured upstream runtime rules without disabling the audit or boundary checks.
- Use the pinned Keycloak 26.8.1-2 vendor image rebuild for local deployment and identity qualification.
- Work in a redesigned Cases, Recovery and Access workspace with grouped queue facts, task-specific editing, before/after review, case-work cards and accessible preferences.
- Incident description and Handling summary are optional LF plain text, each up to 600 Unicode scalars. Omission preserves, explicit clear removes; action text is single-line, up to 200 scalars.
- Complete, cancel and explicitly reopen reminders without rewriting prior history. Completing the last Pending action is valid and exposes missing-plan attention.
- Accessible work attention ignores case page/search filters, distinguishes case/reminder units and counts overlapping attention once. Mine inherits current case responsibility within read access.
- GIN indexes support literal search across case facts and Handling summaries; account for their database storage and write-ahead-log overhead.
- Growth reports retain raw latency samples and live progress; failed native runs and bounded private publication diagnostics survive cleanup.
- Source releases reuse successful main-branch CI for the exact annotated commit; publishing a tag does not repeat the gates.

## [0.14.0] - 2026-10-30

- Use a fresh database: notification/amount projections and authority-history indexes change the SQL checksum. Earlier installations are refused; no migration or reader is supplied.
- Regenerate catalog, client and OpenAPI consumers; bindings name vocabularies/advice groups and add CHOICES for repeatable worklist facets.
- Context adds actorId/hasAssignedCases, case views add assignedToYou, and catalog roles declare principal-kind admission; regenerate consumers for these required fields.
- Discard earlier case-list cursors; format 3 binds canonical filters, assignment and notification order. Other existing cursor formats remain unchanged.
- CSV exports prefix formula-leading cells with an apostrophe before quoting; use JSON for exact authored values.
- CLI public settings load from a private Unix profile with per-value environment overrides; unknown members are refused. Use configure --show to inspect sources.
- Stored browser preferences reset; version 2 adds independent SYSTEM, LIGHT and DARK themes.
- Update source callers for typed IDs/positions, complete field groups, grouped refusals, act descriptions and endpoint reply classification; Schema interpreters handle Vocabulary annotations.
- Handle new CASE_STATUS_PROJECTION, CASE_PROGRESS_PROJECTION, CASE_CHANGED_AT_PROJECTION and CASE_SEARCH_PROJECTION verification findings.
- Audit authority changes with installation OWNER or AUDITOR rights using authority.history or the CLI, including typed facts and target-filtered newest-first pages.
- Select assigned cases and oldest notifications; case.summary counts the current visible filter and returns exact per-currency totals beyond single-amount precision.
- Erasure clears notification dates and all amount/currency projections; verification identifies divergence in each new column.
- Local evaluation setup creates an isolated Firefox CA profile when certutil is available and writes ready public CLI settings; installed Firefox is the documented default.
- Run reviewed commands in claimcore shell with one sign-in; access/refresh tokens and arrow-key history stay in memory, with 15-minute idle discard and no history file.
- Vocabulary token plans reduce repeated work in case writes and ledger verification while retaining permanent bytes and integrity checks.
- growth-compare compares three alternating same-host pairs and fails relative throughput, latency or verification regressions using workload fingerprints.
- Run optional full growth comparisons on a fresh CI runner; paired workload reports are retained as a downloadable artifact.
- Use focused checks during development; change probes and growth runs require a concrete question or explicit acceptance scope, while implementation PRs retain full product verification.
- Musl CI installs pinned Python required by native comparison controls.
- Positive test inputs use inventory builders; local golden updates refuse CI and permanent vectors, and one transition oracle supplies semantic expectations.
- Case history now pages newest revisions first; follow nextCursor for older changes. Cursor bytes and shape remain unchanged.
- Filter case lists by lifecycle, status, progress and normalized reference/claimant/insurer substrings; order by reference or latest accepted change.
- Case-list rows include claimant, insurer, canonical claimed/payable amounts, notification date, progress and last-change time; erased rows expose only retained metadata.
- Case lists hide cases with corrupt case-scoped human-only grants to the querying service principal, even with installation-wide read access, matching case.get.
- Export case lists as RFC 4180 CSV; --all follows per-page snapshots and stops with the reached cursor on refusal.
- Terminal text verbs can fetch an omitted revision, preview and ask before execution; automation still supplies a revision and omitted correction groups default to KEEP interactively.
- Browser routes preserve worklist queries, deep links and sign-in return; reserved references use reversible prefixed escaping.
- Browser inputs use locale dates/amounts and currency/country suggestions; linked error summaries focus first, and the server remains authoritative.
- The responsive work surface adds saved worklists, grouped facts, progress/history and contextual case/authority actions with distinct primary actions in both themes and en/lv/ar.
- Compact case rows open as links; facts share group help, exact amounts retain all digits, and localized history shortens actor/operation IDs with exact details.
- Authority grants require an explicit role and disable inadmissible principal kinds; desktop actor tables and compact mobile cards show known grant counts and open detail.
- Unknown routes stay offline on a localized not-found page.
- Accepted browser case changes return to the current case with an act/revision notice; uncertain delivery retains exact retry and final-resolution controls.
- Authority actor actions bind the selected principal; retained requests need explicit discard consent before a fresh request can target another actor.
- Stored act, role and principal kinds use permanent decoders; unknown tokens fail reads and diverge verification even when SQL accepts their shape.
- Erasure also clears searchable text and status/progress projections while retaining reference, revision, last-change time and ledger metadata.
- One ClaimCore meter exposes bounded request, ledger and session observations; verification replies include elapsedMilliseconds and administrative heads support genesis.
- Field/act declarations drive shared processing; private constructors enforce complete typed groups while permanent ledger encodings stay unchanged.
- Browser forms use catalog vocabulary, correction advice and selected-entry constants; core labels remain owned by their declared vocabularies.
- Tests share a hand-authored independent inventory and one input-builder owner per language; seed-corpus failures name their shared cause without skipping properties.
- Protocol source paths follow Wire, Api, Catalog, Messages, Presentation and CommandLine responsibilities.
- Total exhaustive tables may justify complexity suppressions; rule bodies and product/test code retain the existing lint limits and controls.
- Built-in lint severities inherit enabled categories; explicit exceptions, configured rules and JavaScript-plugin rules remain in place.

## [0.13.0] - 2026-10-28

- Initialize a fresh installation; the SQL baseline checksum changes. Earlier installations are refused untouched; preserve them with matching software. No migration or reader is supplied.
- Deploy matching service, CLI and browser builds; wire tokens, input diagnostics and locale keys change. Regenerate integrations from the checked contracts.
- Input refusals carry a nonempty INVALID_INPUT.issues array; each issue has field, code and declared parameters. Replace single-target scalar diagnostics in integrations.
- Technical-target locale keys use field.*; history detail uses input.detail.*. Replace token.* keys with vocabulary-qualified labels and typed terms; renderer catalogs cannot copy core wording.
- Database HELP/VERSION replies use kind tags; help exitCodes lists code/number entries. Update consumers of earlier machine replies.
- Database USAGE_ERROR carries a typed issue and applicable syntax; update machine consumers. Both tools share help/version models and declare supported exits locally.
- Build with .NET SDK 10.0.403 and deploy .NET/ASP.NET Core 10.0.14; production JWT/configuration transitives and deployment images track the security patch train.
- Independent input errors accumulate within parsing and admission phases. CLI prints each issue; browser forms show all field errors, focus the first and report the count.
- Principal kinds and roles stay typed after decoding. Unknown tokens are protocol faults; malformed stored kinds or roles produce integrity faults instead of input refusals.
- Actor replies use admitted principal identities with canonical HTTPS issuers. Authored identity fields remain separate from trusted output models.
- Case advice lists act kinds with conditional correction choices; offers omit expectedRevision. Use the snapshot revision, or zero when opening.
- Catalog inputs, paths and branch conditions derive from request codecs. Generation refuses missing/orphan annotations and undeclared branch tokens; CLI owns command syntax.
- Source integrations use Domain outcomes, typed amendments/act slots, shared grants/actors and EntryHash positions; Protocol owns endpoint, catalog and command-line models.
- Registration amendments expose KEEP/REPLACE; CLEAR remains a wire decode error. PaymentProgress prevents payment without a decision. Retained encodings and format numbers stay unchanged.
- Other refusals and faults retain flat code-tagged parameters; actualRevision is specific to revision conflicts. One checked diagnostic registry supplies direct message-key lookup.
- Authority admission belongs to Domain. Service and named ports replace Application routes and handlers; CLI authority input uses --principal-kind.
- Client reply correlation checks are removed; clients trust schema-admitted HTTPS replies. Retain exact frames for resubmission or final resolution after uncertain delivery.
- Execute OUTCOME_UNKNOWN remains distinct from known failure with exit 4 and its recovery guidance. Existing success tags and numeric exits remain stable.
- Remove unproduced REPLAY_EXACT/REAUTHENTICATE actions and SESSION_FORBIDDEN host code; credential failures retain SESSION_REJECTED with HTTP 401.
- Remove REVISION_EXHAUSTED and LEDGER_SEQUENCE_EXHAUSTED; endpoint locale keys replace label.read.<endpoint>.*. Country metadata publishes unknownCode.
- RecordFormat owns literal durable names and record-byte limits independently of wire policy; permanent scalar grammars only relax within a format.
- Compiled controls cover core ownership, unused/duplicate declarations, typed vocabularies/messages and codec/catalog agreement. Each added gate has a prohibited control.
- F# and TypeScript product/test files have 500-line limits. Functions allow five parameters and F# tuples four items; complexity remains capped at 12 without new suppressions.
- Remove unused .NET and browser coverage tooling. Locked dependency audits, meaningful properties, handwritten vectors and complete stage discovery remain required.
- Browser production builds compile app sources and omit OpenAPI from the bundle. Browser unit paths mirror source; lint policy is split by responsibility.

## [0.12.0] - 2026-10-26

- Initialize a separate installation; the new database baseline refuses earlier installations untouched. Preserve them with matching software; no conversion is supplied.
- Deploy matching service, CLI and browser: shared operation preview/execution and renamed acts replace the preceding wire contract. Regenerate integrations from the checked OpenAPI.
- Tagged unions use `kind`; receipts and reviews use `subject`. Cases expose ACTIVE, VOIDED or ERASED state with grouped registration, decision and payment fields.
- Durable records have independent codecs and format 1. Database administration replies use `kind`, `entryHash` and `REJECTED`.
- Executables are `claimcore`, `claimcore-server` and `claimcore-database`. CLI verbs provide localized usage errors, preview and bounded offline preparation.
- Operation identity compares amount values and ASCII-folded case references while retaining authored requests; free text remains exact.
- `operation.resolve` returns the author’s receipt or permanently fences an unaccepted ID. Exit 4 retains the exact retry frame; ordinary failure leaves prior uncertainty unresolved.
- A codec-derived operation catalog supplies CLI syntax, browser bindings and the generated [CLI reference][cli-reference-0.12]; generated contracts replace the preceding artifact inventory.
- Core, browser and CLI own separate locale catalogs using one Template grammar. Typed arguments, bidi isolation and literal-only pseudolocalization support presentation.
- Typed endpoint handlers, field-owned scalar rules and one eligibility table align server, client and persistence behavior while preserving refusal precedence.
- Four test environments share support code; independent byte fixtures, compiled architecture checks and maintained browser boundary rules cover the shared contracts.
- Refresh pinned dependencies and deployment images, including Keycloak 26.8.1-1.

## [0.11.0] - 2026-10-21

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.11.0).

- .NET runtime security updates, managed private-input admission, and persistent protected Web sessions with explicit key-ring custody.
- Closed diagnostics and targeted malformed-business-input rejections; current navigation/advice and bounded history/grant pages.
- Coupled consumer-contract identity and a fresh database baseline; earlier installations remain preserved with matching software.
- Duende OAuth/OIDC clients, offline localized CLI help, and strict loopback callback handling.
- Conservative CLI output-loss handling, bounded readiness and explicit browser recovery preserve delivery uncertainty.
- Managed build/process controls replace native components; framework-dependent publication retains reviewed redistribution notices.
- Owned Compose readiness, private path handling and identity memory budgets improve evaluation setup.
- Closed domain types, pure verification folds, independent contract/record checks and retained numeric growth evidence strengthen verification.

## [0.10.0] - 2026-10-17

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.10.0).

- Owner-private file admission checks ownership, modes and ACLs on the qualified platforms.
- A coupled twelve-endpoint client/host contract separates principal syntax from authoritative identity admission.
- Case listing and bounded actor/grant views authorize independently and preserve snapshot scope.
- Generated browser authoring and complete decoders cover business, stewardship and authority work; preferences retain only their declared fields.
- PostgreSQL verification, project closures and bounded fixture/process cleanup gain dedicated controls; native publication prerequisites are documented in the release notes.

## [0.9.0] - 2026-10-15

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.9.0).

- One PostgreSQL authority and accepted ledger replace the primary/witness model; incompatible installations are refused untouched.
- A coupled eleven-endpoint contract and versioned RFC 8785 records define case work and exact bounded wire values.
- Current authority controls snapshot disclosure; preview remains advisory and execution rechecks state under the ledger lock.
- Case references retain accepted spelling, service principals need explicit registration/grants, and historical accepted values remain readable.
- VOID, REINSTATE and ERASE become accepted case acts, with live erasure limits stated explicitly.
- Build and verification stages are simplified; product-managed witness, copy-custody and backup/restore orchestration are removed.

## [0.8.0] - 2026-10-08

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.8.0).

- Dependency auditing retires the patched build-tool advisory exception and refreshes pinned runtimes and providers.
- Docker contexts exclude private/generated material, while publication retains package-specific redistribution notices.
- Witness schema and asynchronous native interfaces change; matching deployments and fresh compatible baselines are required.
- Exact draft comparison, independent preference changes and delayed-result handling preserve authored browser state.
- Database and audit work honor cancellation; timestamp validation, runtime diagnostics and bounded evidence handling are tightened.
- Operating guidance covers public origins, separate listeners, private macOS paths and complete deployment qualification.

## [0.7.0] - 2026-10-04

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.7.0).

- Original work and incoming contributions move to MPL-2.0; covered redistribution requires corresponding source information.
- TLS revocation, public-only trust roots, private-file Unicode handling and platform admission receive security hardening.
- Raw Ed25519 key comparison enforces signing-role separation; backup and lifecycle approval controls bind current authority and time.
- Session deadlines and conservative CLI cancellation/output handling preserve unresolved mutation outcomes.
- Browser recovery and stale-read handling preserve the intended case, current disclosure and authored values.
- Bounded frames, typed admission phases and Unicode warnings make input/delivery behavior explicit.
- Independent oracles, complete report discovery and owned restore/overload controls strengthen qualification; detailed historical advisory exceptions remain in the published notes.

## [0.6.0] - 2026-10-02

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.6.0).

- A separately credentialed PostgreSQL witness records acceptance, attempts, grants, lifecycle transitions and owner authority.
- Actor-bound authorization, void/reinstatement holds and staged privacy erasure govern case work.
- Signed copy custody, encrypted captures, isolated restore qualification and two-owner terminal retirement support the then-current operating model.
- Fresh schema and client/native contracts replace earlier baselines and unbound facades; recovery artifacts become authenticated and installation-bound.
- Authority, clock and TLS checks cover runtime opening, delayed work and audits.
- Contributor tooling, compiled boundaries, source/publication evidence and strict report qualification are expanded.

## [0.5.0] - 2026-09-23

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.5.0).

- English, Latvian and Arabic browser text, RTL layout and independent display formats preserve drafts and prepared work.
- Initialization binds one schema, installation identity and immutable business time zone; earlier migration and historical import paths are removed.
- Versioned canonical requests, recovery envelopes and closed diagnostic identities require matching clients and exact uncertain retries.
- Explicit project dependencies and scalar-aware identity strengthen contributor and integration boundaries.
- CLI/database delivery handling, typed HTTP failures and targeted correction guidance preserve known outcomes through failures.

## [0.4.0] - 2026-09-21

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.4.0).

- Installation-calendar guidance requires consistent host time-zone data and canonical IANA identifiers.
- Compiled component boundaries and strict decoding/property controls cover protocol and recovery surfaces.
- Hosting and CLI protocol responsibilities are separated, and pinned toolchains/publication trees are documented.
- Canonical identity and workflow policy receive preventive hardening; GitHub release notes avoid duplicate headings.

## [0.3.0] - 2026-09-20

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.3.0).

- One-step factual correction covers registration, decision and payment facts while retaining the thirteen-field register.
- An installation-wide business time zone and safer bounded recovery clarify unfinished work.
- Coupled client contracts, fresh identities after editing and durable revocation prevent stale work from changing a case.

## [0.2.0] - 2026-09-14

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.2.0).

- Backup/restore guidance requires reconciliation of accepted work newer than a restored checkpoint.
- Replaying accepted work returns the original receipt without prior recovery details.
- Conflicting retries preserve private receipt/recovery information and do not repeat the accepted act.

## [0.1.0] - 2026-09-13

[Published release notes](https://github.com/resoltico/claimcore/releases/tag/v0.1.0).

- First release.

[cli-reference-0.12]: https://github.com/resoltico/claimcore/blob/v0.12.0/docs/cli.md
