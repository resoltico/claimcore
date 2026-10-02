# Code organization and practical changeability

Audit base: `774576d618ee704b5b16d9f39d6f88426d9fcbe8`. Requirements are owned by
[Domain](domain.md), [Architecture](architecture.md) and [Development](development.md).

## Concrete extension trace

Use a hypothetical authorized `case.count` query returning a count of visible cases. This is a
design exercise, not a new supported feature. Application must own its visibility semantics and
typed outcome; Postgres must supply its authorized query; Hosting must compose it; Web must decode
and invoke its typed facade member. Those changes protect distinct boundaries and remain explicit.

The contract path also requires updating endpoint metadata, a separate identifier-to-response
inventory, and an unrelated identifier list selecting the generated TypeScript response module.
Web registration repeats the same authentication, actor acquisition, JSON limit and rate-limit
setup across endpoint families. These edits do not introduce new business meaning and make a
small extension harder to follow.

## Decisions

- Co-locate each response schema with its request/method/path/media declaration in
  `WebEndpointCatalog`. Delete `WebResponseCatalog` and its string-keyed join. Retain the existing
  pure schema families; their separate domain-shaped responses have meaningful responsibilities.
  Use one POST/JSON constructor for the repeated transport shape, retaining explicit full
  declarations for session reads, recovery exports and raw uploads. This leaves room for a small
  extension in the cohesive catalog without creating another identifier list to satisfy size limits.
- Derive TypeScript response-file membership from the endpoint's established namespace (`case`,
  `operation`, `authority`, `command`, `recovery`, `lifecycle`, `tombstone`) and explicit session/
  definition identities. Preserve existing filenames, declaration order and imports. Reject unknown
  namespaces and duplicate identifiers, so a new family requires a reviewed generator decision.
  This is presentation grouping, not authorization or mutability.
- Build Web's common JSON route adapter once per mapping invocation and register explicit typed
  identifier/handler pairs through it. Keep raw recovery upload acquisition separate because its
  content type, size and digest header differ. Keep named decoder/outcome modules and fresh actor
  acquisition per request; do not introduce reflection dispatch or a service bag.
- Keep CLI's explicit reviewed read-only list and unknown-endpoint mutation-safe default. Session
  logout demonstrates that a response file named read is not proof of read-only behavior. A new
  query still needs a deliberate cancellation/uncertainty review.

The change removes duplicate inventories and repetitive setup rather than introducing another
endpoint framework, public metadata field or generator configuration. Module boundaries remain
responsibility-based. Size limits are retained without exceptions; the smaller routing module
reflects fewer repeated operations, not moving them into unowned fragments.

## Verification and delivery

Exercise the hypothetical query only in a synthetic contract projection: it must appear in the
read response module without a second identifier inventory, and remain mutation-safe until CLI
review. Reject an unknown family and duplicate endpoint. Existing independent endpoint inventories,
schema/output tests and real hosted route tests remain required. Contract generation must reproduce
the current lock byte-for-byte; no product command, public contract, schema baseline, authority
decision or dependency changes. The separate [design QA](code-organization-design-qa.md) challenges
the approach before implementation.
