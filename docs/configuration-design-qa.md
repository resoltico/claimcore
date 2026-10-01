# Configuration design QA

This separate pass challenges [the configuration design](configuration-design.md) before edits to
production configuration. Experiments used synthetic temporary trees or read-only compiler/tool
invocations; reports are ignored artifacts. It is design review, not owner approval.

## Evidence and counterexamples

| Challenge | Observation and required resolution |
| --- | --- |
| A top-level lint limit looks strict, but an override replaces it. | A synthetic 999 override was not detected. Check each override and inherited declaration, not only the parent limit. Native lint fixtures must prove the resulting per-file behavior. |
| Native inheritance changes plugin, rule or override merging. | The locked Oxlint schema supports ordered relative inheritance. Keep common policy free of scope-specific overrides and validate the tool's effective configuration. Child plugins and React/type-aware rules must remain present. |
| Moving common disabled rules makes exception registrations appear stale. | Move retained stable IDs to the common source file and retire duplicate registrations in the same change. Count actual declarations, not inherited copies. No silent blanket exclusion. |
| A new config file escapes source/policy scanning. | Add the shared native config to recognized configuration scanning and visibility probes. Resolve only safe repository-local inheritance, reject cycles and missing parents. |
| A TypeScript include pattern is mistaken for JavaScript coverage. | Enabling JavaScript checking produced 274 project diagnostics across 21 files. Repair checked source and test types; do not disable noImplicitAny or register a directory-wide waiver. |
| Checking tools imports accessible-component declarations with DOM relationships. | The experiment also exposed seven vendor declaration conflicts under the Node project. Supply the declarations actually required by those imports or use type-safe export introspection; no global skipLibCheck. Prove the final project with the real compiler. |
| Browser globals remain permitted throughout tools. | Separate lint environments and qualify actual consumer scope. DOM declaration interoperability must not create permission to use browser state in Node orchestration. |
| Enforcing engines rejects the existing locked graph. | Engine-strict dry runs passed both manifests under the selected runtime. Perform real locked installs and a mismatch control before claiming enforcement. |
| An exception date is ISO-shaped but impossible. | `2027-02-31` was accepted. Require a calendar-valid UTC day by round-trip comparison; test leap-day boundaries without timing-dependent current-year fixtures. |
| Registry entries disappear during permissive filtering. | Reject malformed arrays, entries, duplicate rules and unknown fields. Validate paths before checking existence so no parent escape can read private state. |
| Config consolidation accidentally changes package or contract bytes. | Keep dependency versions and generated lock bytes unchanged unless a separately justified dependency change is needed. Run locked restores, compiler/build, contract verification and native consumers. |
| A shared compiler parent changes without invalidating retained assets. | The source manifest currently names only leaf configs. Traverse their safe native inheritance graph and include every parent and shared traversal helper in the source fingerprint; qualify a parent-change negative control. |
| A shared compiler parent crosses the mutation execution root. | Actual Stryker execution fails because the root parent is absent from its isolated copy. Revised design: retain strict compiler settings in each package root and native frontend-local inheritance. Reject a custom sandbox copier or generated settings layer solely to consolidate these declarations. Repeat actual mutation execution after this decision. |
| NuGet inherits a workstation vulnerability source or mapping. | Declare cleared repository audit/source sections, then qualify with synthetic inherited configuration. Workstation credential files are not audit inputs. |
| Reviewer guidance asks for dependency inspection but Git hides the diff. | Lock attributes currently specify `-diff`. Restore text diffs and verify effective attributes plus a synthetic dependency change. |
| A backup file acts as an undocumented second tooling implementation. | The tracked `.bak` invokes the retired RepositoryTools assembly; no source references consume it. Remove it. Do not make a compatibility entry point. |
| A nonexistent Stryker path is replaced with an invented disable flag. | The locked schema only accepts a string; installed code imports the incompatible compiler API only if that file exists. Retain the tested absence control and explain this concrete tool constraint. |
| Numeric limits are treated as architectural proof. | Retain the current limits as bounded review signals, close bypasses and document their limits. Do not make a universal generator or mechanically fragment code to satisfy them. |
| Native settings or real installations are changed as a source-audit side effect. | No live GitHub protection, credential, installation, database or volume mutation is authorized by this implementation. Source configuration and synthetic verification suffice. |

## Verdict and implementation sequence

The reviewed design resolves the demonstrated failures with native configuration inheritance,
strict boundary validation and executed tool controls. A universal configuration framework,
combined npm workspace, protocol language port, generated Compose prerequisite and per-PR test
scoping add costs without addressing these findings and are rejected.

First consolidate shared policy and strengthen its validators. Then establish complete tooling
type coverage, enforce package/source settings and restore review visibility. Update documentation
and exception ownership, run affected native/frontend checks and complete PR CI. If a tool's actual
behavior contradicts this design, revise the affected decision and challenge it before proceeding.
All retained and changed areas are explicit decisions; no finding is deferred.
