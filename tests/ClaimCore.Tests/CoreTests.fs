module ClaimCore.Tests.CoreTests

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let private clock = businessTime today

let private waitFor (task: Task<'value>) = task.GetAwaiter().GetResult()

let private openValues =
    [
        "incidentDate", registration.IncidentDate
        "incidentNotificationDate", registration.IncidentNotificationDate
        "incidentCountry", registration.IncidentCountry
        "claimantName", registration.ClaimantName
        "insurerName", registration.InsurerName
        "claimedAmount", registration.ClaimedAmount
        "claimedCurrency", registration.ClaimedCurrency
    ]

let private draft operationId reference expectedVersion kind values : CommandDraft =
    {
        OperationId = operationId
        CaseReference = reference
        ExpectedVersion = expectedVersion
        Command = DraftCommand.Flat(kind, values)
    }

let private openDraft operationId reference =
    draft operationId reference 0L CommandKind.Open openValues

let private createWithClock businessClock =
    let claims = new CoreStore.Store()
    let recovery = new CoreRecoveryStore.Store()
    recovery.AttachClaimStore(claims :> IClaimStore)

    CoreApi.create (claims :> IClaimStore) (recovery :> IRecoveryStore) businessClock

let private create () = createWithClock clock

let private expectAccepted outcome =
    match outcome with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted receipt,
                                  SettlementConfirmation.Confirmed) -> receipt
    | _ -> failtest "Expected a definite accepted execution with confirmed settlement."

let private expectOpen (core: IClaimsCore) operationId reference =
    core.Execute(boundRequest (openDraft operationId reference), CancellationToken.None)
    |> waitFor
    |> expectAccepted

let private descriptionTests =
    testList
        "typed discovery"
        [
            testCase "description separates static contract from runtime context" (fun () ->
                let description = (create ()).Describe()
                Expect.equal description.Contract.Fields.Length 13 "Exact business-field count"

                Expect.equal
                    description.Contract.Commands
                    CommandDefinitions.all
                    "Command descriptors"

                Expect.equal
                    description.Runtime.EffectiveBusinessDate
                    today
                    "Injected business date"

                Expect.isTrue
                    ((SemanticCoreFingerprint.value description.SemanticFingerprint).Length > 0)
                    "Fingerprint")
        ]

let private openAndRead =
    testCase "execute commits an open case and get exposes derived actions" (fun () ->
        let core = create ()
        let operationId = Guid.Parse("20000000-0000-4000-8000-000000000101")
        let receipt = expectOpen core operationId "CORE-001"

        Expect.equal receipt.Snapshot.Fields.CaseReference "CORE-001" "Receipt target"
        Expect.equal receipt.Snapshot.Fields.Status CaseStatus.Opened "Receipt status"

        match core.Get("CORE-001", CancellationToken.None) |> waitFor with
        | QueryOutcome.Succeeded(Lookup.Found current) ->
            Expect.equal current.Record.Fields.Status CaseStatus.Opened "Core-derived status"
            Expect.contains current.AvailableCommands CommandKind.Decide "Core-derived action"
        | _ -> failtest "Expected current typed case.")

let private exactRetry =
    testCase "exact retry observes the original receipt without another commit" (fun () ->
        let mutable effectiveDate = today

        let changingClock =
            { new IBusinessTime with
                member _.Capture() = businessContext effectiveDate
            }

        let core = createWithClock changingClock
        let operationId = Guid.Parse("20000000-0000-4000-8000-000000000102")
        let command = openDraft operationId "CORE-002"

        core.Execute(boundRequest command, CancellationToken.None)
        |> waitFor
        |> expectAccepted
        |> ignore

        effectiveDate <- today.AddDays(1)

        match core.Execute(boundRequest command, CancellationToken.None) |> waitFor with
        | SubmissionOutcome.ObservedAccepted receipt ->
            Expect.equal receipt.OperationId operationId "Original operation"
            Expect.isTrue receipt.Replayed "Exact retry is replayed"
        | _ -> failtest "Expected observed exact receipt.")

