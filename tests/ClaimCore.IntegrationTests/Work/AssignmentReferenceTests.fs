module ClaimCore.IntegrationTests.AssignmentReferenceTests

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
                            Service.executeSubmission
                                admin
                                ((ClaimCore.TestSupport.Submissions.createRaw
                                    "case.open"
                                    (id number)
                                    (ClaimCore.Application.Contracts.OperationBinding.CaseNew(0L))
                                    (ClaimCore.TestSupport.Submissions.fields (
                                        RegistrationInput.values (registration)
                                     )
                                     |> Map.add
                                         (TaskInputId.Target(
                                             InputTarget.Field BusinessField.CaseReference
                                         ))
                                         (ClaimCore.Application.Contracts.SubmissionValue.Text(
                                             reference
                                         )))))
                                token

                        return (accepted reply |> caseReceipt).CaseId
                    }

                let! parent = openCase 1002 "ASSIGNMENT-PARENT" |> Async.AwaitTask
                let! other = openCase 1003 "ASSIGNMENT-OTHER" |> Async.AwaitTask

                let register number name create =
                    task {
                        let principal =
                            create "https://assignment.example/" name
                            |> Result.defaultWith (string >> failtest)

                        let! reply =
                            (task {
                                let! submission =
                                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                                        (admin).Store
                                        ((id number))
                                        (principal)
                                        (Some 0L)
                                        "people.register"
                                        (ClaimCore.TestSupport.Submissions.profile (
                                            {
                                                DisplayName = name
                                                Contact = None
                                                DigestEmail = None
                                            }
                                        ))

                                return! Service.executeSubmission (admin) submission (token)
                            })

                        let actorId =
                            match (accepted reply).Subject with
                            | ReceiptSubject.People receipt ->
                                PeopleTargetDisclosure.actorId receipt.TargetActor
                            | _ -> failtest "Expected people receipt"

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

                    acceptedPeople reply

                let! disabled =
                    (task {
                        let! submission =
                            ClaimCore.TestSupport.StoreFixtures.personSubmission
                                (database.Fixture.Store)
                                ((id 10))
                                (bobPrincipal)
                                (None)
                                "people.disable"
                                (Map.empty)

                        return! Service.executeSubmission (admin) submission (token)
                    })
                    |> Async.AwaitTask

                acceptedPeople disabled
                let pairs = [ parent, alice; parent, bob; other, alice; parent, service ]

                let! found =
                    database.Fixture.Store.Read
                        {
                            Work = (fun tx -> WorkReads.assignedAccounts tx pairs)
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
                    (DisplayName.Named(DomainInputs.name "Alice"))
                    "Current saved names are retained"

                Expect.equal
                    (account parent bob).DisplayName
                    (DisplayName.Named(DomainInputs.name "Bob"))
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
