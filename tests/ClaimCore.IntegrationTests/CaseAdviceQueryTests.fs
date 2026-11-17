module ClaimCore.IntegrationTests.CaseAdviceQueryTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testCaseAsync
        "case advice reasons share execution permissions state and maintenance precedence"
        (async {
            let token = CancellationToken.None

            let principal name =
                PrincipalKey.human "https://issuer.example/" name
                |> Result.defaultWith (fun _ -> failtest "Principal")

            let owner, reader, payment, hidden =
                principal "owner", principal "reader", principal "payment", principal "hidden"

            let! database = PostgresFixture.createFor owner |> Async.AwaitTask

            try
                let id = StoreFixtures.id database.Fixture
                let! admin = StoreFixtures.caseWorker database.Fixture |> Async.AwaitTask

                for index, (target, role) in
                    List.indexed
                        [
                            reader, Role.CaseReader
                            payment, Role.PaymentRecorder
                            hidden, Role.Owner
                        ] do
                    let! registered =
                        (task {
                            let! submission =
                                ClaimCore.TestSupport.StoreFixtures.personSubmission
                                    (admin).Store
                                    ((id (100 + index * 2)))
                                    (target)
                                    (Some 0L)
                                    "people.register"
                                    (ClaimCore.TestSupport.Submissions.profile (
                                        DomainInputs.actorProfile
                                    ))

                            return! Service.executeSubmission (admin) submission (token)
                        })
                        |> Async.AwaitTask

                    StoreFixtures.acceptedPeople registered

                    let! granted =
                        StoreFixtures.grant
                            admin
                            (id (101 + index * 2))
                            target
                            {
                                Role = role
                                Scope = GrantTarget.Installation
                            }
                            true
                        |> Async.AwaitTask

                    StoreFixtures.acceptedPeople granted

                let core principal =
                    StoreFixtures.core database.Fixture principal

                let write n principal revision taskName values =
                    Service.executeSubmission
                        (core principal)
                        (StoreFixtures.request
                            database.Fixture
                            n
                            "ADVICE-1"
                            revision
                            taskName
                            values)
                        token

                let! opened =
                    write
                        (10)
                        (owner)
                        (0L)
                        "case.open"
                        (ClaimCore.TestSupport.Submissions.fields (
                            RegistrationInput.values (DomainInputs.registration)
                        ))
                    |> Async.AwaitTask

                match opened.Outcome with
                | ExecuteResult.Accepted _ -> ()
                | other -> failtestf "Open: %A" other

                let get principal =
                    Service.get
                        (core principal)
                        {
                            Target = CaseSelector.Reference "ADVICE-1"
                        }
                        token

                let read =
                    function
                    | Outcome.Succeeded value -> value
                    | other -> failtestf "Read: %A" other

                let reason act (advice: CaseAdvice) =
                    advice.Blocked |> List.find (fun item -> item.Act = act) |> _.Reason

                let! current = get owner |> Async.AwaitTask
                let current = read current

                Expect.equal
                    (reason (TaskId.ofLiteral "case.recordPayment") current.Advice)
                    (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Undecided))
                    "An authorized payment needs a recorded decision"

                Expect.equal
                    current.Advice.Recommended
                    (Some(TaskId.ofLiteral "case.decide"))
                    "The alternative is actually offered"

                Expect.equal
                    (Set.union
                        (Set.ofList current.Advice.Acts)
                        (current.Advice.Blocked |> List.map _.Act |> Set.ofList))
                    (Set.ofList (
                        [
                            "case.open"
                            "case.amendRegistration"
                            "case.correct"
                            "case.decide"
                            "case.withdrawDecision"
                            "case.recordPayment"
                            "case.removePayment"
                            "case.correctPayment"
                            "case.void"
                            "case.reinstate"
                            "case.close"
                            "case.reopen"
                            "case.erase"
                        ]
                        |> List.map TaskId.ofLiteral
                    ))
                    "The actual producer covers the literal act inventory"

                let! refused =
                    write
                        (11)
                        (owner)
                        (1L)
                        "case.recordPayment"
                        (ClaimCore.TestSupport.Submissions.fields (
                            PaymentInput.values (DomainInputs.payment)
                        ))
                    |> Async.AwaitTask

                Expect.equal
                    refused.Outcome
                    (ExecuteResult.Rejected(
                        Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Undecided)
                    ))
                    "Execution agrees with the literal state reason"

                let! current = get reader |> Async.AwaitTask
                let current = read current

                Expect.equal
                    (reason (TaskId.ofLiteral "case.recordPayment") current.Advice)
                    (Refusal.Access(AccessRefusal.NotAuthorized Capability.RecordPayment))
                    "A readable denied action reports capability before state"

                let! refused =
                    write
                        (12)
                        (reader)
                        (1L)
                        "case.recordPayment"
                        (ClaimCore.TestSupport.Submissions.fields (
                            PaymentInput.values (DomainInputs.payment)
                        ))
                    |> Async.AwaitTask

                Expect.equal
                    refused.Outcome
                    (ExecuteResult.Rejected(
                        Refusal.Access(AccessRefusal.NotAuthorized Capability.RecordPayment)
                    ))
                    "Execution enforces the same permission first"

                let! closed = write (13) (owner) (1L) "case.close" (Map.empty) |> Async.AwaitTask

                match closed.Outcome with
                | ExecuteResult.Accepted _ -> ()
                | other -> failtestf "Close: %A" other

                let! current = get payment |> Async.AwaitTask

                Expect.equal
                    (reason (TaskId.ofLiteral "case.correct") (read current).Advice)
                    (Refusal.Access(AccessRefusal.NotAuthorized Capability.Register))
                    "A payment-only actor cannot correct a closed case with no financial group"

                let! entered =
                    RegisterMaintenanceStore.change
                        database.OwnerSource
                        (MaintenanceChange.Acquire(
                            0L,
                            System.Guid.Parse "10000000-0000-4000-8000-000000000099"
                        ))
                        token
                    |> Async.AwaitTask

                Expect.isTrue entered.State.Active "The owner pauses register writes"
                let! current = get owner |> Async.AwaitTask

                Expect.equal
                    (reason (TaskId.ofLiteral "case.reopen") (read current).Advice)
                    Refusal.RegisterMaintenance
                    "Maintenance blocks otherwise permitted reopening"

                Expect.equal
                    (reason (TaskId.ofLiteral "case.void") (read current).Advice)
                    (Refusal.Access(AccessRefusal.NotAuthorized Capability.Steward))
                    "Maintenance does not replace a missing permission"

                let! unavailable = get hidden |> Async.AwaitTask

                Expect.equal
                    unavailable
                    (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                    "Unreadable cases never expose advice or state"
            finally
                database.Fixture.Dispose()
        })
