# Canonical records and capability presets

Decision made 2026-10-15 on the reviewed owner foundations design. This refines
[0001](0001-single-authority-ledger.md); its single-ledger trust model and non-goals remain.

## Decision

RecordFormat writes RFC 8785 canonical JSON with ordinal UTF-16 member order, strict Unicode and
invariant exact numeric spelling. Request format 4, snapshot format 3, authority format 2, case
identity format 2 and ledger entry format 2 replace unreleased development formats. Entry hashes
use a fixed 183-byte metadata preimage and salted payload commitment; neither payload nor a second
request digest survives erasure. Normal reads/replays still validate retained payload commitments.

Format invariants and admission policy are separate across Domain, codecs, schemas, SQL and browser
hints. New assertions obey current policy; retained values decode/render/reaffirm by permanent
format rules. One opaque claim/case record and one act union cover business and stewardship.
M = 9007199254740991 is terminal: expected revisions 0..M, accepted revisions/sequences 1..M;
M − 1 can advance to M. Exact authorized replay remains possible at exhaustion.

The owner selected OD-1 printable non-space ASCII references, ASCII case-folded resource identity
and original snapshot spelling. Canonical request identity retains authored reference case.
OD-2 country checks two uppercase ASCII letters without an ISO membership table; `ZZ` means
unknown/international. Name admission is bounded to 200 scalars with explicit invisible/directional
refusals. Permanent format rejects NUL, malformed Unicode and noncharacters; display warnings never
rewrite accepted text. The generic canonical writer can encode escaped NUL.

OD-3 uses seven presets: OWNER, CASE_READER, CASE_REGISTRAR, CASE_HANDLER, PAYMENT_RECORDER,
DATA_STEWARD and AUDITOR. One Application table owns capabilities. Correction permissions follow
all requested groups, including reaffirmations. OWNER/DATA_STEWARD targets and execution are
human-only; at least one enabled human installation owner remains. Installation authority review
requires ReadAuthority. Current snapshot disclosure requires ReadCase; superseded disclosure needs
ReadHistoryFull, within the locked/read context for execution, replay, preview, observation/history.

Eleven endpoints share one acyclic wire fingerprint and tracked generated contracts. .NET produces
them without npm; the Node frontend consumes them without .NET. Server schema validation remains;
browser strict parsing and consumed-shape guards preserve malformed-response uncertainty without
AJV. Localizations remain ignored and generated before consumers; notices stay independent.

OD-4 removes contract-token-to-test-name coupling while retaining unique owner tokens, link/anchor
checks and automatic native discovery/focus/skip/membership guards. OD-5 replaces 108 repeated
browser correction cases per engine with 36 literal serialization cases and six localized browser
acceptance/refusal/RTL cases; other specifications and zero retries remain. The later CI efficiency
audit adds explicitly partial Chromium PR smoke, with full isolated engine jobs on main/tag/manual.

Verification has 17 typed findings with deterministic precedence; divergence exits 5 and I/O remains
FAILED. Permanent authority/history invariants remain; current policy is not replayed. PostgreSQL
support declares and enforces major/minimum minor (initially 18.6); no major 19 qualification exists.
Cursors bind authority/query/time in a bounded authenticated envelope, with a shared ephemeral
runtime HMAC key, fixed-time verification, 15-minute lifetime and restart invalidation.

OD-6 establishes format-number dispatch now and decoder retention for released formats. Public
compatibility commitments start with 1.0.0; unreleased formats may be replaced with explicit
incompatibility. Shipped migrations remain immutable. This is a compatibility mechanism, not an
assertion that older development databases can be converted or a promise of current production use.

## Alternatives and challenge

The design considered server-rendered HTML, deterministic CBOR, a Merkle log/signed checkpoints,
static separation of duties, self-grant prohibitions and amount-tiered authority. These add concepts
without solving the bounded register’s current needs. Canonical JSON preserves readable records;
scoped capability presets and current locked decisions keep one authority policy.

Review corrected material shortcuts: format policy must survive generic schemas; malformed browser
receipts cannot be tag-only accepted; superseded disclosure applies to every receipt path; global
exhaustion precedes arithmetic; retained commitment checks replace redundant digest storage. A
single source owns fingerprint inputs, support versions and capability facts. CSS semantic linting
was deliberately removed with Stylelint; formatting is not equivalent assurance. Unneeded NuGet
transitive pins were removed only after restore/audit/build evidence, preserving direct versions.

## Consequences

Deploy matching clients/service and initialize a fresh database; no conversion is supplied.
Grant CASE_HANDLER and PAYMENT_RECORDER where one person decides and records payments.
Policy tightening does not invalidate history. One format change adds a decoder after release.
PR smoke is partial evidence; complete qualification is separate and never implied by its result.
