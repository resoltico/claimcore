# Engineering commands

Use focused checks while editing and `bash eng/verify.sh --fast` for a broader checkpoint. Run
the applicable final stages once, choosing CI or local execution as their owner. See the
[verification policy](../docs/contributing.md#verification) for scope and evidence reuse.

| Tool | Responsibility |
| --- | --- |
| [verify.sh](verify.sh) | Preflight, shared restore/build work, checks and per-stage TRX reports |
| [compose-restart-smoke.sh](compose-restart-smoke.sh) | Builds the evaluation Compose stack and proves `down`/`up` keeps the installation; run by the [Compose restart workflow](../.github/workflows/compose.yml) |
| [ContractGenerator](ClaimCore.ContractGenerator/Program.fs) | Only contracts; runtime catalogs resolve in Presentation; [generation instructions](../docs/contributing.md#generated-contracts) |
| [third-party-notices.json](licenses/third-party-notices.json) | Reviewed package/apphost notices; native and acceptance checks verify actual publication contents |

The [release procedure](../docs/contributing.md#releases) explains the owner's inputs to the
[release workflow](../.github/workflows/release.yml).
CI success records executed checks, without granting release approval.

The unit stage builds its Debug dependency closure, runs assembly-scoped ArchUnitNET controls and
qualifies a real host CLI publication: apphost execution, dependency closure, notices and source archive.
Pull requests use Linux x64 and Chromium. Main pushes, merge groups and manual dispatch use Linux x64,
Linux musl and macOS ARM64, plus Chromium, Firefox and WebKit. Dependency advisories run on full events;
the PR advisory job succeeds without auditing. Every job feeds Gate.

CI splits native acceptance into `walkthrough` and `remaining` via `CLAIMCORE_ACCEPTANCE_SHARD`.
An unset selector runs all native cases. Chromium uses `CLAIMCORE_ACCEPTANCE_BROWSER_SHARD=1/2` and
`2/2`; Firefox and WebKit select `@engine` cases without splitting. Each job owns its fixtures.
`acceptance` runs both complete scopes locally. Offline CLI checks run in-process in unit with one
published smoke per category. The [verification policy](../docs/contributing.md#verification)
describes the docs-only Gate and full event coverage.
Contract generation checks catalog structure, term namespaces, producer arguments and wording
ownership. Browser production builds use the app and protocol sources. Frontend verification
checks the full TypeScript solution and runs the browser unit tests.
[Durable byte examples](../tests/fixtures/durable-vectors.json) carry fixed expectations and
derivation notes and hand-authored expected bytes. [SQL baseline identity](../src/ClaimCore.Application/Storage/Schema/Baseline.fs) derives from
exact embedded source bytes. The installation format is owned by [InstallationFormat.fs](../src/ClaimCore.RecordFormat/InstallationFormat.fs).

See the [glossary](../docs/glossary.md) for project terminology.


For simultaneous published acceptance processes on one development host, set
`CLAIMCORE_ACCEPTANCE_PORT_RANGE=FIRST-LAST` to a different inclusive range for each process.
Use disjoint ranges within 10000–32767. Each fixture still skips restricted, ephemeral and occupied ports.
