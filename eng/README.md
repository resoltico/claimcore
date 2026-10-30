# Engineering commands

Use focused checks while editing and `bash eng/verify.sh --fast` for a broader checkpoint. Run
`bash eng/verify.sh` before marking an implementation PR ready for review; CI still qualifies its full
platform matrix. See the [verification policy](../docs/contributing.md#verification) for scope and reuse.
Select [growth measurements](../docs/contributing.md#growth) for a concrete performance question or an
explicit task or release requirement. Neither growth command is part of the ordinary gate. A full
comparison isolates the reference commit and a stable candidate snapshot, including tracked working
changes, and retains all six runs.

For an independently hosted comparison, dispatch the CI workflow on the candidate branch with
`comparison_ref` set to the reference commit. Its optional job runs the same complete comparison on
one fresh runner and retains JSON/Markdown and raw workload reports as `growth-comparison` for 30 days.
This does not enable the separate nine-profile `growth` input or change normal PR requirements.

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
