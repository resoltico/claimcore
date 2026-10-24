# Engineering commands

[Development](../docs/development.md) owns prerequisites, commands, verification stages, resource limits and evidence. Run `bash eng/verify.sh` from the repository root for the complete gate; named stages and `--fast` are partial runs.

- [verify.sh](verify.sh) is the shared local and CI entry point. It checks prerequisites, shares restore/build work within one invocation and stops at failure.
- [ClaimCore.ContractGenerator](ClaimCore.ContractGenerator/Program.fs) publishes the three tracked contracts. [Generation and freshness](../docs/development.md#generated-contracts-and-boundary-verification) explains invocation and independent boundary checks.
- [Check-FSharpLint.sh](Check-FSharpLint.sh) checks F# sources and retains every lint failure.
- [third-party-notices.json](licenses/third-party-notices.json) owns reviewed package and apphost notices. Application publication copies the reviewed common superset; acceptance checks the actual publish inventory for missing or unreviewed contributions.

Release publication is separate owner work under the [source-release workflow](../.github/workflows/release.yml). CI success is execution evidence, not owner approval.
