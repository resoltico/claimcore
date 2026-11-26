# Engineering commands

Use focused checks while editing and `bash eng/verify.sh --fast` for a broader checkpoint. Run
the applicable final stages once, choosing CI or local execution as their owner. See the
[verification policy](../docs/contributing.md#verification) for scope and evidence reuse.

| Tool | Responsibility |
| --- | --- |
| [verify.sh](verify.sh) | Preflight, shared restore/build work, checks and per-stage TRX reports |
| [native_test_report.py](native_test_report.py) | Requires every selected native test to pass in the fresh TRX report |
| [versioning.py](versioning.py) | Evaluated product version, lockfile and toolchain version checks, and release-note extraction |
| [compose-restart-smoke.sh](compose-restart-smoke.sh) | Builds the evaluation Compose stack and proves `down`/`up` keeps the installation in [CI qualification and scheduled runs](../.github/workflows/compose.yml) |
| [ci_policy.py](ci_policy.py) | Selects qualification work and checks required GitHub job results |
| [validate_advisories.py](validate_advisories.py) | Checks native advisory report coverage before interpreting findings |
| [ContractGenerator](ClaimCore.ContractGenerator/Program.fs) | Only contracts; runtime catalogs resolve in Presentation; [generation instructions](../docs/contributing.md#generated-contracts) |
| [third-party-notices.json](licenses/third-party-notices.json) | Reviewed package/apphost notices; native and acceptance checks verify actual publication contents |

The [release procedure](../docs/contributing.md#releases) explains the owner's inputs to the
[release workflow](../.github/workflows/release.yml).
CI success records executed checks, without granting release approval.

The unit stage builds its Debug dependency closure, runs assembly-scoped ArchUnitNET controls and
qualifies a real host CLI publication: apphost execution, dependency closure, notices and source archive.
Pull requests use Linux x64 and Chromium. Main pushes, merge groups and manual dispatch use Linux x64,
Linux musl and macOS ARM64, plus Chromium, Firefox and WebKit. Dependency advisories run on full events
and dependency-changing PRs. Compose restart runs on full events and PRs that affect the stack.
Gate checks that every job in scope succeeded.

CI splits native acceptance into `walkthrough` and `remaining` via `CLAIMCORE_ACCEPTANCE_SHARD`.
An unset selector runs all native cases. Chromium uses three shards selected by
`CLAIMCORE_ACCEPTANCE_BROWSER_SHARD`. Firefox and WebKit select `@engine` cases without splitting.
Each job owns its fixtures.
`acceptance` runs both complete scopes locally. Offline CLI checks run in-process in unit with one
published smoke per category. The [verification policy](../docs/contributing.md#verification)
describes the docs-only Gate and full event coverage.
Contract generation checks catalog structure, term namespaces, producer arguments and wording
ownership. Browser production builds use the app and protocol sources. Frontend verification
checks the full TypeScript solution and runs the browser unit tests.
[Wire boundary properties](../tests/ClaimCore.Tests/Contract/WireBoundaryProperties.fs) exercise
decoder totality. Hand-authored boundary cases assert acceptance and refusal separately.
When qualifying a critical assertion, use a disposable checkout, run the selected case unmodified,
then change the rule and require failure at that assertion. The JSON depth limit and
[expiry frontier equality](../tests/ClaimCore.Tests/Domain/ExpiryFrontierTests.fs) are examples.
[Durable byte examples](../tests/fixtures/durable-vectors.json) carry fixed expectations and
derivation notes and hand-authored expected bytes. [SQL baseline identity](../src/ClaimCore.Application/Storage/Schema/Baseline.fs) derives from
exact embedded source bytes. The installation format is owned by [InstallationFormat.fs](../src/ClaimCore.RecordFormat/InstallationFormat.fs).

See the [glossary](../docs/glossary.md) for project terminology.


For simultaneous published acceptance processes on one development host, set
`CLAIMCORE_ACCEPTANCE_PORT_RANGE=FIRST-LAST` to a different inclusive range for each process.
Use disjoint ranges within 10000–32767. Each fixture still skips restricted, ephemeral and occupied ports.
