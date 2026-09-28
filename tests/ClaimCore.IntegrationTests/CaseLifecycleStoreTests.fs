module ClaimCore.IntegrationTests.CaseLifecycleStoreTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private cancellation = CancellationToken.None

let private applied =
    function
    | LifecycleWriteOutcome.Applied _ -> ()
    | _ -> failtest "Expected definite witnessed lifecycle result, got ."

let internal review (actor: IActorClaimsCore) reference =
    match actor.Lifecycle.Review(reference, cancellation) |> await with
    | LifecycleReviewOutcome.Available value -> value
    | _ -> failtest "Expected authorized lifecycle review, got ."

let internal change eventId reference (current: LifecycleReview) action =
    {
        EventId = eventId
        CaseReference = reference
        ExpectedRevision = current.BusinessRevision
        ExpectedLifecycleSequence = current.LifecycleSequence
        ExpectedLifecycleHash = current.LifecycleHash
        Action = action
    }

let private openCase (actor: IActorClaimsCore) =
    let request = openRequest (Guid.NewGuid()) ("LIFE-" + Guid.NewGuid().ToString("N"))

    match actor.Execute(request, cancellation) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> request
    | _ -> failtest "Synthetic OPEN was not definitely accepted: ."

let internal executeAccepted (actor: IActorClaimsCore) request =
    match actor.Execute(request, cancellation) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> ()
    | _ -> failtest "Synthetic transition was not accepted: ."

let internal setup = CaseLifecycleStoreFixture.setup

let internal voidCase owner (actor: IActorClaimsCore) (outsider: IActorClaimsCore) =
    let request = openCase actor
    let current = review actor request.CaseReference
    Expect.equal current.Disposition CaseDisposition.Active "Initial disposition"
    Expect.equal current.LifecycleSequence 0L "Fresh lifecycle sequence"
    let unavailable = LifecycleReviewOutcome.ResourceUnavailable

    Expect.equal
        (outsider.Lifecycle.Review(request.CaseReference, cancellation) |> await)
        unavailable
        "Ungranted case must be hidden"

    Expect.equal
        (actor.Lifecycle.Review("NO-SUCH-LIFECYCLE-CASE", cancellation) |> await)
        unavailable
        "Absent and inaccessible use the same public refusal"

    let voidChange =
        change
            (Guid.NewGuid())
            request.CaseReference
            current
            (LifecycleMutation.VoidDataEntryError "Synthetic data entry error")

    actor.Lifecycle.Apply(voidChange, cancellation) |> await |> applied

    match actor.Get(request.CaseReference, cancellation) |> await with
    | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
    | _ -> failtest "Void must fence ordinary case read."

    let blocked = review actor request.CaseReference
    Expect.equal blocked.Disposition CaseDisposition.VoidedDataEntryError "Voided"
    Expect.equal blocked.BusinessRevision 2L "Disposition advances revision"
    Expect.equal blocked.LifecycleSequence 1L "One witnessed lifecycle event"
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use query =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.case_changes WHERE case_reference=@reference",
            connection
        )

    Sql.text query "reference" request.CaseReference
    Expect.equal (query.ExecuteScalar() :?> int64) 1L "Business history remains"
    request, blocked

let internal reinstateCase
    (actor: IActorClaimsCore)
    (approvers: IActorClaimsCore list)
    (request: CommandRequest)
    (blocked: LifecycleReview)
    =
    let reinstate =
        change
            (Guid.NewGuid())
            request.CaseReference
            blocked
            (LifecycleMutation.ReinstateVoided "Synthetic correction reviewed")

    match actor.Lifecycle.Apply(reinstate, cancellation) |> await with
    | LifecycleWriteOutcome.Refused LifecycleRefusal.ApprovalRequired -> ()
    | _ -> failtest "Unapproved reinstatement must refuse."

    let expiry = DateTimeOffset.UtcNow.AddHours(1.0)

    for approver in approvers do
        approver.Lifecycle.Approve(reinstate, Guid.NewGuid(), expiry, cancellation)
        |> await
        |> applied

    actor.Lifecycle.Apply(reinstate, cancellation) |> await |> applied
    let restored = review actor request.CaseReference
    Expect.equal restored.Disposition CaseDisposition.Active "Reinstated"
    Expect.equal restored.BusinessRevision 3L "Reinstatement advances revision"
    Expect.equal restored.LifecycleSequence 2L "Two lifecycle events"

    match actor.Get(request.CaseReference, cancellation) |> await with
    | QueryOutcome.Succeeded(Lookup.Found _) -> ()
    | _ -> failtest "Reinstated case is available to its editor."

    actor.Lifecycle.Apply(reinstate, cancellation) |> await |> applied

