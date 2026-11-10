# Foundations design adopted 2026-11-09

This is the historical owner-adopted design record, reproduced from `01-decisions.md` in
`claimcore-foundations-2026-11-09.tar.gz` (SHA-256
`89a8b69c0219bbbcb009278668b98a7808dc64eb2b17c1f1e4fe2818881c3133`).
The archive and its manifest were verified separately from implementation verification.

For current supported behavior, use [Integrating](../integrating.md), [Operating](../operating.md),
[Case work](../case-work.md) and [Contributing](../contributing.md). Those guides link to the owning
contracts and source. This record does not establish that a build, test or deployment has passed.

## Adoption authority

All D01–D14 were adopted by the owner on 2026-11-09. Details below resolve the remaining calls and the separate QA; they are not questions for an implementer to re-decide. Investigate real contradictions and correct methods within scope, preserving explicit guarantees. See the focused contracts and package completion conditions.

### D01 — Separate facts, query policy and presentation metadata

Domain owns typed business identity, input value kind, admissibility, optionality, semantic groups and paired money. Application owns use-case/query selection meaning. Presentation owns display order, prominence, listing, task presentation, labels and editable-input presentation. Keep one typed `BusinessField` identity referenced by those owners; do not repeat scalar bounds or validation rules in their metadata.

Remove `Prominence`/`Listed` from Domain field descriptions and relocate `Searchable` to the query owner. A table-layout edit must not change Domain. Presentation does not become an input-validation dependency. Closed metadata mappings must cover supported facts deliberately, not gain silent wildcard fallbacks. Keep the modular monolith and useful assembly purity/transport/privacy boundaries; do not merge projects merely to reduce their count.

### D02 — Semantic tasks drive interaction; transport schemas describe transport

Use finite typed task/input/choice/group/collection declarations referencing Domain inputs. Presentation owns their human metadata; Wire binds each task exhaustively to its typed canonical input and endpoint. Generate explicit typed TypeScript assemblers and native assembly/discovery from those owners. Delete runtime JSON-schema walking for UX, generic dotted-JSON-path assignment, branch-condition interpretation and duplicate client refusal-path interpretation.

There is no generalized workflow/expression language, server preview-ticket store or runtime schema service. A `TaskId`, `TaskInputId`, and collection-row identity are semantic identities, not transport paths. Preserve OMIT/KEEP, explicit CLEAR/null and authored SET (including empty invalid text) as distinct states. Money pairs are selected/cleared atomically. Core HUMAN issues target semantic input IDs and, for collection errors, typed collection ID plus submitted row ordinal; DATA issues retain machine field targets. Clients retain an immutable submitted ordinal-to-local-row-ID map for response correlation, excluded from business bytes/identity. UI-only unfiltered sentinels remain UI-owned. Prove registration amendment, payment and authority admission before extending all tasks.

### D03 — One human resource document; explicit machine projection

Keep existing actor endpoint identities, but negotiate response representation uniformly at the transport boundary. `ClaimCore-Representation: HUMAN|DATA`, case-insensitive header values normalized to the closed vocabulary. Missing header selects DATA for raw callers; unknown values are a sanitized transport refusal, never fallback. Browser and CLI text explicitly request HUMAN; JSON/CSV/call/session automation explicitly request DATA. Host session/logout, health and public catalog routes keep their own host contracts; negotiation applies to actor API endpoints.

Successful/refused/failed actor API envelopes explicitly identify the selected representation. HUMAN carries only core display (`Screen`/review document), bounded typed interaction context, observation basis and disclosure. Context includes permitted IDs/revisions, offered task handles and necessary editable input seeds, not a renamed machine DTO. DATA carries typed machine results with no Screen. Both are projected from one authorized use-case result and one transaction/query snapshot. Do not duplicate endpoints or queries merely to produce two views.

