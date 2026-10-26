# Engineering commands

Run `bash eng/verify.sh` at the repository root for the required gate. Use `--fast` or named stages for
iteration. Run `bash eng/verify.sh growth` separately before release or on demand. The
[development guide](../docs/contributing.md) explains prerequisites, stage scopes and evidence.

| Tool | Responsibility |
| --- | --- |
| [verify.sh](verify.sh) | Preflight, shared restore/build work, checks and bounded native report retention |
| [ContractGenerator](ClaimCore.ContractGenerator/Program.fs) | Contracts, CLI reference and browser chrome messages; [generation instructions](../docs/contributing.md#generated-contracts) |
| [Check-FSharpLint.sh](Check-FSharpLint.sh) | F# rules, collecting failures across source roots |
| [third-party-notices.json](licenses/third-party-notices.json) | Reviewed package/apphost notices; acceptance checks actual publication contents |

The [source release procedure](../docs/contributing.md#source-releases) explains the owner's inputs to the
[release workflow](../.github/workflows/release.yml).
CI success records executed checks, without granting release approval.

See the [glossary](../docs/glossary.md) for project terminology.
