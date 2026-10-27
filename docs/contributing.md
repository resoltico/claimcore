# Contributing to the implementation

Use this guide for a source checkout, an implementation change or a verification report. The
[case-work guide](case-work.md) owns user-facing meaning; [operating](operating.md) owns deployment and
custody procedures. See the [glossary](glossary.md) for terminology.

## First checkout

Install the SDK declared by [global.json](../global.json), Node from [.node-version](../.node-version), and
npm from [web/package.json](../web/package.json). Bash and Python 3 are required; Docker is needed for
integration, workflows and acceptance. Workflow checks also use ShellCheck. Unix sink tests use sh, mkfifo,
dd, stty, script, and Linux prlimit where available. The [CI matrix](../.github/workflows/ci.yml) declares
platform qualification.

From the repository root:

```sh
dotnet tool restore
dotnet restore ClaimCore.slnx --locked-mode
npm --prefix web ci
```

Use the declared versions before restoring. The frontend can build without executable .NET or Python;
ordinary .NET builds run neither npm nor generation. Framework-dependent apphost publication is qualified;
retain the whole publish directory and reviewed [notices](../eng/licenses/third-party-notices.json).
Self-contained, trimmed and single-file publication need separate qualification.

## Project boundaries

| Project | Responsibility |
| --- | --- |
| Json | Strict JSON values, writers, codecs and schemas |
| Domain | JSON-free value types, structural declarations, service requests/replies, policy and decisions |
| Protocol | Wire codecs, endpoints, semantic catalog, OpenAPI, core messages and host build identity |
| RecordFormat | Durable grammars, literal names, permanent byte limits and cursor encoding |
| Application | ServiceContext-based orchestration, named port inputs, replay, fencing and verification |
| Postgres | Transaction primitives, projections and installation administration |
| HostSecurity | Private files, HTTPS origins and TLS purposes |
| Web | Authentication, request gates, runtime leases and route serving |
| CLI | HTTPS, command grammar, typed replies and frames |
| Database | Owner administration |

Domain has no project dependencies. Protocol and RecordFormat build codecs over Domain structure using
Json; Application uses Domain and RecordFormat. Protocol owns combined act/scope vocabularies, endpoint names and catalog read declarations. Postgres implements Application ports using Domain,
RecordFormat and Json. Application and Postgres have no Protocol reference. Web and Database use the
Application Service and port surface; the published CLI contains Domain, Protocol, Json and HostSecurity,
with Application and Postgres excluded. The
[project-edge policy](../tests/ClaimCore.Tests/Architecture/ProjectReferences.fs) owns the exact direct edges.

[Compiled architecture controls](../tests/ClaimCore.Tests/Architecture/) run ArchUnitNET in Debug. Subjects
cover whole assemblies because F# task/async bodies and closures can live in StartupCode types. Nonempty
forbidden sets and compiled direct/task controls cover Domain isolation, durable vocabulary isolation,
service-model independence, host usage and vocabulary naming. Domain controls verify each forbidden
assembly separately, so duplicate fixtures cannot mask a missing boundary. Product internals may be exposed
to tests only.

A vocabulary uses the module named for its value type, compiled as `<Type>Module`. Structural act behavior
on `CaseActKind` is separate from its vocabulary module, so durable codecs can use shapes without importing
wire-facing names. [Domain outcome models](../src/ClaimCore.Domain/Outcomes/) own typed outcomes;
[ServiceContext](../src/ClaimCore.Application/ServiceContext.fs) receives product identity and dependencies
from host composition. Multi-argument transaction and store members take named records.

Change value rules in [Values.fs](../src/ClaimCore.Domain/Values.fs), and record/operation structure in the
[typed declaration](../src/ClaimCore.Domain/CaseFields.fs). Named projection columns derive from that declaration.
Web binds endpoint codecs to the public [Service](../src/ClaimCore.Application/Service.fs) through
[Route.bind](../src/ClaimCore.Web/Routes.fs).
Closed code sets use the shared [Vocabulary](../src/ClaimCore.Domain/Vocabulary/Vocabulary.fs) mechanism.

