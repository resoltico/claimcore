# Test oracles and evidence design

## Findings and scope

The transition sequence property checks revision and registration stability only after whatever
result Claim.decide returns. An implementation that refuses every command satisfies that property;
one that accepts payment without storing its date can also satisfy it. The separate availability
matrix protects individual eligibility, but does not prove cumulative sequence state.

Canonical golden vectors already provide independent bytes and SHA-256 expectations. The generated
record property, however, exercises only one of eighteen correction group combinations and changes
only the case reference in its identity property. Preserve the fixed vectors; broaden generated
shapes and guarantee their traversal rather than relying on random selection to hit every tag.

Recovery totality fuzzing currently mutates an OPEN command record and supplies no signing key.
Snapshot decoding receives that same command corpus. Those inputs cannot reach their own valid
decoder interiors. Totality also accepts a decoder that refuses every input. Seed each with its own
valid encoding, require a positive control, and keep malformed inputs and typed refusal checks.

## Approach

Use a test-owned state oracle for the sequence's existing synthetic command vocabulary. Determine
eligibility and complete expected fields from the documented state rules, independently of production
eligibility and transition helpers. Advance expected state separately from actual state. Exercise
explicit accepting and refusing paths before randomized sequences. Demonstrate that the same oracle
rejects refusal-only, missing-payment, wrong-revision and incorrect-closure results.

Generate every correction group shape and traverse the whole command vocabulary in each record
property case. Check round trips and identity sensitivity to operation ID, revision, reference and
command changes. These remain metamorphic checks, not substitutes for independent golden bytes.

Use synthetic recovery keys and a valid encrypted artifact for recovery fuzzing, plus a valid snapshot
for snapshot fuzzing. Assert accepted seed meaning before mutation. Authentication normally blocks
mutated bytes before decryption; describe this limit honestly rather than claiming arbitrary noise
reaches authenticated plaintext parsing. Existing signed-artifact policy and tamper tests remain.

No product behavior, public contract, dependency, storage format or protection changes are needed.
Do not add a generic mutation framework or infer real storage integration from fake stores. Existing
PostgreSQL and published-client suites retain those responsibilities. Run complete changed native
suites, repository quality and exact-head CI; regenerate inventories and documentation per policy.

## Implementation evidence follow-up

The expanded generator demonstrated that all-KEEP is record-decodable but rejected by request
admission. Assert its specific refusal rather than attempting a prepared fingerprint. The fixed
command vectors also derive typed requests by decoding their own golden bytes; paired field swaps
in encoder and decoder could preserve those bytes and digests. Supply independently constructed
typed command expectations and snapshot fields, and compare decoding and encoding separately.

The required suite runner also accepts the diagnostic recheck environment. Rechecking a unit-only
property while running fuzz returns successfully from every non-target property, so an exact seven-test
inventory can still pass without fuzz property execution. Reject diagnostic recheck configuration
at the required runner boundary before build or results creation. Keep direct executable rechecks
available for diagnosis; do not combine their results with complete verification.

The TestServer overload test builds a copy of Program's limiter registration. A production-only
permit-limit defect can therefore escape while its four-permit/sixteen-queue assertion passes.
Move the existing registration to one internal Web rate-limit module used by Program and both
HTTP fixture hosts. Keep fixture limits explicit, production defaults and middleware order unchanged,
and the fake core only as a controlled scheduler. Challenge the same permit mutation again after
sharing the actual mechanism; do not describe this test as PostgreSQL or complete Program execution.