let private voidAndReinstate =
    testCase
        "[CC-LIFE-001] witnessed void preserves history and reinstatement requires distinct stewards"
        (fun _ ->
            setup (fun owner _ _ runtime proposer firstApprover secondApprover ungranted _ ->
                let actor = runtime.ForActor proposer
                let request, blocked = voidCase owner actor (runtime.ForActor ungranted)

                reinstateCase
                    actor
                    [ runtime.ForActor firstApprover; runtime.ForActor secondApprover ]
                    request
                    blocked))

let private erasureFenceAndHold =
    testCase
        "[CC-LIFE-001] witnessed erasure request and hold fence ordinary work without claiming purge"
        (fun _ ->
            setup (fun _ _ _ runtime proposer _ _ _ _ ->
                let actor = runtime.ForActor proposer
                let request = openCase actor
                let before = review actor request.CaseReference
                let holdId = Guid.NewGuid()

                let hold =
                    change
                        (Guid.NewGuid())
                        request.CaseReference
                        before
                        (LifecycleMutation.RecordHold(
                            holdId,
                            "Synthetic retention ground",
                            DateOnly.FromDateTime(DateTime.UtcNow.AddDays 10.0)
                        ))

                actor.Lifecycle.Apply(hold, cancellation) |> await |> applied
                let held = review actor request.CaseReference

                Expect.equal
                    (held.ActiveHolds |> List.map _.HoldId)
                    [ holdId ]
                    "Hold is independent"

                let erase =
                    change
                        (Guid.NewGuid())
                        request.CaseReference
                        held
                        (LifecycleMutation.RequestErasure "Synthetic privacy request")

                actor.Lifecycle.Apply(erase, cancellation) |> await |> applied
                let requested = review actor request.CaseReference
                Expect.equal requested.PrivacyPhase PrivacyPhase.ErasureRequested "Request only"
                Expect.equal requested.ActiveHolds.Length 1 "Hold still blocks purge"

                match actor.Get(request.CaseReference, cancellation) |> await with
                | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
                | _ -> failtest "Erasure request must immediately fence ordinary reads."

                match actor.Execute(next request 1L Command.Close, cancellation) |> await with
                | SubmissionOutcome.RejectedBeforeAttempt(None, Rejection.ResourceUnavailable) ->
                    ()
                | _ -> failtest "Erasure request must fence ordinary commands."))

let private historicalPaymentVoid =
    testCase
        "[CC-LIFE-001] historical payment assertion requires two witnessed void approvals"
        (fun _ ->
            setup (fun _ _ _ runtime proposer firstApprover secondApprover _ _ ->
                let actor = runtime.ForActor proposer
                let opened = openCase actor

                let decision =
                    next
                        opened
                        1L
                        (Command.Decide
                            {
                                PaymentDecisionDate = "2026-08-04"
                                PayableAmount = "100"
                                PayableCurrency = "EUR"
                            })

                executeAccepted actor decision
                let paid = next decision 2L (Command.RecordPayment "2026-08-05")
                executeAccepted actor paid
                executeAccepted actor (next paid 3L Command.ClearPayment)
                let current = review actor opened.CaseReference
                Expect.isTrue current.VoidRequiresTwoApprovals "Prior payment survives clearing"

                let voidChange =
                    change
                        (Guid.NewGuid())
                        opened.CaseReference
                        current
                        (LifecycleMutation.VoidDataEntryError "Synthetic paid-entry error")

                match actor.Lifecycle.Apply(voidChange, cancellation) |> await with
                | LifecycleWriteOutcome.Refused LifecycleRefusal.ApprovalRequired -> ()
                | _ -> failtest "Unapproved paid void must refuse."

                let expiry = DateTimeOffset.UtcNow.AddHours 1.0

                for principal in [ firstApprover; secondApprover ] do
                    (runtime.ForActor principal)
                        .Lifecycle.Approve(voidChange, Guid.NewGuid(), expiry, cancellation)
                    |> await
                    |> applied

                actor.Lifecycle.Apply(voidChange, cancellation) |> await |> applied

                Expect.equal
                    (review actor opened.CaseReference).Disposition
                    CaseDisposition.VoidedDataEntryError
                    "Paid case is voided without deletion"))

let tests =
    testList
        "case lifecycle storage"
        [ voidAndReinstate; erasureFenceAndHold; historicalPaymentVoid ]