Negotiation is excluded from business input, canonical command identity, cursors and ledger bytes. Changing representation cannot create a second operation or idempotency conflict. In-process transport cache/coalescing keys MUST include representation (or statically separate channels); excluding it from business identity does not mean merging transport requests. The selected representation is correlated/validated in replies; operation-ID echo, contract/installation binding, strict bytes and disclosure checks remain. Apply disclosure before BOTH display and context/seeds. Core-authored regions include semantic group identity where needed; do not select a fact group by translated title/catalog key. Seven content kinds and sixteen roles remain useful presentation vocabulary; evolve their fields only as the contract needs.

All human reads, receipts, lookup outcomes, conflicts and review changes consume this contract. Fix authority acceptance, correction-current display and assignee captions through it. Editable prefill and machine export may use their explicitly typed input/data projections. Clients do not rebuild read-only facts, financial totals, stage/attention policy or shared outcome meaning.

### D04 — Shared vocabulary and terminology have core identities

Domain owns machine tokens. Presentation owns shared business wording and necessary short/grammatical variants. CLI/web own syntax, navigation, local interaction and transport/recovery instructions. Reuse core message identities for the SAME concept; do not move all UI prose into Domain or merge distinct meanings because text happens to match.

Remove independent business-label copies and client-maintained supported business-choice lists. Correct CLI “handling notes” to the distinct handling-summary concept; append-only case notes remain separate. Core owns consequence classification; CLI only chooses confirmation target/prompt. Presentation remains the one source-hash fallback implementation; required authored en/lv/ar, optional languages use declared English fallback. Existing glossary/style checks are editorial controls, not proof of semantic identity. No universal word-equality registry or new scan gate.

### D05 — One operation policy, reused with the correct state

Use small pure operation-specific policy functions over explicit loaded facts for availability/refusal, actual admission and replay. Preserve necessary distinction between input-independent offers and input-dependent refusal: a full collection/action may be unoffered while a submitted invalid request still gets its declared diagnostic. Authorization is rechecked using execution's actual state; replay uses pre-operation as-of authority. Do not remove boundary checks because an earlier screen offered the action.

Do not build a general policy framework. Keep client-specific emphasis among offered acts local unless the core declares a shared recommendation. SQL remains a query adapter; derive shared role/token constants and retain independent Domain-versus-real-query witnesses for time-dependent filters rather than adding a general query DSL.

### D06 — A private financial position; derived progress

Replace public progress tags carrying overlapping financial facts with a private, validated financial position. It contains decision, exact paid total, active-entry count and required payment date facts; progress and remaining are derived. A selected entry and store-provided remaining extrema are bounded decision context, not UI data. Domain alone decides accepted mutations and financial effects. Preserve exact decimal/currency semantics and zero-payable-is-paid behavior. Do not cache an independently authoritative `PARTLY_PAID` tag.

### D07 — Payment entries are paged; no total-50 business limit

Remove the lifetime/current-entry count cap of 50. Keep payment READ page limit 50, default 20, and show the latest three plus total count in the case human document, with paged expansion. Introduce `case.payments` within the existing API pattern; this makes 21 HTTP endpoints. Payment writes remain CASE RECORD_PAYMENT/REMOVE_PAYMENT with case expected revision, active/open rules, Payment recorder capability, positive amount, same currency and paid≤payable.

Indexed payment projections retain accepting operation UUID as payment identity and removal state. Case views, receipts and durable operation payloads never embed all payments. Decision/correction admission receives exact paid/count/currency/earliest/latest facts; removal receives the selected active entry plus remaining earliest/latest computed in the SAME locked transaction. Remove-only-if-recorded-in-error wording remains. Removing/recording never changes the historical uniqueness of operation/payment IDs. Parent erasure clears amounts/dates and related private histories. CSV retains paidAmount/paymentCount/lastPaymentDate. Counts remain within the supported exact-integer range; reaching that technical range is explicit refusal, not overflow.

### D08 — One ledger envelope per accepted operation

