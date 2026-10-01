# Identity and privacy design QA

This separate skeptical pass challenged the [design](identity-privacy-design.md) against actual
`RuntimeAdmission`, actor gates, outcome unions, Web authentication, private-file code, lifecycle
approval queries and terminal owner evidence before implementation.

## Challenges resolved

1. **A receipt may already be committed.** Returning a new pre-admission refusal would lie about
   completion. Fail at the disclosure boundary and preserve durable acceptance, W1 and attempt
   knowledge. The existing HTTP dispatch boundary must retain uncertainty.
2. **A final check without a lease still races.** Perform the callback while the final primary
   shared authority lock and independent witness read fence are held. Do not await a check and then
   acquire the fence. The two barriers keep a concurrent grant/lifecycle writer behind disclosure.
3. **OPEN can have no case row.** Reuse command admission's reserved operation/preparation resolution;
   do not require `Get` or a reader grant. A pending OPEN export/import remains valid for its recovery
   capability before the business case exists.
4. **Recovery privileges differ.** Exporter, operator and importer checks must use their endpoint
   actions, including case-scope import authority. Revocation is not erasure: authorized dismissal
   may still return its retained preparation; a later grant loss must withhold it.
5. **Failure outcomes also carry payload.** Cover preparation summaries in rejected, failed,
   cancelled-after-preparation and unresolved outcomes, not just accepted receipts. Retain/import
   result operation identity comes from the trusted result and stored resolution.
6. **Lifecycle metadata differs from claimant data.** Steward review and opaque owner approval
   identities should not gain or require ordinary case-reader powers. Keep the existing owner-only
   purge and terminal evidence boundary.
7. **Claim order must not select authority.** Reverse conflicting clients/subjects, repeat identical
   values, supply composite identities and unauthenticated claims. Refuse all ambiguous identities;
   preserve positive human and service cases.
8. **Discovery is external JSON.** Reject scalar/array/null roots, syntax errors and duplicated known
   or extension members. Unique extensions must still succeed; errors must not echo input.

9. **Issuer paths are identity, not discovery URL formatting.** Root and slash-ended path issuers
   must work in both clients, but slash-different metadata must fail. Keep trimming only when
   constructing the discovery location.
10. **Future result variants need review.** Explicitly enumerate payload-free alternatives instead
    of a wildcard that could silently exempt a new claimant-bearing outcome. Normalize disclosure
    lookup faults to the same bounded exception as a lost grant, without provider details.

## Executable evidence

`PrincipalAdmissionTests` covers identity ambiguity and total discovery refusal.
`MutationDisclosureTests` uses real isolated PostgreSQL and witness authority: it inserts editor,
operator, exporter or importer revocation, or an erasure request, after work completes and before the
final disclosure lease. Independent SQL and W1 assertions distinguish withheld output from rollback.
Existing registered authorization, session, private-file, cursor, purge, hold and terminal suites
provide the corresponding positive and negative boundaries. Qualification outcomes belong in the PR,
not in this design record; source inspection alone is not execution evidence.
