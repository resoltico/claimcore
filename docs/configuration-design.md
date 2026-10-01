# Configuration audit and design

This record audits repository configuration at the merged tooling revision
`db6997baa1bf5a57023e074c694c33624427d211`. Configuration is executable policy, not an immutable
constraint. Each finding below has a present decision, including considered alternatives and
effects on its consumers. The separate design QA precedes implementation.

## Ownership model

Keep each ecosystem's native configuration format. A universal repository settings file would
require generators for npm, MSBuild, TypeScript, Python, Docker and Actions and introduce another
interpretation layer. Facts shared within an ecosystem use its native inheritance where execution isolation permits; facts that
must appear in independent native formats have executable equality checks. Derived locks and
database catalog observations remain generated evidence, not manually maintained settings.

Configuration changes must be tested through the tool that consumes them, including effective
per-file settings. Source text saying a rule is enabled is weaker than executing its negative
control. Unknown or malformed fields at repository-owned data boundaries are refusals, not
permission to fall back to an empty policy.

## Findings and decisions

| Area | Evidence and decision | Consumers and qualification |
| --- | --- | --- |
| JavaScript lint ownership | Engineering has 70 base rules; 69 exactly duplicate frontend rules. Put common categories, options and rules in one root native Oxlint configuration. Package configurations extend it and own their environments, plugins and genuine specializations. | Native Oxlint inheritance, frontend lint fixtures, engineering lint, exception accounting and policy validation must agree. Move shared exceptions to their actual source; retire duplicate registrations rather than retain aliases. |
| Effective lint policy | An experiment with per-file complexity/function limits of 999 is not detected by the checker. Validate inherited settings and every override, including severity, finite positive limits and options that change the measurement. Check category/options weakening too. | Add negative controls against override and inheritance bypasses. Keep suppression accounting separate from policy invariants. |
| TypeScript tooling scope | Frontend Node config includes scripts but does not enable JavaScript. Enabling it exposes 274 project diagnostics across 21 files. Establish a checked JavaScript tools project and repair its annotations/boundary models; do not turn off strictness or pretend the existing include covers it. | `checkJs`, strict compiler checks, Node reporters/generation tests, actual frontend build and mutation execution. Runtime JSON schemas remain the wire authority. |
| Compiler configuration isolation | A shared root compiler parent fails the actual Stryker sandbox: the tool copies only the frontend root. Retain strict settings inside each independent package root; frontend projects share their native local base. A custom sandbox copier or config generator would add machinery to eliminate a small, deliberate duplication. | Complete native compiler projects and actual isolated mutation execution; retained asset identity follows the frontend parent chain. |
| Browser and Node environments | The frontend lint environment enables both browser and Node for every file. Scope those environments to their actual consumers. Node tooling types may need DOM declarations for accessible-component introspection, but that does not authorize browser globals in arbitrary tooling. | Source, tests, browser evaluation callbacks and Node scripts retain the declarations they actually use; negative fixtures prove forbidden ambient globals. |
| Numeric complexity policy | Size/complexity thresholds are review heuristics, not proof of design quality. Retain the current ceilings in this change because the audit found a bypass, not evidence that a different number improves the project. Do not split responsibilities merely to satisfy a metric. | Shared Oxlint settings and existing F#/Python rules remain bounded; governance documentation describes their limits honestly. |
| Exception registry | Impossible dates normalize through JavaScript Date parsing. Path validation also does not refuse parent segments, and malformed array entries can disappear through filtering. Validate actual calendar dates, safe exact paths, complete shapes, unique rules and arrays. | Fixtures cover leap years, impossible dates, path escapes, malformed entries and duplicate identities without reading private state. |
| Dependency review | `.gitattributes` disables diffs for NuGet/npm/uv locks while contributor policy requires reviewing each graph change. Restore text diffs for those locks. | Git's effective attributes and a synthetic lock diff prove visibility; lock bytes and package graphs do not change for this correction. |
| npm toolchain enforcement | Engines are declared but npm defaults to advisory warnings. Both locked graphs passed an engine-strict dry run. Enable engine-strict in the two credential-free native npm policies. | Locked `npm ci` under the selected Node/npm versions, with mismatch controls. Retain separate graphs: making one workspace would force frontend dependencies into source-only checks and complicate production SBOM scope. |
| NuGet source and audit ownership | Package sources are cleared, but inherited audit sources are not explicitly bounded. Declare the public vulnerability source and clear its inherited entries; retain the single package source and explicit source mapping with cleared inherited mappings. | Native locked restore and a synthetic inherited-config experiment. Do not read or edit workstation credentials. The NuGet reference documents section inheritance and explicit audit sources. |
| Python policy | Ruff ALL, strict mypy, exact dev dependencies and a locked Python patch are coherent. Python's declared minimum and Ruff target describe supported syntax rather than the selected interpreter patch. Keep these distinct facts. | Frozen uv operations and the existing qualification/property checks. Do not port owner-side backup protocols merely to reduce the file-format count. |
| Formatting and editor settings | EditorConfig owns whitespace and F# formatting; Prettier, Ruff and shfmt own their native languages. Keep the ecosystem formats rather than generate editor configuration. Update editor language coverage only where it is missing an authored extension. | Formatter checks and effective native configuration; editor recommendations are convenience, not verification evidence. |
| Build-input identity | The Web asset manifest lists leaf TypeScript configs but omits their inherited base. Bind the complete repository-local TypeScript configuration chain, including its frontend base, to source identity. | Inherited parents and the shared traversal helpers are source inputs. An inherited-config change must invalidate a retained asset manifest; native build/verification must still produce identical contract bytes. |
| Mutation configuration | A deliberately absent TypeScript config path skips Stryker's compiler-API rewrite, required by the locked TypeScript 7 toolchain. Installed Stryker accepts only a string here and imports the incompatible compiler API when the file exists. Keep the absence control and truthful explanation; there is no native disable flag to substitute. | Actual required mutation execution plus the existing negative control. Keep target/threshold identity checks; no optional or weekly substitute for a qualifying run. |
| Coverage and test topology | Coverage floors are in the consumers that enforce them; suite inventories own exact tests and partitions are verified against their parent. These are distinct facts, not a second test catalogue. Keep them. | Native MTP suites, all three browser engines, report completeness and merged coverage. Do not replace assertions with configured counts or aggregate percentage claims. |
| Architecture and central packages | Architecture declares permitted responsibilities/edges; central packages pin versions; project locks resolve dependency graphs. Those are separate authorities and compiled/evaluated graph checks connect them. Retain them. | Complete architecture suite and locked restore. No generated architecture permissions from current code, which would make forbidden edges self-authorizing. |
| PostgreSQL and schema manifests | Runtime baseline pins supported server range and official image; primary/witness identities bind independent SQL bytes. Keep the independent manifests and the Compose equality control. Native Compose must remain usable without a generated environment prerequisite. | Fresh-baseline refusal/creation, witness and restore qualifications. Preserve adopted databases and retained old evidence; no reset or migration is part of this audit. |
| Runtime configuration | Web, CLI, Hosting and owner tools have different credential/authority responsibilities. Reviewed loaders already require private inputs, HTTPS admission, bounded operational limits and stored installation calendar. Retain typed loaders and their scoped setting names rather than one flat generic binder. | Existing configuration, origin/OIDC, private-file, actor-admission and audit-cadence tests. This audit changes no operational cap or business field merely to unify configuration. |
| Git ignore policy | Git owns visible source membership; the probes protect private state and generated output. Keep probe invariants rather than derive them from `.gitignore`, which would certify its own defects. | Existing isolated-index ignore controls and source scans. Remove the tracked retired-tool `.bak` after confirming it has no consumer. |
| Actions and dependency updates | The required Gate, pinned actions, read-only verification and protected publication remain coherent. Dependabot supports the selected ecosystems; cooldown applies to version updates, not security updates. Keep the explicit seven-day policy and native configurations. | Actionlint, pedantic zizmor, repository workflow controls and full PR CI. Live repository protections and owner credential custody are separate from source declarations. |
| Downloaded tools and external settings | Exact asset digests, selected platform executables and installed-byte validation own acquisition. Explicit plan/apply/readback owns GitHub administration. Retain both; no token or scheduled live-settings mutation is introduced by a configuration audit. | Existing three-platform smoke jobs, hash rejection, mocked settings tests and explicit unknown-state reporting. |

## Implementation boundaries

Implement shared native lint configuration, effective-policy checks, complete JavaScript tooling
type coverage, precise registry validation, visible lock diffs, enforced npm engines and explicit
NuGet audit/source inheritance. Remove the unused tracked backup. Update registered exceptions and
documentation with their actual owners. Do not add a compatibility loader or a replacement config
generator. Native formats and wire/schema baselines retain their meanings.

All areas above are resolved now as change or retain decisions. None is a deferred work item.
The retained decisions are not claims that future requirements can never change the design.

## Primary references

- [Oxlint native configuration](https://oxc.rs/docs/guide/usage/linter/config) describes relative inheritance and override behavior; the locked tool's schema and executable qualify this implementation.
- [NuGet configuration](https://learn.microsoft.com/en-us/nuget/reference/nuget-config-file) owns section inheritance and explicit audit sources.
- [Dependabot options](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference) distinguishes version cooldowns from security updates.
