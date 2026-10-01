# Repository tooling design QA

This is a separate adversarial pass over [the design](tooling-design.md), performed before
implementation. It reviews design guarantees; it is not execution evidence or owner approval.

## Findings and resolutions

| Counterexample | Resolution required before implementation |
| --- | --- |
| Cloning HEAD silently omits the change being checked. | Snapshot current visible source, including untracked additions and deletions; identify it by bytes rather than HEAD alone. |
| Source changes during copying, leaving a mixed tree. | Compare the copied fingerprint with the initial source fingerprint, then recheck the source; all three must agree. |
| A user's global Git exclusions hide a defective source file. | Only repository `.gitignore` rules select untracked source. Tracked ignored source remains included. |
| `git ls-files` repeats a staged addition, or filenames contain newlines. | Deduplicate names and use strict NUL framing, never newline splitting. Refuse portable case collisions. |
| A plain-directory fixture is actually inside a parent Git checkout. | Require the discovered Git top-level to equal the requested root; otherwise use isolated enumeration. An existing invalid `.git` marker is refused. |
| A missing executable emits both error and close events. | Each process promise and owned descriptor settles/closes once. Do not print command arguments or raw stderr in generic errors. |
| Existing executable bytes change while their marker stays valid. | Record and verify the executable digest, not only the downloaded archive digest. Invalid records require verified reinstallation. |
| Two stages acquire the same pinned tool concurrently. | Use unique temporary files; publish executable before its marker. A missing/invalid marker never establishes trust. |
| An output selector escapes the ignored tree, or cleanup traverses a link into private state. | Validate result roots and all cleanup paths before mutation; allow only checkout-owned artifact directories without link traversal. |
| Stage selection names nothing, leaving a zero-work success. | Reject unknown/empty selections before execution. Inventory and suite selection follow the same rule. |
| `--parallel 2` treats `2` as a suite name. | Parse positional IDs and value-taking flags once, validate them, then select suites. |
| A .NET entry point silently discards a requested Vitest suite. | Reject unsupported kinds before work; keep frontend execution behind its concrete adapter. |
| Debug compilation accidentally creates an architecture report for unrelated tests. | Environment templates belong to the suite, not the build configuration. |
| A database suite opens a clean checkout without a CLI executable. | Declare the CLI build input on every suite that needs it, and verify its path before starting tests. |
| A skipped stage appears in the passed count. | Missing required tools fail; skipped and not-started outcomes are reported separately. Local scoping is explicitly partial. |
| Optional result booleans permit an empty result or contradictory passed/skipped claims. | Use a required finite status; validate callback results at runtime and turn malformed results into failures. |
| Malformed resource, environment or appended-file metadata silently removes required inputs. | Validate stage metadata before acquisition or execution; reject unknown stage fields and unsafe source selectors. |
| A renamed workflow script is mentioned only in a comment. | Validate parsed executable fields; comments neither prove existence nor create execution references. |
| An orphan inventory contains the only contract token. | Contract evidence is read only from registered suite inventories; registry checks reject orphans separately. |
| Separate partition inventories omit an entire class of tests. | Keep one parent inventory and prove discovery partition union and disjointness before execution. |
| A security linter reports findings but deliberately exits zero. | Remove failure-suppressing flags and add a control against their reintroduction. |
| Generated command docs differ from actual stage arguments. | Render from the plan, including environment, requirements and appended source selection; validate freshness through the existing docs tool. |
| Fewer languages appear simpler but move protocol authority into an orchestration runner. | Retain explicit protocol owners. No generic runner receives signing keys or decides recovery acceptance. |
| A lock semantic summary becomes a second contract specification. | Reject a separately maintained semantic catalogue. Keep generated endpoint/schema source as authority; the derived contract-test map improves navigation without claiming semantic proof. |
| A contract lock's byte length is false or a duplicate row is hidden by map construction. | Validate the canonical lock shape and unique file list before constructing maps; compare actual byte lengths as well as SHA-256. |
| A read-only settings job has insufficient visibility and certifies absence. | Keep unavailable policy as unknown; no scheduled conformance claim without qualified visibility. Retain explicit workstation administration. |

## Review verdict

The design is implementable after incorporating the resolutions above. It deliberately avoids a
new universal build language, test-count registry, cross-job evidence ledger, language port,
cross-platform binary-transfer system or generated prerequisite for Compose. Those alternatives
were evaluated and rejected, not left as future work.

The reviewed implementation surface is engineering source enumeration, executable selection and
process lifecycle, tool acquisition, suite parsing/metadata/restore/scheduling, source-reference and
security-stage checks, contract-test navigation and generated development documentation. Product
contracts and persistent formats remain outside this change because the review found no tooling
benefit that requires changing their meaning.

Implementation must keep this record honest: an unexecuted platform or qualification is reported
as unverified, and an incomplete accepted change is not labelled completed.
