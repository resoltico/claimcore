# Identity, authorization and privacy design

This audit follows authentication into actor/resource authority, claimant disclosure, recovery
artifacts and destructive privacy work. The design and challenge passes preceded implementation;
[design QA](identity-privacy-design-qa.md) records the counterexamples. Current contracts remain
owned by [Architecture](architecture.md#cc-auth-001), [Web](web.md#cc-web-001),
[CLI](cli.md#cc-cli-002) and [Operations](operations.md#privacy-erasure-and-suppression).

## Findings and decisions

Web identity selection used `FindFirst` for subject and authorized client. Repeated claims could
silently choose the first actor or the human/service classification. Require one authenticated
identity, exact ordinal claim names, one subject and, for bearer admission, one authorized client. Reject repeated identical
claims too. Browser callback and cookie admission share this selection. Signature, exact issuer,
audience, lifetime, algorithm and token-type validation remain middleware responsibilities.

Web discovery used object-property operations without first checking object shape, and accepted
duplicate members. CLI discovery already rejected both. Web also prohibited slash-ended HTTPS issuers and endpoints, while CLI trimmed the issuer during
comparison. Keep the complete configured URI as identity in both transports; allow canonical root
and slash-ended HTTPS endpoints, and refuse a slash-different metadata issuer.

Web now refuses nonobjects and duplicate
members while allowing unique provider extensions; failure remains a fixed nonreflecting cause.

Mutation stores authorized under their primary transaction, but Hosting acquired its final disclosure
fence after that transaction and witness settlement. A grant revocation, actor disablement, void or
erasure request could therefore win before an already-produced preparation, receipt or artifact
returned. The installation-generation check alone could not authorize that payload.

Application now classifies claimant-bearing mutation outcomes and reuses the existing endpoint
capability and resource gates. Hosting performs this reauthorization inside the final primary-share,
then witness-read fence. Commands use their original endpoint action; recovery resolve, dismissal
and export use their own operation capabilities. Import results resolve stored operation identity
and check the case-scoped import capability. No new public authorization interface, reusable grant,
credential or caller-supplied case identity is introduced. Read operations retain the fence across
both original admission and materialization.

Lost disclosure authority raises a bounded failure across the native core boundary. A dispatched
HTTP mutation already projects that failure as delivery uncertainty. The committed operation and
attempt evidence remain intact; neither transport nor native callers may infer rollback. An export
buffer withheld by this check is cleared. Optional claimant-bearing preparation summaries in failed,
refused and uncertain mutation outcomes receive the same check as successful results. Payload-free
approval identities, revisions and tombstones do not require claimant-read privileges.

Holding a read fence across the whole mutation would deadlock its primary/witness write locks.
Comparing only the original global grant revision would unnecessarily reject a result after unrelated
owner activity and would not express the current resource policy. Hidden ambient actor state and a
second authorization policy are unnecessary. Explicit callbacks under the existing final fence reuse
one policy and preserve primary-first lock order.

## Adjacent boundaries reviewed

- The security policy still described the removed shared browser credential. Its supported-boundary
  description now agrees with individual OIDC identities, actor grants and trusted administrator limits.
- Browser tickets remain server-side, idle/absolute bounded and removed on logout; restart loses
  them. Cookies are Secure, HttpOnly and host scoped. Mixed bearer/cookie requests, unsafe origins,
  fetch metadata, absent antiforgery and body/media violations refuse before dispatch.
- CLI tokens remain process-local; credentials use handle-first private reads. HTTPS transport
  disables redirects, validates peer identity and does not echo provider replies or private paths.
- Case-list positions are authenticated encrypted continuations bound to principal, actor, grant
  revision, query limit, expiry and runtime key. Recovery keyset positions are storage positions,
  never grants: every page independently resolves current actor/resource authority. They do not
  encode claimant names or raw references.
- Private files use no-follow directory/leaf handles, owner/mode/ACL checks, descriptor identity,
  bounded reads and exclusive creation. Windows private runtime operations remain fail closed.
  Diagnostics use closed causes and safe parameters; production Web logging has no providers.
- Owner is not a universal case-reader role. Service principals cannot become human owners or
  stewards. Management preserves an enabled owner and advances witnessed grant authority.
- Live holds, tombstone holds and managed-copy holds protect different retained material. Review
  dates do not release holds. Purge, witness-payload pruning, copy deletion and terminal certification
  remain separate owner operations, with current human approvals, exact evidence, active-hold checks
  and independent inventory/absence/recovery-fence proofs. Ordinary Web/CLI routes cannot perform
  the owner-only destructive step. Pseudonymous suppression is not total erasure.

There is no schema, canonical record, wire shape, contract lock or migration change. Existing owner
and privacy mechanisms are retained because their responsibilities differ, rather than consolidated
by superficial similarity.