One sequence/hash row represents one accepted command, with ordered typed effects and separately salted, owner-scoped private compartments. Stable `EffectId=(operationId,effectIndex)` identifies provenance; `(operationSequence,effectIndex)` orders effects. Header/root is one operation, not a physical row per authority effect. Replace contiguous-root/group/index/completeness machinery, not merely wrap it under new names.

Root framing commits public command skeleton, ordered public effect descriptors, all compartment descriptors/commitments, actor/operation/install identities, format, sequence/prior hash, recorded instant and stored business date when applicable. Compartment commitment framing binds owner kind+ID, purpose, ordinal/effect references, nonce and canonical bytes. Erasure removes bytes AND nonce, keeps blinded commitment/descriptor. No unsalted private-content/request hash or duplicate monolithic raw request may survive elsewhere.

Owner provisioning/admission remains one transaction. A mixed authority command must retain registration/grants while profile bytes can be erased independently. All command-level authorization uses the pre-operation authority state; effects are then applied in declared order. Public shape policy and private payload admission share a typed operation model. Case-private effects, amounts, dates, work, notes and narratives belong in case-owned compartments. Profile content belongs in actor-profile compartments. Retained principal/grant/header metadata remains within the existing documented erasure trust model.

### D09 — One prepared-command/content pipeline; semantic replay remains independent

Phases: strict structural admission; load/authorize without disclosing protected targets; prepare syntactically complete canonical intent once; exact accepted replay when comparison is possible; revision and operation-lifetime/frontier checks; current state/date/input admission; pure decision; atomic append/projections; disclosed result. Preserve applicable account/target non-disclosure and exact replay before CURRENT business/lifetime rules. Accumulate sibling input issues once. Explicitly document revised refusal precedence instead of preserving incidental reparsing.

One retained-operation decoder checks headers, references, commitments and typed retained content for reads and replay. Replay independently runs pure decisions with stored business dates/pre-operation authority and compares effects/financial position; rebuild writes its resulting projections, verify compares them. Do not decode bytes just produced from trusted typed content to build a receipt. Validate external/stored bytes at actual boundaries. When a required compartment is erased, exact request comparison remains unavailable rather than retaining private digests; known acceptance is never reclassified as nonacceptance. Partial-erasure replay still checks available public/retained effects and structural erasure authorization; never skip the whole mixed operation or fabricate placeholder private values.

### D10 — Separate state lifetimes under one transition authority

Browser state is Draft, Delivery and DisclosedResource under one typed coordinator/reducer. Delivery owns the frozen command/bytes, original owner, review identity, consent and uncertain outcome. Draft owns editable/segmented localized input. DisclosedResource owns currently authorized documents. A shared OwnerScope includes origin, installation, actor and session epoch; each read additionally has target/query/resource identity and request generation.

Read/cache invalidation cannot erase delivery knowledge or authorize a new identity/resend. A later acceptance can settle the original delivery without exposing its stale receipt. Owner/session/disclosure changes synchronously mask sensitive displays and seeds, but preserve conservative scoped journal identity. Keep journal-before-send, exact retry, operation-ID echo, one consent, draft retention on conflict, and identity-only restart persistence. No private draft persistence or server preview tickets. Carry the exact reviewed command through preview→execute; only reviewed changes to draft/basis require fresh consent.

### D11 — Durable confirmed-expiry frontier, separate from business time

Keep bounded UUIDv7 first execution and head-locked final absence, adding a durable `expiredBefore` cutoff in installation/ledger-head operational state. An unseen operation with issuance strictly BEFORE cutoff cannot execute. During final absence confirmation, under the SAME head lock, advance cutoff to max(previous, wallObservedAt−window); strict `<` matches the existing `age>window` expiry rule. Compare full timestamp precision; UUID issuance has millisecond precision. Commit the frontier before replying NOT_ACCEPTED. Cancellation/unknown commit yields unavailable/uncertain outcome, never final absence.

