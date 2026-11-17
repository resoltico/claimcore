module ClaimCore.IntegrationTests.PaginationTests

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private query after limit : PageRequest = { AfterCursor = after; Limit = limit }

let private page (outcome: Outcome<CaseSummaryPage>) =
    match outcome with
    | Outcome.Succeeded value -> value
    | _ -> failtest "Expected authorized page"

let private reference (summary: CaseSummary) = CaseReference.format summary.Reference

let private setup (database: PostgresFixture.Database) =
    task {
        let! admin = caseWorker database.Fixture

        for n, reference in [ 10, "CASE-A"; 11, "CASE-B"; 12, "CASE-C" ] do
            let! result =
                (Service.executeSubmission
                    admin
                    (((request
                        (database.Fixture)
                        (n)
                        (reference)
                        (0L)
                        "case.open"
                        (ClaimCore.TestSupport.Submissions.fields (
                            RegistrationInput.values (registration)
                        )))))
                    (token))

            accepted result |> ignore

        let principal =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (database.Fixture.Store)
                        ((id database.Fixture 3))
                        (principal)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (admin) submission ((token))
            }))

        acceptedPeople registered

        let! granted =
            grant
                admin
                (id database.Fixture 4)
                principal
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted
        return admin, principal, core database.Fixture principal
    }

let private revocation (database: PostgresFixture.Database) =
    task {
        let! admin, principal, actor = setup database

        let! first =
            (Service.list actor ((ClaimCore.TestSupport.Inputs.caseListPage (query None 1))) (token))

        let cursor = (page first).NextCursor
        Expect.isSome cursor "Real database issues a continuation"

        let! revoked =
            grant
                admin
                (id database.Fixture 5)
                principal
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                false

        acceptedPeople revoked

        let! next =
            (Service.list
                actor
                ((ClaimCore.TestSupport.Inputs.caseListPage (query cursor 2)))
                (token))

        Expect.equal
            next
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Current grants are rechecked at the real database boundary"
    }

type internal PausedPageStore
    (inner: IStore, entered: TaskCompletionSource, release: ManualResetEventSlim) =
    let pause () =
        entered.TrySetResult() |> ignore

        if not (release.Wait(TimeSpan.FromSeconds 3.0)) then
            failtest "Page release was not signalled"

    interface IStore with
        member _.Context query = inner.Context query
        member _.Write operation = inner.Write operation

        member _.Read operation =
            inner.Read
                { operation with
                    Work =
                        fun tx ->
                            pause ()
                            operation.Work tx
                }

let private freshAdvice (actor: ServiceContext) cursor current =
    task {
        Expect.isTrue
            ((page current).Offers |> List.exists (fun o -> o = InstallationOffer.Open))
            "Paused rows retain the same registrar snapshot advice"

        let! fresh =
            (Service.list
                actor
                ((ClaimCore.TestSupport.Inputs.caseListPage (query None 50)))
                (token))

        Expect.isFalse
            ((page fresh).Offers |> List.exists (fun o -> o = InstallationOffer.Open))
            "Fresh reader page has no OPEN advice"

        let! next =
            (Service.list
                actor
                ((ClaimCore.TestSupport.Inputs.caseListPage (query cursor 50)))
                (token))

        Expect.isFalse
            ((page next).Offers |> List.exists (fun offer -> offer = InstallationOffer.Open))
            "Plain continuation uses current people and does not restore registrar advice"
    }

let private snapshot (database: PostgresFixture.Database) =
    task {
        let! admin, principal, actor = setup database

        let! promoted =
            grant
                admin
                (id database.Fixture 30)
                principal
                {
                    Role = Role.CaseRegistrar
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople promoted

        let! first =
            (Service.list actor ((ClaimCore.TestSupport.Inputs.caseListPage (query None 1))) (token))

        let cursor = (page first).NextCursor

        let entered =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        use release = new ManualResetEventSlim(false)
        let store = PausedPageStore(database.Fixture.Store, entered, release)

        let facade =
            Service.create
                BuildIdentity.current.Version
                database.Fixture.InstallationId
                (store :> IStore)
                database.Fixture.Calendar

                principal

        let reading =
            Task.Run<Outcome<CaseSummaryPage>>(
                Func<Task<Outcome<CaseSummaryPage>>>(fun () ->
                    (Service.list
                        facade
                        ((ClaimCore.TestSupport.Inputs.caseListPage (query cursor 50)))
                        (token)))
            )

        try
            do! entered.Task.WaitAsync(TimeSpan.FromSeconds 3.0)

            let! revoked =
                grant
                    admin
                    (id database.Fixture 5)
                    principal
                    {
                        Role = Role.CaseRegistrar
                        Scope = GrantTarget.Installation
                    }
                    false

            acceptedPeople revoked

            let! later =
                (Service.executeSubmission
                    admin
                    (((request
                        (database.Fixture)
                        (13)
                        ("CASE-AFTER")
                        (0L)
                        "case.open"
                        (ClaimCore.TestSupport.Submissions.fields (
                            RegistrationInput.values (registration)
                        )))))
                    (token))

            accepted later |> ignore
        finally
            release.Set()

        let! current = reading

        Expect.equal
            ((page current).Items |> List.map reference)
            [ "CASE-B"; "CASE-C" ]
            "Authorization and data share the earlier read snapshot"

        do! freshAdvice actor cursor current
    }

[<Tests>]
let tests =
    testList
        "database cursor people"
        [
            testCaseAsync
                "a real grant revocation refuses continuation on the next page"
                (run revocation)
            testCaseAsync
                "cursor validation and page fetch share one snapshot across accepted concurrent changes"
                (run snapshot)
        ]
