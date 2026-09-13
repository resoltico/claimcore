# Engineering tooling

`eng` means engineering. It contains repository tooling and policy configuration that supports the
product code under `src/` and the verification code under `tests/`; it is not a shipped application
or a language-specific source directory.

The scripts enforce formatting, analysis, dependency, privacy, coverage-input, property-seed,
Compose, Git-ignore, source-secret, artifact-upload, and published-application policies. The
`Remove-LabeledTestContainers.sh` helper checks the exact current test-run label before removing
disposable containers and their anonymous volumes; `Test-LabeledTestContainerCleanup.sh` exercises
its ownership and failure boundaries. Neither script prunes the shared Docker daemon or removes
ClaimCore's persistent named Compose volume. The Git-ignore gate uses an isolated temporary Git
database to prove that private/generated probes stay out while release inputs
remain visible. The source-secret gate builds a private Git-semantic snapshot so ignored local state
is not mistaken for committed source while force-tracked files remain in scope. CI scans each
artifact family before upload with `Scan-ArtifactSecrets.ps1`; `Check-ArtifactUploadPolicy.ps1`
and its negative controls require every workflow upload to retain that fail-closed guard.
`Invoke-MergedCoverage.ps1` is the one local/CI six-input merge and floor procedure;
`CoverageThresholds.psm1` enforces unchanged repository and Web floors with its own negative controls.
[`ClaimCore.Docs`](ClaimCore.Docs/) checks authored and generated
documentation, validates integrity-bound publish trees, and reconciles workflow-produced reports.
GitHub Actions executes the registered procedures; stage manifests describe and bind those workflow
outcomes but do not independently prove that a command ran.

[`ClaimCore.ContractGenerator`](ClaimCore.ContractGenerator/) materializes checked artifacts from the
pure `ClaimCore.Contracts` projection: semantic core, exact CLI-v3/Web-v2 request and response
schemas, production-codec corpora, and split TypeScript DTO modules. The locked frontend postprocess
adds bounded AJV standalone validators and binds the combined output manifest. The generator remains
engineering tooling, not a runtime authority or additional application. CLI, Web, and the browser
consume the same pure codec/schema authority; no legacy HTTP-v1 descriptor is retained.

Shared test configuration, license inputs, and exact test-identity inventories also live here.
`test-baseline-v0.1.json` is immutable source evidence; `test-lineage.json` and
`assurance-matrix.json` must remain exact live mappings, verified by
`Check-ConvergenceAssurance.ps1` and its negative controls. The matrix binds each generated CLI/Web
endpoint to its current response outcome tags and names separate runtime, protocol, recovery,
cancellation, migration, and GUI assertions; schema conformance is not a runtime-outcome claim.
The pinned PostgreSQL image recipe and same-volume synthetic validators live beside the
multi-architecture index/SBOM/Trivy policy. The manual publisher is separate from ordinary CI;
normal CI consumes only the reviewed index digest in `db/postgresql-baseline.json`. Generated reports
and manifests belong under ignored `artifacts/`, never in this directory.

[Development](../docs/development.md) is the sole owner of contributor commands;
[Contributing](../CONTRIBUTING.md) owns contribution policy. This directory map owns neither.
