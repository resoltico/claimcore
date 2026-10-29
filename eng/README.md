# Engineering commands

Run `bash eng/verify.sh` at the repository root for the required gate. Use `--fast` or named stages for
iteration. Run `bash eng/verify.sh growth` and `bash eng/verify.sh growth-compare REF` separately
before release or on demand. Comparison uses committed, isolated inputs and retains all six runs. The
[development guide](../docs/contributing.md) explains prerequisites, stage scopes and evidence.

| Tool | Responsibility |
| --- | --- |
| [verify.sh](verify.sh) | Preflight, shared restore/build work, checks and bounded native report retention |
| [growth-compare.py](growth-compare.py) | Alternating same-host growth measurements, workload identity and relative budgets |
| [ProtocolGenerator](ClaimCore.ProtocolGenerator/Program.fs) | Contracts, CLI reference and browser chrome messages; [generation instructions](../docs/contributing.md#generated-contracts) |
| [Check-FSharpLint.sh](Check-FSharpLint.sh) | F# rules, collecting failures across source roots |
| [third-party-notices.json](licenses/third-party-notices.json) | Reviewed package/apphost notices; acceptance checks actual publication contents |

The [source release procedure](../docs/contributing.md#source-releases) explains the owner's inputs to the
[release workflow](../.github/workflows/release.yml).
CI success records executed checks, without granting release approval.

The unit stage runs assembly-scoped ArchUnitNET controls in Debug. Contract generation also validates
semantic catalog structure, typed term namespaces, declared producer arguments and renderer/core wording ownership. Browser production builds use only app/protocol sources;
the frontend verification stage still checks the whole TypeScript solution and runs all browser unit tests.
The [independent oracle](../tests/fixtures/oracle/generate_vectors.py) derives durable bytes and hashes from
[hand-authored cases](../tests/fixtures/oracle/sources.json), preserving their acceptance expectations without
calling the implementation. [SQL baseline identity](../src/ClaimCore.Postgres/Schema/Baseline.fs) derives from
exact embedded source bytes. Format numbers are owned by [Versions.fs](../src/ClaimCore.RecordFormat/Versions.fs).

See the [glossary](../docs/glossary.md) for project terminology.