## Transactions and durable formats

[RecordFormat](../src/ClaimCore.RecordFormat/) owns durable grammar and
[Versions.fs](../src/ClaimCore.RecordFormat/Versions.fs) owns format numbers. Permanent scalar grammars used
to decode retained records may only be relaxed under the same format number. Tightening requires a
separately authorized format change. Independent permanent-limit vectors in
[record-format-vectors.json](../tests/fixtures/record-format-vectors.json) protect retained-record decoding.
[DurableNames.fs](../src/ClaimCore.RecordFormat/DurableNames.fs) owns exhaustive literal spellings;
[RecordLimits.fs](../src/ClaimCore.RecordFormat/RecordLimits.fs) owns the 65,536-byte request-record ceiling.
Wire spelling and transport limits do not govern retained records. Oversized encoding is an internal fault.
[Generated inventory properties](../tests/ClaimCore.Tests/RecordFormat/DurableRoundTripProperties.fs) visit
every case kind, snapshot status, authority kind, role and scope on each of 200 deterministic trials, with
varied permanent values and independently authored spellings; fixed byte vectors remain independent.

[IStore](../src/ClaimCore.Application/LedgerStorePort.fs) exposes read/write callbacks over primitives.
The PostgreSQL adapter creates a write transaction capability after taking the head lock. Reads use one
repeatable-read snapshot; writes read decision state under the lock and commit only on Proceed. Receipt
construction uses that same state, without a later read changing disclosure. Runtime leases remain held
through actual provider cleanup.

Permanent decoding does not reapply current admission. [baseline.sql](../db/baseline.sql) is the
checksum-bound schema source; its SHA-256 installation identity derives from the exact embedded bytes.
Baseline edits require a rebuild and a fresh installation. The runtime refuses unsupported installation
identities without modifying them.

## Generated contracts

After a declaration, codec or message change:

```sh
dotnet run --project eng/ClaimCore.ProtocolGenerator --configuration Release --no-restore -- --output contracts
dotnet run --project eng/ClaimCore.ProtocolGenerator --configuration Release --no-restore -- --check
```

The [generator](../eng/ClaimCore.ProtocolGenerator/Program.fs) owns its exact inventory under
[contracts](../contracts/), [the CLI reference](cli.md) and [browser chrome messages](../web/src/generated/chrome-messages.ts).
Check mode refuses missing, stale or extra outputs and does not repair them. Review generated diffs with their
producer. Custom output directories contain only contract artifacts.

Protocol publishes semantic operation/input metadata; CLI owns verbs, flags, operands, switches and
synopses. The generator renders the command reference through CLI grammar. The public catalog has no
`synopsis` field. Core owns domain wording and scalar guidance; renderer catalogs contain interaction text.
Qualified vocabulary keys are produced by `Vocabulary.messageKey`; the generated `Terms` map supports
`p.term(vocabulary, value)`. Registry checks allow equal tokens in different vocabularies and reserve one
composite `act` vocabulary. Identical renderer/core wording is refused in every locale.

Catalogs share one Template grammar. Structural checks cover language parity, producer arguments and every
template before publication. Handwritten
[template vectors](../tests/fixtures/message-template-vectors.json), wire examples, correction/transition
matrices and CLI frames provide independent expectations. Hand-authored durable cases in
[sources.json](../tests/fixtures/oracle/sources.json) define inputs and acceptance expectations; the
[Python oracle](../tests/fixtures/oracle/generate_vectors.py) derives their bytes and hashes without calling
the implementation. Keep those source cases independent of production codecs and validators.

## Browser boundaries and lint

