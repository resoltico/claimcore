module ClaimCore.Tests.TypedCoreTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private clock = businessTime today

let private registrationValues =
    [
        "incidentDate", registration.IncidentDate
        "incidentNotificationDate", registration.IncidentNotificationDate
        "incidentCountry", registration.IncidentCountry
        "claimantName", registration.ClaimantName
        "insurerName", registration.InsurerName
        "claimedAmount", registration.ClaimedAmount
        "claimedCurrency", registration.ClaimedCurrency
    ]

let private draft operationId reference : CommandDraft =
    {
        OperationId = operationId
        CaseReference = reference
        ExpectedVersion = 0L
        Command = DraftCommand.Flat(CommandKind.Open, registrationValues)
    }

let private create () =
    let claims = new CoreStore.Store()
    let recovery = new CoreRecoveryStore.Store()
    recovery.AttachClaimStore(claims :> IClaimStore)

    CoreApi.create (claims :> IClaimStore) (recovery :> IRecoveryStore) clock

let private waitFor (task: System.Threading.Tasks.Task<'value>) = task.GetAwaiter().GetResult()

let private invalidRecoveryInputs (core: IClaimsCore) =
    match
        core.Recovery.List(RecoveryListView.Pending, Some "not-a-cursor", 1, CancellationToken.None)
        |> waitFor
    with
    | RecoveryQueryOutcome.RecoveryRejected rejection ->
        Expect.equal rejection.Code RecoveryRejectionCode.InvalidRecoveryInput "Recovery cursor"
    | _ -> failtest "Expected malformed recovery-cursor refusal."

    for operationId, digest in
        [ Guid.Empty, String.replicate 64 "a"; Guid.NewGuid(), "not-a-digest" ] do
        match core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> waitFor with
        | ResolveOutcome.RefusedBeforeAttempt(None, rejection) ->
            Expect.equal
                rejection.Code
                RecoveryRejectionCode.InvalidRecoveryInput
                "Recovery identity"
        | _ -> failtest "Expected invalid recovery-identity refusal."

let private prepared value =
    match value with
    | PrepareOutcome.Prepared(details, review) -> details, review
    | _ -> failtest "Expected a retained advisory preparation."

let private preparationTests =
    testList
        "preparation"
        [
            testCase "prepare derives all review values from domain state" (fun () ->
                let operationId = Guid.Parse("40000000-0000-4000-8000-000000000001")

                let details, review =
                    (create ())
                        .Prepare(
                            boundRequest (draft operationId "TYPED-001"),
                            CancellationToken.None
                        )
                    |> waitFor
                    |> prepared

                Expect.equal details.Summary.OperationId operationId "Retained operation"
                Expect.isNone review.Before "Opening has no previous case"

                Expect.equal
                    review.Proposed.Fields.Status
                    CaseStatus.Opened
                    "Domain derives status"

                Expect.equal
                    review.Changes.Length
                    9
                    "Review contains every value that changes from an absent case"

                Expect.isTrue review.IsAdvisory "Preview is never commit authority")
        ]

let private queryTests =
    testList
        "queries"
        [
            testCase "missing history is a successful explicit lookup absence" (fun () ->
                let request =
                    {
                        CaseReference = "MISSING-001"
                        AfterCursor = None
                        Limit = 50
                        Detail = HistoryDetail.Summary
                    }

                match (create ()).History(request, CancellationToken.None) |> waitFor with
                | QueryOutcome.Succeeded(Lookup.NotFound reference) ->
                    Expect.equal reference "MISSING-001" "Absence retains its target"
                | _ -> failtest "Expected an explicit missing history lookup.")
            testCase "query cancellation never enters a storage read" (fun () ->
                use cancellation = new CancellationTokenSource()
                cancellation.Cancel()

                match (create ()).Get("TYPED-001", cancellation.Token) |> waitFor with
                | QueryOutcome.Cancelled -> ()
                | _ -> failtest "Expected typed cancellation before read admission.")
        ]

let private resolveExactRetained =
    testCase "recovery resolve executes an exact retained preparation once" (fun () ->
        let core = create ()
        invalidRecoveryInputs core
        let operationId = Guid.Parse("40000000-0000-4000-8000-000000000002")

        let details, _ =
            core.Prepare(boundRequest (draft operationId "TYPED-002"), CancellationToken.None)
            |> waitFor
            |> prepared

        let digest =
            details.Summary.RequestSha256
            |> Option.defaultWith (fun () -> failtest "Digest")

        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()

        match core.Recovery.Resolve(operationId, digest, cancelled.Token) |> waitFor with
        | ResolveOutcome.ResolveCancelledBeforeAdmission actualOperationId ->
            Expect.equal actualOperationId operationId "Cancelled recovery identity"
        | _ -> failtest "Expected cancellation before recovery admission."

        match core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> waitFor with
        | ResolveOutcome.ResolveCompleted(_, _, DefiniteExecution.Accepted receipt, _) ->
            Expect.equal receipt.Snapshot.Fields.CaseReference "TYPED-002" "Exact target committed"
        | _ -> failtest "Expected accepted recovery resolution."

        match core.Recovery.Inspect(operationId, None, 50, CancellationToken.None) |> waitFor with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RetainedInspection inspected)) ->
            Expect.equal
                inspected.Preparation.Summary.AvailableActions
                [ RecoveryAction.Export ]
                "Accepted evidence advertises export only"
        | _ -> failtest "Expected accepted recovery inspection.")

