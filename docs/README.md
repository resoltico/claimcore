# ClaimCore documentation

Choose the shortest path for the work you are doing:

| Task | Start here |
|---|---|
| Build the source and open the local Web application | [Getting started](getting-started.md) |
| Understand the thirteen fields, validation, or transitions | [Domain contract](domain.md) |
| Understand project dependencies and trust boundaries | [Architecture](architecture.md) |
| Consume structured core outcomes or extend diagnostic presentation | [Core outcome diagnostics](diagnostics.md) |
| Automate ClaimCore or recover an uncertain CLI request | [CLI and protocol](cli.md) |
| Configure or operate the browser host | [Web](web.md) |
| Manage PostgreSQL schema or technical preparation retention | [Database](database.md) |
| Build, test, lint, update dependencies, or qualify a change | [Development](development.md) |
| Publish a PR, diagnose CI, or activate repository governance | [CI governance](ci-governance.md) |
| Review changes and authorize a merge | [Owner review](owner-review.md) |
| Prepare or publish a source-preview release | [Releasing](releasing.md) |
| Assess privacy, recovery, or deployment readiness | [Security and operations](operations.md) |
| Exercise one synthetic CLI lifecycle | [Synthetic walkthrough](../examples/README.md) |

Repository-local references explain the [frontend source](../web/README.md), generated semantic,
CLI-v4, and Web-v3 contract artifacts, and [engineering tooling](../eng/README.md). Contributor
policy is in [CONTRIBUTING.md](../CONTRIBUTING.md); support routing is in [SUPPORT.md](../SUPPORT.md);
sensitive reports follow [SECURITY.md](../SECURITY.md).

Each rule has one durable owner. Other documents summarize and link to that owner instead of
maintaining competing copies.

The [repository tooling design](tooling-design.md) and its separate [design QA](tooling-design-qa.md)
record the decisions behind engineering foundations. The [configuration design](configuration-design.md)
and its separate [design QA](configuration-design-qa.md) record the cross-ecosystem configuration audit. The generated [contract-test map](contract-tests.md)
helps locate the registered tests that name each contract. The [domain model design](domain-model-design.md)
and its separate [design QA](domain-model-design-qa.md) record the business-invariant audit.

The [architecture and contract design](architecture-contract-design.md) and its separate
[design QA](architecture-contract-design-qa.md) record the component and ArchUnitNET wiring audit.

The [persistence and recovery design](persistence-recovery-design.md) and its separate
[design QA](persistence-recovery-design-qa.md) record the transaction, witness, replay and retention audit.

The [identity and privacy design](identity-privacy-design.md) and its separate
[design QA](identity-privacy-design-qa.md) record the authentication and disclosure audit.

The [verification and delivery design](verification-delivery-design.md) and its separate
[design QA](verification-delivery-design-qa.md) record the operational foundations audit.

The [product scope design](product-scope-design.md) and its separate
[design QA](product-scope-design-qa.md) examine required mechanisms and remove unsupported complexity.

The [code organization design](code-organization-design.md) and its separate
[design QA](code-organization-design-qa.md) trace extension ownership and remove duplicate coordination.

The [client workflows design](client-workflows-design.md) and its separate
[design QA](client-workflows-design-qa.md) trace drafts, exact recovery handoffs and client read races.

The [capacity and scheduling design](capacity-scheduling-design.md) and its separate
[design QA](capacity-scheduling-design-qa.md) examine query work, audit cadence and shutdown ownership.

The [test evidence design](test-evidence-design.md) and separate
[design QA](test-evidence-design-qa.md) challenge state oracles, generated shapes and decoder reachability.
