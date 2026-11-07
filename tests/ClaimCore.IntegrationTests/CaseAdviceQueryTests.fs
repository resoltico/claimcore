module ClaimCore.IntegrationTests.CaseAdviceQueryTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.TestSupport

let private id n =
    Guid.Parse(sprintf "d0000000-0000-4000-8000-%012d" n)

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
                let! admin = StoreFixtures.caseWorker database.Fixture |> Async.AwaitTask

                for index, (target, role) in
                    List.indexed
                        [
                            reader, Role.CaseReader
                            payment, Role.PaymentRecorder
                            hidden, Role.Owner
                        ] do
                    let! registered =
                        Service.execute
                            admin
                            (OperationInput.Authority(
                                Inputs.authorityRequest
                                    (id (100 + index * 2))
                                    target
                                    (AuthorityAct.Register Inputs.actorProfile)
                            ))
                            token
                        |> Async.AwaitTask

                    StoreFixtures.acceptedAuthority registered

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

                    StoreFixtures.acceptedAuthority granted

                let core principal =
                    StoreFixtures.core database.Fixture principal

                let write n principal revision act =
                    Service.execute
                        (core principal)
                        (Inputs.caseOperation
                            {
                                OperationId = id n
                                CaseReference = "ADVICE-1"
                                ExpectedRevision = revision
                                Act = act
                            })
                        token

                let! opened =
                    write 10 owner 0L (AuthoredCaseAct.Open Inputs.registration) |> Async.AwaitTask

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
                    (reason CaseActKind.RecordPayment current.Advice)
                    (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Undecided))
                    "An authorized payment needs a recorded decision"

                Expect.equal
                    current.Advice.Recommended
                    (Some CaseActKind.Decide)
                    "The alternative is actually offered"

                Expect.equal
                    (Set.union
                        (Set.ofList current.Advice.Acts)
                        (current.Advice.Blocked |> List.map _.Act |> Set.ofList))
                    (Set.ofList CaseActKind.all)
                    "The actual producer covers the literal act inventory"

                let! refused =
                    write 11 owner 1L (AuthoredCaseAct.RecordPayment Inputs.payment)
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
                    (reason CaseActKind.RecordPayment current.Advice)
                    (Refusal.Access(AccessRefusal.NotAuthorized Capability.RecordPayment))
                    "A readable denied action reports capability before state"

                let! refused =
                    write 12 reader 1L (AuthoredCaseAct.RecordPayment Inputs.payment)
                    |> Async.AwaitTask

                Expect.equal
                    refused.Outcome
                    (ExecuteResult.Rejected(
                        Refusal.Access(AccessRefusal.NotAuthorized Capability.RecordPayment)
                    ))
                    "Execution enforces the same permission first"

                let! closed =
                    write 13 owner 1L (AuthoredCaseAct.Bare BareAct.Close) |> Async.AwaitTask

                match closed.Outcome with
                | ExecuteResult.Accepted _ -> ()
                | other -> failtestf "Close: %A" other

                let! current = get payment |> Async.AwaitTask

                Expect.equal
                    (reason CaseActKind.Correct (read current).Advice)
                    (Refusal.Access(AccessRefusal.NotAuthorized Capability.Register))
                    "A payment-only actor cannot correct a closed case with no financial group"

                let! entered =
                    RegisterMaintenanceStore.change
                        database.OwnerSource
                        MaintenanceChange.Enter
                        token
                    |> Async.AwaitTask

                Expect.isOk entered "The owner enters maintenance"
                let! current = get owner |> Async.AwaitTask

                Expect.equal
                    (reason CaseActKind.Reopen (read current).Advice)
                    Refusal.RegisterMaintenance
                    "Maintenance blocks otherwise permitted reopening"

                Expect.equal
                    (reason CaseActKind.Void (read current).Advice)
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