[oxlint.config.ts](../web/oxlint.config.ts) is the single browser policy. Maintained import rules separate
presentation from request coordination and reject computed imports there. The strict composite
[protocol project](../web/tsconfig.protocol.json) uses ES2024, no ambient package types and a closed source
manifest. Its explicit JSON inputs include contract-owned locale data; portable encoding/clone
declarations support the byte boundary without enabling DOM or Node types. Pure DTOs live under protocol.
React, DOM, Node and outside source imports must fail that project. Browser and test projects still check
actual platform declarations. The app-only build references the strict protocol project, whose declarations
are emitted only into the ignored Node cache; whole-solution typecheck includes test and e2e sources.
Browser unit tests mirror `web/src` under `web/tests/unit`; cross-module scenarios live under
`web/tests/flows`, with fixtures and setup under `web/tests/support`. `npm run lint` rejects unit paths
without a matching source module. The lint configuration omits root category defaults while preserving
explicit override policy; review the complete `oxlint --print-config` output when changing it.

F# rules live in [eng/fsharplint.json](../eng/fsharplint.json); formatting policy
lives in [web/prettier.config.mjs](../web/prettier.config.mjs). Inline named exceptions need concrete reasons.

## Verification

```sh
bash eng/verify.sh --fast
bash eng/verify.sh
```

The first command is an iteration scope. The second is the complete required gate. Named stages share
restore/build work, execute in order and stop at failure:

| Stage | Scope |
| --- | --- |
| lint | Fantomas, FSharpLint and browser compiler boundaries |
| unit | Debug native properties, byte contracts and architecture controls |
| frontend | Audit, format, type-aware lint/types, browser units, Knip, licenses and build/notices |
| workflows | ShellCheck and pinned Actionlint |
| integration | PostgreSQL then Web suites |
| postgres / web | The corresponding isolated suite |
| browsers | Install the locked Playwright engines and required system dependencies |
| acceptance | Published service/CLI, deployment image and real browsers |
| growth | Separate synthetic contention and verifier qualification |

Pure tests run in Debug without optimization for visible compiled negative controls; production is built
in Release. Shared store-contract scenarios run against both the memory double and PostgreSQL. Native
projects use automatic nonempty discovery and explicit stage assignment. Focused, filtered, skipped, pending
or retried tests do not qualify a change. The postgres/growth stage split is defined by the gate itself.

Default acceptance runs complete Chromium, Firefox and WebKit suites, with zero retries. Engine/smoke selectors
are iteration scopes and label their incomplete evidence. Native report directories under
`artifacts/verification/` retain TRX and browser summaries; preserve relevant failures before report pruning.
No later passing check erases a failed run. Cleanup must preserve original failures and target owned resources.

## Growth

Run `bash eng/verify.sh growth` before a release and on demand. Its nine profiles, cardinality comparisons,
contention controls and report fields are owned by
[GrowthQualificationTests](../tests/ClaimCore.IntegrationTests/GrowthQualificationTests.fs). Selecting one
`CLAIMCORE_GROWTH_PROFILE` marks qualification incomplete. Report actual counts, throughput, latency and
verifier memory/time; measurements establish neither an SLA nor constant total-system memory.

The gate bounds managed completed-report retention, not total caches, image or artifact use. Protect active
runs, measure resource use and reclaim only owned temporary resources. Locked NuGet/npm restore and audits
still run with caches; shared caches are not disposable task resources.

## Dependencies

[Directory.Packages.props](../Directory.Packages.props) pins direct .NET packages and reviewed package lockfiles
record transitive resolution. Node/npm and [package-lock.json](../web/package-lock.json) own the frontend graph.
Audit all dependencies, retain image digests and use locked restore/npm ci. Dependency changes must update
consumers, lockfiles, licensing evidence and qualification together.

Private-file qualification uses real objects on the declared native CI platforms, including Linux musl.
Native-language and assistive-technology review remain human work.

## Source releases

[Releases](https://github.com/resoltico/claimcore/releases) distribute source archives. The owner authorizes
release version/date/tag assignment and publication. A completed change or green gate does not grant that
authority. See [CHANGELOG.md](../CHANGELOG.md) and the [release workflow](../.github/workflows/release.yml).
The workflow requires main membership, the exact annotated tag/SHA, matching version and successful tag CI;
branch evidence does not substitute for those inputs.
