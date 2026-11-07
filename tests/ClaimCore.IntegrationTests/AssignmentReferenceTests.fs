module ClaimCore.IntegrationTests.AssignmentReferenceTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

[<Tests>]
let tests =
    testCaseAsync
        "assignment references preserve every requested relation and current eligibility"
        (async {
            let! database = PostgresFixture.create () |> Async.AwaitTask

            try
                let id = StoreFixtures.id database.Fixture
                let! admin = caseWorker database.Fixture |> Async.AwaitTask
                let token = CancellationToken.None

                let openCase number reference =
                    task {
                        let! reply =
                            Service.execute
                                admin
                                (Inputs.caseOperation
                                    {
                                        OperationId = id number
                                        CaseReference = reference
                                        ExpectedRevision = 0L
                                        Act = AuthoredCaseAct.Open registration
                                    })
                                token

                        return (accepted reply |> caseReceipt).CaseId
                    }

                let! parent = openCase 2 "ASSIGNMENT-PARENT" |> Async.AwaitTask
                let! other = openCase 3 "ASSIGNMENT-OTHER" |> Async.AwaitTask

                let register number name create =
                    task {
                        let principal =
                            create "https://assignment.example/" name
                            |> Result.defaultWith (string >> failtest)

                        let! reply =
                            Service.execute
                                admin
                                (OperationInput.Authority(
                                    authorityRequest
                                        (id number)
                                        principal
                                        (AuthorityAct.Register
                                            { DisplayName = name; Contact = None })
                                ))
                                token

                        let actorId =
                            match (accepted reply).Subject with
                            | ReceiptSubject.Authority receipt ->
                                AuthorityTargetDisclosure.actorId receipt.TargetActor
                            | _ -> failtest "Expected authority receipt"

                        return principal, actorId
                    }

                let! alicePrincipal, alice =
                    register 4 "Alice" PrincipalKey.human |> Async.AwaitTask

                let! bobPrincipal, bob = register 5 "Bob" PrincipalKey.human |> Async.AwaitTask

                let! servicePrincipal, service =
                    register 6 "Service" PrincipalKey.service |> Async.AwaitTask

                for number, principal, scope in
                    [
                        7, alicePrincipal, GrantTarget.CaseId parent
                        8, bobPrincipal, GrantTarget.Installation
                        9, servicePrincipal, GrantTarget.CaseId parent
                    ] do
                    let! reply =
                        grant
                            admin
                            (id number)
                            principal
                            {
                                Role = Role.CaseHandler
                                Scope = scope
                            }
                            true
                        |> Async.AwaitTask

                    acceptedAuthority reply

                let! disabled =
                    Service.execute
                        admin
                        (OperationInput.Authority(
                            authorityRequest (id 10) bobPrincipal AuthorityAct.Disable
                        ))
                        token
                    |> Async.AwaitTask

                acceptedAuthority disabled
                let pairs = [ parent, alice; parent, bob; other, alice; parent, service ]

                let! found =
                    database.Fixture.Store.Read
                        {
                            Work = (fun tx -> tx.AssignedAccounts pairs)
                            CancellationToken = token
                        }
                    |> Async.AwaitTask

                let actual = found |> Result.defaultWith (fun error -> failtestf "%A" error)

                Expect.equal
                    actual.Count
                    4
                    "Historical before/after actors on the same case cannot overwrite one another"

                let account parent actor = Map.find (parent, actor) actual

                Expect.equal
                    (account parent alice).DisplayName
                    (DisplayName.Named(Inputs.name "Alice"))
                    "Current saved names are retained"

                Expect.equal
                    (account parent bob).DisplayName
                    (DisplayName.Named(Inputs.name "Bob"))
                    "Disabled actors keep their actual current name"

                Expect.equal
                    (account parent alice).Availability
                    ActorAvailability.Available
                    "Case-local assignment roles apply to the requested relation"

                Expect.equal
                    (account other alice).Availability
                    ActorAvailability.NotEligible
                    "Roles on one case do not make another relation eligible"

                Expect.equal
                    (account parent bob).Availability
                    ActorAvailability.Disabled
                    "Disabled status is independent of role eligibility"

                Expect.equal
                    (account parent service).Availability
                    ActorAvailability.NotEligible
                    "Service accounts are never assignable"
            finally
                database.Fixture.Dispose()
        })
