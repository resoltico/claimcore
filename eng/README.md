# Engineering commands

Run `bash eng/verify.sh` at the repository root for the required gate. Use `--fast` or named stages for
iteration. Run `bash eng/verify.sh growth` separately before release or on demand. The
[development guide](../docs/development.md) explains prerequisites, stage scopes and evidence.

| Tool | Responsibility |
| --- | --- |
| [verify.sh](verify.sh) | Preflight, shared restore/build work, checks and bounded native report retention |
| [ContractGenerator](ClaimCore.ContractGenerator/Program.fs) | The three tracked contracts; [generation instructions](../docs/development.md#generated-contracts-and-boundary-verification) |
| [Check-FSharpLint.sh](Check-FSharpLint.sh) | F# rules, collecting failures across source roots |
| [third-party-notices.json](licenses/third-party-notices.json) | Reviewed package/apphost notices; acceptance checks actual publication contents |

The [source release procedure](../docs/development.md#source-releases) explains the owner's inputs to the
[release workflow](../.github/workflows/release.yml).
CI success records executed checks, without granting release approval.
