# Engineering commands

Use focused checks while editing and `bash eng/verify.sh --fast` for a broader checkpoint. Run
the applicable final stages once, choosing CI or local execution as their owner. See the
[verification policy](../docs/contributing.md#verification) for scope and evidence reuse.

| Tool | Responsibility |
| --- | --- |
| [verify.sh](verify.sh) | Preflight, shared restore/build work, checks and per-stage TRX reports |
| [ContractGenerator](ClaimCore.ContractGenerator/Program.fs) | Only contracts; runtime catalogs resolve in Presentation; [generation instructions](../docs/contributing.md#generated-contracts) |
| [third-party-notices.json](licenses/third-party-notices.json) | Reviewed package/apphost notices; acceptance checks actual publication contents |

The [source release procedure](../docs/contributing.md#source-releases) explains the owner's inputs to the
[release workflow](../.github/workflows/release.yml).
CI success records executed checks, without granting release approval.

The unit stage builds only its Debug dependency closure and runs assembly-scoped ArchUnitNET controls.
Pull requests use Linux x64 and Chromium. Main pushes, merge groups and manual dispatch use Linux x64,
Linux musl and macOS ARM64, plus Chromium, Firefox and WebKit. Dependency advisories run on full events;
the PR advisory job succeeds without auditing. Every job feeds Gate.

CI owns non-browser published acceptance once (`acceptance-native`) and each engine separately
(`acceptance-browser`); `acceptance` runs both locally. Native runner filters select each scope without repeating the native walkthrough in browser jobs.
Chromium runs all cases; Firefox and WebKit select `@engine` cases.
Contract generation also validates
semantic catalog structure, typed term namespaces, declared producer arguments and renderer/core wording ownership. Browser production builds use only app/protocol sources;
the frontend verification stage still checks the whole TypeScript solution and runs all browser unit tests.
[Durable byte examples](../tests/fixtures/durable-vectors.json) carry fixed expectations and
derivation notes and hand-authored expected bytes. [SQL baseline identity](../src/ClaimCore.Postgres/Schema/Baseline.fs) derives from
exact embedded source bytes. The installation format is owned by [InstallationFormat.fs](../src/ClaimCore.RecordFormat/InstallationFormat.fs).

See the [glossary](../docs/glossary.md) for project terminology.
