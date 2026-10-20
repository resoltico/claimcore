# Engineering commands

[Development](../docs/development.md) owns setup and verification. `verify.sh` runs named
stages in order, stops at failure and shares restore/build work within one invocation. Browser
selection and explicit PostgreSQL major overrides are checked before any expensive work; frontend assets build once when frontend and
acceptance stages share an invocation. CI
calls these same stages. `unit`, `lint`, `integration`, `postgres` and `web` require .NET;
`frontend` requires Node only and consumes tracked `contracts/` artifacts. `browsers` installs the required
Playwright engines, including Chromium for CLI authentication, and their Linux dependencies; `acceptance` builds and tests published clients.
`postgres` and `web` run the complete individual integration suites and restore and build only their project
closures; `integration` sequences these stages and reuses an earlier solution build when available.
Docker-dependent stages check the daemon before any selected expensive work. CI starts the two
suites independently of lint/unit completion and requires every suite in `Gate`. `workflows` checks shell scripts and workflow syntax.
`Check-FSharpLint.sh` retains every failure. With no arguments `verify.sh` selects the default;
`--fast` must stand alone. Empty or invalid arguments refuse before tools or Docker.

`licenses/third-party-notices.json` owns reviewed package and apphost notices; application
publication copies this common superset without consulting the NuGet cache. Acceptance checks
the actual publish inventory and rejects missing or unreviewed contributions.

ContractGenerator derives OpenAPI and TypeScript client types from Wire codecs and descriptors
from Domain/Application. It also references the pure Catalog for shared vocabulary and authored presentation. F# tests check exact artifact
bytes and inventory without invoking Node. Committed independent Python oracles bind durable bytes,
delivery and correlation; the generated vectors bind the browser to F# policies.
Frontend checks include consumed-response guards, localization, notices and accessibility tooling.
Knip checks unused frontend code. CSS checks cover formatting and browser accessibility/layout;
the gate performs no dedicated CSS semantic linting.

Native tests use Microsoft Testing Platform and automatic Expecto discovery. Required properties
are bounded in memory; no mutation or standalone fuzz campaign is invoked locally or in CI.
PR and merge-queue CI run one explicitly labelled Chromium smoke check. Main/tag CI and an
explicit full-qualification dispatch run all specifications on three independent engine runners.
Local engine execution stops on its first failure. A smoke result does not establish complete
browser coverage. Release publication is owner work.
