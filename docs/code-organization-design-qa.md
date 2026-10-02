# Code organization design QA

This separate pass challenges the [extension trace and design](code-organization-design.md).

1. **One catalog must not hide another dispatcher.** Endpoint data already carries the response
   schema. Construct it directly beside the request metadata instead of joining two string-keyed
   lists. Keep Web's explicit typed handler registrations and Application's closed outcomes.
2. **Names encode grouping, not authority.** The current endpoint namespaces already name their
   families. TypeScript files may derive membership from those names, but CLI delivery classification
   must remain independent. `session.logout` belongs to the session response file and mutates;
   unknown endpoints remain mutation-safe even when their namespace is recognized.
3. **A convenient fallback could hide omissions.** Reject unknown namespaces. The existing group
   coverage uses sets and can overlook duplicate endpoint identifiers; reject duplicates before
   generating declarations. Give both guards a synthetic counterexample and a healthy projection.
4. **Co-location must preserve exact output.** Keep response schemas, paths, request bodies,
   media/header bounds and endpoint order unchanged. The existing contract lock independently
   verifies generated artifacts; do not refresh it merely to accept a refactor.
5. **Sharing registration could weaken admission.** The shared JSON mapper must retain configured
   size limits, principal verification, per-request actor acquisition, antiforgery/origin handling,
   and core rate limiting. Raw uploads retain their distinct adapter and required digest header.
   Existing real hosted-route tests cover these boundaries.
6. **Fewer files alone is not improvement.** Delete the obsolete response inventory; keep schema,
   input decoder and wire outcome families with distinct responsibilities. The result reduces the
   number of unrelated edits for the concrete query without shifting decisions to callers.
7. **The exercise must not become speculative scope.** The count query exists only in an isolated
   test projection. No route, permission, store API or new business field is shipped. Real future
   query semantics still require their own Domain/Application design and boundary verification.

The design is approved as an agent design decision. Required verification and the user's explicit
merge instruction remain separate from that decision and from deployment qualification.
