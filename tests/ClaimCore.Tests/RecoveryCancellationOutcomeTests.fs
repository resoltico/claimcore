module ClaimCore.Tests.RecoveryCancellationOutcomeTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let private clock = businessTime today

let private draft operationId : CommandDraft =
    {
        OperationId = operationId
        CaseReference = "CANCEL-" + operationId.ToString("N")
        ExpectedVersion = 0L
        Command =
            DraftCommand.Flat(
                CommandKind.Open,
                [
                    "incidentDate", registration.IncidentDate
                    "incidentNotificationDate", registration.IncidentNotificationDate
                    "incidentCountry", registration.IncidentCountry
                    "claimantName", registration.ClaimantName
                    "insurerName", registration.InsurerName
                    "claimedAmount", registration.ClaimedAmount
                    "claimedCurrency", registration.ClaimedCurrency
                ]
            )
    }

let private coreWith (recovery: CoreRecoveryStore.Store) =
    let claims = new CoreStore.Store()
    recovery.AttachClaimStore(claims :> IClaimStore)
    CoreApi.create (claims :> IClaimStore) (recovery :> IRecoveryStore) clock, claims

let private prepared (core: IClaimsCore) operationId =
    match core.Prepare(boundRequest (draft operationId), CancellationToken.None).Result with
    | PrepareOutcome.Prepared(details, _) ->
        details.Summary.RequestSha256
        |> Option.defaultWith (fun () -> failtest "Retained digest is required.")
    | _ -> failtest "Synthetic preparation must be retained."

let private cancelledPrepare =
    testCase
        "[CC-REC-001] definite retain cancellation becomes cancelled before admission"
        (fun () ->
            let recovery =
                new CoreRecoveryStore.Store(
                    retainFailure = RecoveryStoreFailure.CancelledBeforeCommit
                )

            let core, claims = coreWith recovery
            let operationId = Guid.NewGuid()

            match
                core.Execute(boundRequest (draft operationId), CancellationToken.None).Result
            with
            | SubmissionOutcome.CancelledBeforeAdmission actual ->
                Expect.equal actual operationId "Exact cancelled operation"
            | _ -> failtest "Definite retain cancellation must not look uncertain."

            Expect.equal claims.TransactionCalls 0 "No claim transaction")

let private cancelledAttempt =
    testCase "[CC-REC-001] definite attempt cancellation prevents claim execution" (fun () ->
        let recovery =
            new CoreRecoveryStore.Store(startFailure = RecoveryStoreFailure.CancelledBeforeCommit)

        let core, claims = coreWith recovery
        let operationId = Guid.NewGuid()
        let digest = prepared core operationId

        match core.Recovery.Resolve(operationId, digest, CancellationToken.None).Result with
        | ResolveOutcome.ResolveCancelledBeforeAttempt summary ->
            Expect.equal summary.OperationId operationId "Retained operation"
        | _ -> failtest "Definite attempt cancellation must not execute."

        Expect.equal recovery.StartCalls 1 "Attempt admission was tried"
        Expect.equal claims.TransactionCalls 0 "No claim transaction")

let private cancelledDismissal =
    testCase "[CC-REC-001] definite dismiss cancellation remains actionable" (fun () ->
        let recovery =
            new CoreRecoveryStore.Store(
                dismissFailure = RecoveryStoreFailure.CancelledBeforeCommit
            )

        let core, _ = coreWith recovery
        let operationId = Guid.NewGuid()
        let digest = prepared core operationId

        match core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None).Result with
        | RecoveryDismissOutcome.DismissCancelledBeforeAdmission actual ->
            Expect.equal actual operationId "Exact cancelled dismissal"
        | _ -> failtest "Definite dismissal cancellation must not look uncertain."

        match core.Recovery.Inspect(operationId, None, 50, CancellationToken.None).Result with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RetainedInspection details)) ->
            Expect.equal details.Preparation.Summary.State PreparationState.Unsubmitted "No marker"
        | _ -> failtest "Cancelled preparation remains inspectable.")

let private cancelledImport =
    testCase "[CC-REC-001] definite import retention cancellation does not submit" (fun () ->
        let recovery =
            new CoreRecoveryStore.Store(retainFailure = RecoveryStoreFailure.CancelledBeforeCommit)

        let core, claims = coreWith recovery
        let command = draft (Guid.NewGuid())

        let request =
            Drafts.bind command
            |> Result.defaultWith (fun _ -> failtest "Synthetic import command must bind.")

        let source = RequestRecord.encode request
        let digest = source |> SHA256.HashData |> Convert.ToHexStringLower

        match
            core.Recovery
                .RetainCanonicalRecordImport(source, digest, CancellationToken.None)
                .Result
        with
        | RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission -> ()
        | _ -> failtest "Definite import cancellation must not be retention unknown."

        Expect.equal claims.TransactionCalls 0 "Import never executes a claim")

