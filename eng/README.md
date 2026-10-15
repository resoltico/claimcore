# Engineering commands

[Development](../docs/development.md) owns setup and verification. `verify.sh` runs named
stages in order, stops at failure and shares restore/build work within one invocation. Browser
selection is checked before any expensive work; frontend assets build once when frontend and
acceptance stages share an invocation. CI
calls these same stages. `unit`, `lint` and `integration` require .NET; `frontend` requires
Node only and consumes tracked `contracts/` artifacts. `browsers` installs the selected
Playwright engine and its Linux dependencies; `acceptance` builds and tests published clients.
`workflows` checks shell scripts and workflow syntax. `Check-FSharpLint.sh` retains every failure.

ContractGenerator derives tracked catalogue, schemas, descriptors, corpus and client declarations
from Contracts. Native tests check exact artifact bytes and inventory, without invoking Node.
Frontend checks include consumed-response guards, localization, notices and accessibility tooling.
Knip checks unused frontend code. Stylelint is no longer a gate: this deliberately removes its
CSS semantic linting; formatting, type checks and browser accessibility/layout cases remain.

Native tests use Microsoft Testing Platform and automatic Expecto discovery. Required properties
are bounded in memory; no mutation or standalone fuzz campaign is invoked locally or in CI.
PR and merge-queue CI run one explicitly labelled Chromium smoke check. Main/tag CI and an
explicit full-qualification dispatch run all specifications on three independent engine runners.
Local engine execution stops on its first failure. A smoke result does not establish complete
browser coverage. Release publication is owner work.
