# Engineering commands

[Development](../docs/development.md) owns setup and verification. `verify.sh` fixes the order
of commands that can also run individually and stops at failure. `Check-FSharpLint.sh` checks
all source roots while retaining every failure status. ContractGenerator derives catalogue,
schemas, codecs, corpus and client declarations from Contracts; its lock is reviewed with changes.
Native tests use Microsoft Testing Platform and automatic Expecto discovery. Required properties
are bounded in memory; no mutation or standalone fuzz campaign is invoked locally or in CI.
Release publication is owner work and is outside a code-change verification run.
