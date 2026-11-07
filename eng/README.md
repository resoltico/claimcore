# Engineering commands

Use focused checks while editing and `bash eng/verify.sh --fast` for a broader checkpoint. Run
the applicable final stages once, choosing CI or local execution as their owner. See the
[verification policy](../docs/contributing.md#verification) for scope and evidence reuse.
| Tool | Responsibility |
| --- | --- |
| [verify.sh](verify.sh) | Preflight, shared restore/build work, checks and bounded native report retention |
| [ContractGenerator](ClaimCore.ContractGenerator/Program.fs) | Contracts, CLI reference and complete web/CLI runtime catalogs; [generation instructions](../docs/contributing.md#generated-contracts) |
| [Check-FSharpLint.sh](Check-FSharpLint.sh) | F# rules, collecting failures across source roots |
| [third-party-notices.json](licenses/third-party-notices.json) | Reviewed package/apphost notices; acceptance checks actual publication contents |

The [source release procedure](../docs/contributing.md#source-releases) explains the owner's inputs to the
[release workflow](../.github/workflows/release.yml).
CI success records executed checks, without granting release approval.

The unit stage builds only its Debug dependency closure and runs assembly-scoped ArchUnitNET controls.
CI owns non-browser published acceptance once (`acceptance-native`) and each engine separately
(`acceptance-browser`); `acceptance` runs both locally. Native runner filters select each scope without repeating the native walkthrough in browser jobs.
Chromium runs all cases; Firefox and WebKit select `@engine` cases.
Contract generation also validates
semantic catalog structure, typed term namespaces, declared producer arguments and renderer/core wording ownership. Browser production builds use only app/protocol sources;
the frontend verification stage still checks the whole TypeScript solution and runs all browser unit tests.
[Ten durable byte examples](../tests/fixtures/durable-vectors.json) carry fixed expectations and
derivation notes and hand-authored expected bytes. [SQL baseline identity](../src/ClaimCore.Postgres/Schema/Baseline.fs) derives from
exact embedded source bytes. Format numbers are owned by [Versions.fs](../src/ClaimCore.RecordFormat/Versions.fs).

See the [glossary](../docs/glossary.md) for project terminology.