let private cancelledCombinedCommit =
    testCase "[CC-REC-001] cancelled combined execution is definite before commit" (fun () ->
        let recovery =
            new CoreRecoveryStore.Store(settleFailure = RecoveryStoreFailure.CancelledBeforeCommit)

        let core, claims = coreWith recovery
        let operationId = Guid.NewGuid()
        let digest = prepared core operationId

        match core.Recovery.Resolve(operationId, digest, CancellationToken.None).Result with
        | ResolveOutcome.ResolveCompleted(_,
                                          _,
                                          DefiniteExecution.FailedBeforeCommit(actual, fault),
                                          SettlementConfirmation.Confirmed) ->
            Expect.equal actual operationId "Exact operation identity"
            Expect.equal fault.Code FaultCode.StoreUnavailable "Definite pre-commit outcome"
        | _ -> failtest "Cancellation before a combined commit must not claim acceptance."

        Expect.equal claims.TransactionCalls 0 "No claim transaction reached the combined port"
        Expect.equal recovery.SettlementCalls 1 "Definite pre-commit outcome is recorded")

let private unknownDismissal =
    testCase "[CC-REC-001] commit-start dismissal failure remains state unknown" (fun () ->
        let recovery =
            new CoreRecoveryStore.Store(
                dismissFailure = RecoveryStoreFailure.TechnicalMutationUnknown
            )

        let core, _ = coreWith recovery
        let operationId = Guid.NewGuid()
        let digest = prepared core operationId

        match core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None).Result with
        | RecoveryDismissOutcome.DismissStateUnknown(actual, actualDigest, fault) ->
            Expect.equal actual operationId "Uncertain dismissal operation"
            Expect.equal actualDigest digest "Uncertain exact request digest"
            Expect.equal fault.Code FaultCode.TechnicalMutationUnknown "No inferred non-dismissal"
        | _ -> failtest "Commit-start dismissal loss must be explicit uncertainty.")

let private unknownImport =
    testCase "[CC-REC-001] commit-start import retention failure remains state unknown" (fun () ->
        let recovery =
            new CoreRecoveryStore.Store(
                retainFailure = RecoveryStoreFailure.TechnicalMutationUnknown
            )

        let core, claims = coreWith recovery
        let command = draft (Guid.NewGuid())

        let request =
            Drafts.bind command
            |> Result.defaultWith (fun _ -> failtest "Synthetic import command must bind.")

        let source = RequestRecord.encode request
        let digest = source |> SHA256.HashData |> Convert.ToHexStringLower

        match
            core.Recovery
                .RetainCanonicalRecordImport(source, digest, CancellationToken.None)
                .Result
        with
        | RecoveryImportRetainOutcome.RetainStateUnknown(kind, actual, operation, fault) ->
            Expect.equal kind RecoveryArtifactKind.UnboundCanonicalRecord "Artifact kind"
            Expect.equal actual digest "Exact source digest"
            Expect.equal operation (Some command.OperationId) "Retained operation identity"
            Expect.equal fault.Code FaultCode.TechnicalMutationUnknown "No inferred non-retention"
        | _ -> failtest "Commit-start import loss must be explicit uncertainty."

        Expect.equal claims.TransactionCalls 0 "Import never submits a claim")

let private cancelledObservation =
    testCase
        "[CC-REC-001] cancellation during receipt observation returns cancelled read"
        (fun () ->
            use cancellation = new CancellationTokenSource()
            let claims = new CoreStore.Store(onOperation = cancellation.Cancel)

            let core =
                CoreApi.create
                    (claims :> IClaimStore)
                    (new CoreRecoveryStore.Store() :> IRecoveryStore)
                    clock

            match core.ObserveOperation(Guid.NewGuid(), cancellation.Token).Result with
            | QueryOutcome.Cancelled -> ()
            | _ -> failtest "Observation cancelled during storage read must not return data.")

let private cancelledInspectObservation =
    testCase "[CC-REC-001] cancellation during recovery inspect hides receipt result" (fun () ->
        use cancellation = new CancellationTokenSource()
        let claims = new CoreStore.Store(onOperation = cancellation.Cancel)

        let core =
            CoreApi.create
                (claims :> IClaimStore)
                (new CoreRecoveryStore.Store() :> IRecoveryStore)
                clock

        let operationId = Guid.NewGuid()
        prepared core operationId |> ignore

        match core.Recovery.Inspect(operationId, None, 50, cancellation.Token).Result with
        | RecoveryQueryOutcome.RecoveryCancelled -> ()
        | _ -> failtest "Inspect cancelled during receipt observation must not return details.")

let private cancelledResolveObservation =
    testCase "[CC-REC-001] cancellation during resolve observation prevents attempt" (fun () ->
        use cancellation = new CancellationTokenSource()
        let claims = new CoreStore.Store(onOperation = cancellation.Cancel)
        let recovery = new CoreRecoveryStore.Store()
        recovery.AttachClaimStore(claims :> IClaimStore)

        let core = CoreApi.create (claims :> IClaimStore) (recovery :> IRecoveryStore) clock

        let operationId = Guid.NewGuid()
        let digest = prepared core operationId

        match core.Recovery.Resolve(operationId, digest, cancellation.Token).Result with
        | ResolveOutcome.ResolveCancelledBeforeAttempt summary ->
            Expect.equal summary.OperationId operationId "Retained identity"
        | _ -> failtest "Cancelled observation must not admit a claim attempt."

        Expect.equal recovery.StartCalls 0 "No attempt after canceled observation"
        Expect.equal claims.TransactionCalls 0 "No claim transaction")

let tests =
    testList
        "typed recovery cancellation"
        [
            cancelledPrepare
            cancelledAttempt
            cancelledDismissal
            cancelledImport
            cancelledCombinedCommit
            unknownDismissal
            unknownImport
            cancelledObservation
            cancelledInspectObservation
            cancelledResolveObservation
        ]
