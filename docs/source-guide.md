# Find the source for a change

Start at the task you can observe. Follow its declaration, the code that supplies facts, and the
adapter that exposes the result. Use [Contributing](contributing.md#verification) to prepare the
checkout and choose broader verification.

## Case listing and reporting

1. Find `case.list` and `case.summary` in the [query identities](../src/ClaimCore.Application.Contracts/TaskIdentities.fs)
   and the [endpoint declarations](../src/ClaimCore.Application.Contracts/EndpointDeclarations.fs)
   then find their bindings in the [Wire registry](../src/ClaimCore.Wire/Api/Endpoints.fs).
2. Follow [ApiRoutes](../src/ClaimCore.Server/ApiRoutes.fs) through [Service](../src/ClaimCore.Application/Service.fs)
   to [CaseSetQueries](../src/ClaimCore.Application/Cases/CaseSetQueries.fs). Lists and reports share the
   [Domain selection rules](../src/ClaimCore.Domain/WorklistSemantics.fs). In the same snapshot,
   [FamilyReads](../src/ClaimCore.Application/Reads/FamilyReads.fs) delegates lists to
   [TransactionPages.cases](../src/ClaimCore.Application/Reads/TransactionPages.fs) and reports to
   [CaseSummaryQuery.read](../src/ClaimCore.Application/Reads/CaseSummaryQuery.fs). Both use the
   [SQL selection predicate](../src/ClaimCore.Application/Reads/CaseSelectionSql.fs). The route selects the
   [case list](../src/ClaimCore.Presentation/Screens/CaseList.fs) or
   [totals](../src/ClaimCore.Presentation/Screens/CaseTotals.fs) HUMAN presenter.
3. Run the [worklist tests](../tests/ClaimCore.IntegrationTests/Work/WorklistQueryTests.fs) with
   `FullyQualifiedName~PostgreSQL worklist contracts` for filters, ordering
   and cursor binding. [CaseReferenceTests](../tests/ClaimCore.IntegrationTests/Operations/CaseReferenceTests.fs)
   checks permanent references through opening, collisions, exact lookup and erasure. Pair the
   worklist checks with [reporting](../tests/ClaimCore.IntegrationTests/Cases/CaseReportingTests.fs)
   using `FullyQualifiedName~reporting with the published case selection` and the unit filter
   `FullyQualifiedName~pure query selections` for selection and access boundaries.

## Payment correction and financial exports

1. Start at [CaseCorrectPayment](../src/ClaimCore.Domain/Tasks/Case/CaseCorrectPayment.fs) for
   update inputs, payee retention and entry replacement. [FinancialPosition](../src/ClaimCore.Domain/FinancialPosition.fs)
   owns exact balances. [CaseDescription](../src/ClaimCore.Domain/Changes/CaseDescription.fs)
   supplies case balance differences for review, receipts and Activity.
2. Follow [PaymentQueries](../src/ClaimCore.Application/Cases/PaymentQueries.fs) for permitted
   actions on existing entries and [Payments](../src/ClaimCore.Presentation/Screens/Payments.fs)
   for the HUMAN payment region. [CaseSummaryQuery](../src/ClaimCore.Application/Reads/CaseSummaryQuery.fs)
   supplies per-case currency contributions shared by the grouped report and [Cases CSV](../src/ClaimCore.Wire/Export/CaseCsv.fs).
3. Use `FullyQualifiedName~PostgreSQL financial positions` for
   [integration checks](../tests/ClaimCore.IntegrationTests/Cases/PaymentPositionTests.fs)
   and the [financial register journey](../web/e2e/financial-register.spec.ts) for browser,
   CLI and service agreement.

## Reminder completion

1. Start at [reminder.complete](../src/ClaimCore.Domain/Tasks/Reminder/ReminderComplete.fs) or
   [reminder.completeAndPlanNext](../src/ClaimCore.Domain/Tasks/Reminder/ReminderCompleteAndPlanNext.fs).
   [Reminders](../src/ClaimCore.Domain/Reminders.fs) and
   [ReminderRules](../src/ClaimCore.Domain/Tasks/ReminderRules.fs) own reminder state and decisions.
2. The operation routes call [OperationService](../src/ClaimCore.Application/Execution/OperationService.fs),
   which uses [Engine.prepare](../src/ClaimCore.Application/Execution/Engine.fs) for preview and execution.
   [WorkQueries](../src/ClaimCore.Application/Work/WorkQueries.fs) reads the result and
   [ReminderScreen](../src/ClaimCore.Presentation/Screens/Reminder.fs) presents it.
3. Run the [reminder tests](../tests/ClaimCore.IntegrationTests/Work/ReminderPostgresTests.fs) with
   `FullyQualifiedName~injected second-effect failure` for atomic
   completion and planning. The unit filter `FullyQualifiedName~reminder timing` checks timing.
   [Browser acceptance](../web/e2e/reminder-completion-foundations.spec.ts) exercises the controls.

## Attachment preview and withdrawal

1. Find `attachment.preview` in the [endpoint declarations](../src/ClaimCore.Application.Contracts/EndpointDeclarations.fs)
   and `attachment.withdraw` in [AttachmentTasks](../src/ClaimCore.Domain/Tasks/Attachment/AttachmentTasks.fs).
2. For preview, follow [AttachmentRoutes](../src/ClaimCore.Server/AttachmentRoutes.fs) through
   [Service](../src/ClaimCore.Application/Service.fs) to
   [AttachmentReads](../src/ClaimCore.Application/Reads/AttachmentReads.fs) and
   [AttachmentStorage](../src/ClaimCore.Application/Storage/AttachmentStorage.fs), which verifies retained
   bytes. [WireOutcomes](../src/ClaimCore.Server/WireOutcomes.fs) selects the media type declared by
   [AttachmentTransport](../src/ClaimCore.Wire/Api/AttachmentTransport.fs).
3. The browser's [attachment panel](../web/src/features/case/CaseAttachments.tsx) calls
   [attachment transport](../web/src/api/attachments.ts) for preview. The
   [viewer build](../web/scripts/attachment-viewer.mjs) has a separate PDF entry. Withdrawal uses
   the [operation routes](../src/ClaimCore.Server/ApiRoutes.fs) and
   [OperationService](../src/ClaimCore.Application/Execution/OperationService.fs).
   [Engine](../src/ClaimCore.Application/Execution/Engine.fs) commits the accepted withdrawal through
   the [write transaction](../src/ClaimCore.Application/Storage/WriteTransaction.fs).
4. Run the [verification tests](../tests/ClaimCore.IntegrationTests/Attachments/AttachmentVerificationTests.fs)
   and [ledger test](../tests/ClaimCore.IntegrationTests/Attachments/AttachmentLedgerTests.fs) with
   `FullyQualifiedName~PostgreSQL attachment replay and metadata` and
   `FullyQualifiedName~attachment ledger binds binary acceptance` for byte proof, withdrawal and erasure. The [attachment browser spec](../web/e2e/attachments.spec.ts)
   checks the published UI and preview boundary.

## Uncertain operation recovery

1. Start with [Recover an uncertain change](integrating.md#recover-an-uncertain-change). Trace the
   operation lookup binding in [Service](../src/ClaimCore.Application/Service.fs) to
   [OperationQueries](../src/ClaimCore.Application/Operations/OperationQueries.fs).
2. The browser [reducer](../web/src/operation/operationState.ts) governs transitions.
   [useOperation](../web/src/operation/useOperation.ts) binds browser effects.
   The [recovery banner](../web/src/features/recovery/RecoveryBanner.tsx) opens the
   [recovery panel](../web/src/features/recovery/RecoveryPanel.tsx).
   [RecoveryContext](../web/src/recovery/RecoveryContext.tsx) owns the scoped journal and
   [recoveryLookup](../web/src/recovery/recoveryLookup.ts) owns browser scheduling and read leases.
   [OperationLookup](../web/src/features/recovery/OperationLookup.tsx) handles direct lookup by ID.
   [Owner scope](../web/src/protocol/ownerScope.ts) binds active edits to the session. The
   [journal protocol](../web/src/protocol/recoveryJournal.ts) retains principal scope across sessions
   and supplies durable records for the [IndexedDB adapter](../web/src/recovery/indexedJournal.ts).
   The [CLI recovery component](../src/ClaimCore.Cli/Recovery/) owns its local recovery records.
3. Run the [lookup tests](../tests/ClaimCore.IntegrationTests/Operations/OperationLookupTests.fs) with
   `FullyQualifiedName~Bounded operation lookup` for pending and final
   lookup and disclosure. The [recovery journal spec](../web/e2e/recovery-journal.spec.ts),
   [scheduler spec](../web/e2e/recovery-scheduler.spec.ts) and [browser settlement test](../web/tests/unit/operation/OperationSettlement.test.tsx)
   distinguish status lookup from retrying a submission.

## Running the checks

Build the selected project before using `--no-build`. For PostgreSQL walks, use the integration
project and its settings. Substitute the filter from the walk in this example.

```sh
dotnet build tests/ClaimCore.IntegrationTests --configuration Debug --no-restore
dotnet test --project tests/ClaimCore.IntegrationTests --configuration Debug --no-build --no-restore \
  --zero-tests-policy=strict --filter 'FullyQualifiedName~PostgreSQL worklist contracts' \
  -- --settings=eng/expecto.integration.runsettings
```

[Contributing](contributing.md#verification) gives the unit command and the published browser
spec selector. Run `bash eng/verify.sh --help` for stages and acceptance selectors.
The [runner](../eng/verify.sh) uses the same stage names for help and dispatch.

## Other starting points

- [Project boundaries](contributing.md#project-boundaries) and the
  [compiled reference policy](../tests/ClaimCore.Tests/Architecture/ProjectReferences.fs) map
  dependencies. Start the terminal path in [the CLI entry point](../src/ClaimCore.Cli/Program.fs)
  and the browser path in [the workspace shell](../web/src/shell/Shell.tsx).
- [Contract generation](contributing.md#generated-contracts) shows how to check the output of the
  [generator](../eng/ClaimCore.ContractGenerator/Program.fs). Change declarations and codecs at
  their source. HUMAN reads share the [document envelope](../src/ClaimCore.Presentation/Documents/HumanDocuments.fs)
  and [screen codec](../src/ClaimCore.Wire/Codecs/ScreenCodecs.fs). Feature presenters compose the document.
- [Schema integrity](../db/schema-integrity.md) links the storage owners and independent SQL
  witnesses. [Baseline admission](../src/ClaimCore.Application/Storage/Schema/Baseline.fs) hashes the
  installed schema bytes. Follow [case-family acquisition](../src/ClaimCore.Application/Storage/CaseFamilyAcquisition.fs)
  for bounded decision and receipt reads, or [person acquisition](../src/ClaimCore.Application/Storage/PersonAcquisition.fs)
  for person restoration. [Verification and release](contributing.md#releases) describes qualification
  and publication.
