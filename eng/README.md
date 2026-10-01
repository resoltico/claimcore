# Engineering tooling

`eng` means engineering. It contains repository tooling and policy configuration that supports the
product code under `src/` and the verification code under `tests/`; it is not a shipped application
or a language-specific source directory.

Orchestration, policy and report verification are Node programs under [`ci/`](ci/) (test suites,
stage plans, coverage, secret scans, publish manifests, pinned tools), tested with `node:test`; the
exception engine and its scanners are under [`lint/`](lint/), release publishing under
[`release/`](release/), and the PostgreSQL backup drills under [`backup/`](backup/) (Python). The
remaining shell scripts are the Docker-shaped drills and format/lint wrappers: they start and remove
labelled test containers, run the published acceptance and browser lifecycles, and check Compose
behavior. `Remove-LabeledTestContainers.sh` checks the exact current test-run label before removing
disposable containers and their anonymous volumes; `Test-LabeledTestContainerCleanup.sh` exercises its
ownership and failure boundaries. Neither script prunes the shared Docker daemon or removes ClaimCore's
persistent named Compose volume.

[`ClaimCore.Docs`](ClaimCore.Docs/) checks authored and generated documentation: Markdown, generated help
blocks, exact-case links, contract declarations and the contract tokens in the test inventories.

[`ClaimCore.ContractGenerator`](ClaimCore.ContractGenerator/) materializes contract artifacts from the pure
`ClaimCore.Contracts` projection: semantic core, exact CLI-v4/Web-v3 request and response schemas,
production-codec corpora, and split TypeScript DTO modules. The locked frontend postprocess adds bounded AJV
standalone core and recovery validator groups. The artifacts are generated into the ignored
`web/src/generated/contracts/` and verified against `config/contracts.lock.json`. The generator remains
engineering tooling, not a runtime authority or additional application. CLI, Web, and the browser consume the
same pure codec/schema authority.

Shared test configuration and license inputs also live here. The official, digest-pinned PostgreSQL image
used by Compose and the tests is named in `db/postgresql-baseline.json`. Generated reports and manifests
belong under ignored `artifacts/`, never in this directory.

[Development](../docs/development.md) is the sole owner of contributor commands;
[Contributing](../CONTRIBUTING.md) owns contribution policy. This directory map owns neither.