Accepted lookup/replay precedes frontier checks. Read-only lookup can rely on an already committed frontier but cannot publish a new cutoff. Keep actual wall observedAt/recordedAt and stored business_date separate; do not clamp claim dates to a logical clock. Arbitrary forward jumps cannot be distinguished from legitimate idle time using this clock alone. A high committed frontier causes conservative unavailability after rollback; expose typed clock/frontier diagnostics and never lower it to recover availability.

Replace bare sequence/hash operator expectations with a checkpoint file containing installationId, operation sequence/hash and expiredBefore. `head` emits this checkpoint. `verify --checkpoint FILE` and `rebuild --checkpoint FILE` check identity/prefix and frontier. Add owner command `admit-restored-installation --checkpoint FILE`: with traffic isolated, verify the trusted checkpoint prefix exists, raise (never lower) the frontier to max(restored,trusted), invalidate restored sessions and commit atomically. Refuse if accepted history is behind/mismatched; advancing a frontier cannot restore missing acceptances. Remove old --expect and standalone invalidate-restored-sessions. No compatibility alias. Checkpoints must be retained independently before loss; no promise is made to recover observations newer than all trusted retained evidence.

### D12 — Structural transport admission is distinct from core semantic admission

Native and generated client structural validators agree on closed members, tags, required/null/omitted shape, exact integers, UTF-8/scalar/depth/byte limits and disclosure/renderability structure. Business equations and state transitions belong in core semantic admission/producer validation. Stored bytes constructing trusted Domain state still receive core admission. Do not add a second JavaScript financial rules engine.

Make codec interpretation/refinement boundaries explicit; remove the false universal claim that arbitrary Read/Write/Schema predicates cannot differ. Hide uncontrolled codec construction where practical; semantic refinements cannot silently advertise structural-schema equivalence. Preserve strict parser/canonical bytes and the four generated contracts. No serializer-library replacement or generic cross-language predicate language is authorized.

### D13 — Typed operator failures and one owned runtime lifecycle

Database/operator outcomes distinguish configuration/private-file refusal, already initialized, invalid zone/owner, unsupported schema/format, unsafe role/transport, maintenance required, checkpoint/frontier problems, provider unavailable, integrity failure and uncertain commit. Sanitize categories; never echo raw exception messages, SQL, secrets, private paths or principal values. Preserve determinate pre-commit refusal versus uncertain post-send commit and stdout delivery loss after mutating success. Actor API RESOURCE_UNAVAILABLE privacy is unchanged.

Remove duplicate host runtime forwarding interfaces. One host runtime owns provider, sessions, readiness, housekeeping and active leases. Keep leases across queue/pool/row waits and provider cleanup. Preserve typed startup faults; no string comparisons. Shutdown returns Drained or BoundExpired; disposal failure is a separately captured sanitized failure, never an exception escaping a request lease and replacing its committed result. Await drain completion; do not treat Task.WhenAny alone as successful disposal. Bound expiry is visible/nonzero, without overwriting an earlier primary run failure. Do not allow an unbounded hosted-service stop to prevent reaching the runtime drain bound. Bound expiry reports failure and does not dispose storage beneath active leases.

### D14 — Assurance aligned to behavior, not duplicated internal models

Before removing old mechanisms, introduce independent authored examples, Domain algebraic properties, structural mutation witnesses, hand-authored opaque human documents and real PostgreSQL boundary tests. Remove reflective path-name DTO reconstruction and test-side facts→Screen presenters once replacement assertions cover required behavior. Core-produced responses may be integration INPUTS, never independently expected truths. Keep canonical durable vectors and substantive security negative controls.

Change exact dependency graph equality to actual⊆permitted plus unknown-project failure; retain layering/purity/serializer/typed vocabulary and actual published closure checks. Permit explicit LOCAL runner deselection while still rejecting authored skip/todo/only; full CI remains nonempty and rejects disabled/focused tests. Update the existing reporter, no new meta gate. Keep meaningful browser disclosure/recovery ownership tests and one end-to-end journey per task. CI reduced PR/full main+dispatch remains; focused checks during work, one final full dispatch after focused repairs, no duplicate local full qualification. No version bump, release or merge.