let private dismissedRecovery =
    testCase "dismissed recovery is refused before an attempt starts" (fun () ->
        let core = create ()
        let operationId = Guid.Parse("40000000-0000-4000-8000-000000000003")

        let details, _ =
            core.Prepare(boundRequest (draft operationId "TYPED-003"), CancellationToken.None)
            |> waitFor
            |> prepared

        let digest =
            details.Summary.RequestSha256
            |> Option.defaultWith (fun () -> failtest "Digest")

        match
            core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
            |> waitFor
        with
        | RecoveryDismissOutcome.DismissedPreparation _ -> ()
        | _ -> failtest "Expected retained preparation dismissal."

        match core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> waitFor with
        | ResolveOutcome.RefusedBeforeAttempt(_, rejection) ->
            Expect.equal rejection.Code RecoveryRejectionCode.OperationRevoked "Revocation wins"
        | _ -> failtest "Expected refusal without submission attempt.")

let private exactExport =
    testCase "recovery export requires the exact retained digest" (fun () ->
        let core = create ()
        let operationId = Guid.Parse("40000000-0000-4000-8000-000000000004")

        let details, _ =
            core.Prepare(boundRequest (draft operationId "TYPED-004"), CancellationToken.None)
            |> waitFor
            |> prepared

        let digest =
            details.Summary.RequestSha256
            |> Option.defaultWith (fun () -> failtest "Digest")

        let otherDigest = (if digest[0] = 'a' then "b" else "a") + digest.Substring(1)

        match
            core.Recovery.ExportEnvelope(operationId, otherDigest, CancellationToken.None)
            |> waitFor
        with
        | RecoveryQueryOutcome.RecoveryRejected rejection ->
            Expect.equal
                rejection.Code
                RecoveryRejectionCode.RecoveryIdempotencyConflict
                "Mismatch stays in the core"
        | _ -> failtest "Expected exact export identity refusal."

        match
            core.Recovery.ExportEnvelope(operationId, digest, CancellationToken.None)
            |> waitFor
        with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found artifact) ->
            Expect.equal artifact.RequestSha256 digest "Exact export remains available"
        | _ -> failtest "Expected exact recovery envelope export.")

let private observedIdentityConflict =
    testCase
        "[CC-REC-001] existing receipt with different bytes cannot masquerade as replay"
        (fun () ->
            let claims = new CoreStore.Store()
            let recovery = new CoreRecoveryStore.Store()
            let claimPort = claims :> IClaimStore
            recovery.AttachClaimStore(claimPort)
            let core = CoreApi.create claimPort (recovery :> IRecoveryStore) clock
            let operationId = Guid.NewGuid()

            let details, _ =
                core.Prepare(
                    boundRequest (draft operationId "TYPED-RETAINED"),
                    CancellationToken.None
                )
                |> waitFor
                |> prepared

            let digest =
                details.Summary.RequestSha256
                |> Option.defaultWith (fun () -> failtest "Retained digest is required.")

            let conflicting =
                draft operationId "TYPED-DIFFERENT"
                |> Drafts.bind
                |> Result.defaultWith (fun _ -> failtest "Synthetic conflict must bind.")

            match Service.executeAsync claimPort clock conflicting |> waitFor with
            | Ok _ -> ()
            | Error _ -> failtest "The synthetic conflicting receipt must exist."

            match
                core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> waitFor
            with
            | ResolveOutcome.RefusedBeforeAttempt(None, rejection) ->
                Expect.equal
                    rejection.Code
                    RecoveryRejectionCode.RecoveryIdempotencyConflict
                    "Conflicting receipt content is refused without retained metadata"
            | _ -> failtest "A different accepted request must never be returned as exact replay."

            Expect.equal recovery.StartCalls 0 "No recovery attempt on identity conflict"
            Expect.equal recovery.SettlementCalls 0 "No settlement on identity conflict")

let private recoveryTests =
    testList
        "recovery"
        [
            resolveExactRetained
            dismissedRecovery
            exactExport
            observedIdentityConflict
        ]

let tests =
    testList "typed core facade" [ preparationTests; queryTests; recoveryTests ]