let private rejections =
    testList
        "idempotency and concurrency rejections"
        [
            testCase "operation-ID content conflict is an explicit typed rejection" (fun () ->
                let core = create ()
                let operationId = Guid.Parse("20000000-0000-4000-8000-000000000103")
                expectOpen core operationId "CORE-003" |> ignore

                match
                    core.Execute(
                        boundRequest (openDraft operationId "CORE-003-CHANGED"),
                        CancellationToken.None
                    )
                    |> waitFor
                with
                | SubmissionOutcome.RejectedBeforeAttempt(_, rejection) ->
                    Expect.equal
                        rejection.Code
                        RejectionCode.IdempotencyConflict
                        "Content-bound identity"
                | _ -> failtest "Expected idempotency rejection before an attempt.")
            testCase "stale command is rejected before recovery attempt admission" (fun () ->
                let core = create ()

                expectOpen core (Guid.Parse("20000000-0000-4000-8000-000000000104")) "CORE-004"
                |> ignore

                let close =
                    draft
                        (Guid.Parse("20000000-0000-4000-8000-000000000105"))
                        "CORE-004"
                        0L
                        CommandKind.Close
                        []

                match core.Execute(boundRequest close, CancellationToken.None) |> waitFor with
                | SubmissionOutcome.RejectedBeforeAttempt(_, rejection) ->
                    Expect.equal
                        rejection.Code
                        RejectionCode.VersionConflict
                        "Expected revision revalidated"

                    Expect.equal rejection.Action RecommendedAction.ReadCurrent "Safe next action"
                | _ -> failtest "Expected stale command rejection.")
        ]

let private cancellation =
    testCase "caller cancellation before admission proves no execution" (fun () ->
        let core = create ()
        let operationId = Guid.Parse("20000000-0000-4000-8000-000000000106")
        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()

        match
            core.Execute(boundRequest (openDraft operationId "CORE-005"), cancelled.Token)
            |> waitFor
        with
        | SubmissionOutcome.CancelledBeforeAdmission value ->
            Expect.equal value operationId "Cancelled operation identity"
        | _ -> failtest "Expected typed cancellation before admission."

        match core.Get("CORE-005", CancellationToken.None) |> waitFor with
        | QueryOutcome.Succeeded(Lookup.NotFound _) -> ()
        | _ -> failtest "Cancelled execution must not create a case.")

let private unknownPreparationState =
    testCase "execute retains identity when preparation state is unknown" (fun () ->
        let operationId = Guid.Parse("20000000-0000-4000-8000-000000000107")
        let command = openDraft operationId "CORE-006"

        let request =
            match Drafts.bind command with
            | Ok value -> value
            | Error _ -> failtest "Expected a valid command draft."

        let expectedDigest =
            RequestRecord.encode request |> SHA256.HashData |> Convert.ToHexStringLower

        let recovery =
            new CoreRecoveryStore.Store(RecoveryStoreFailure.TechnicalMutationUnknown)

        let core =
            CoreApi.create
                (new CoreStore.Store() :> IClaimStore)
                (recovery :> IRecoveryStore)
                clock

        match core.Execute(boundRequest command, CancellationToken.None) |> waitFor with
        | SubmissionOutcome.PreparationStateUnknown(actualOperationId, digest, fault) ->
            Expect.equal actualOperationId operationId "Ambiguous retain operation identity"
            Expect.equal digest expectedDigest "Ambiguous retain canonical request digest"
            Expect.equal fault.Code FaultCode.TechnicalMutationUnknown "Exact recovery fault"
            Expect.equal fault.Action RecommendedAction.RecoverExact "Safe recovery action"
        | _ -> failtest "Expected an explicit unknown preparation state.")

let private executionTests =
    testList
        "typed execution"
        [ openAndRead; exactRetry; rejections; cancellation; unknownPreparationState ]

let tests = testList "typed core execution" [ descriptionTests; executionTests ]
