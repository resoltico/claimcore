module ClaimCore.IntegrationTests.PaginationTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Postgres
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
                (Service.execute
                    admin
                    (ClaimCore.TestSupport.Inputs.caseOperation (
                        request n reference 0L (AuthoredCaseAct.Open registration)
                    ))
                    (token))

            accepted result |> ignore

        let principal =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                admin
                (OperationInput.Authority(
                    authorityRequest
                        (id 3)
                        principal
                        (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
                ))
                (token))

        acceptedAuthority registered

        let! granted =
            grant
                admin
                (id 4)
                principal
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted
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
                (id 5)
                principal
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                false

        acceptedAuthority revoked

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

        member _.Write
            ({
                 WriteOperation.Work = work
                 CancellationToken = ct
             })
            =
            inner.Write(
                {
                    WriteOperation.Work = work
                    CancellationToken = ct
                }
            )

        member _.Read
            ({
                 ReadOperation.Work = work
                 CancellationToken = ct
             })
            =
            inner.Read(
                {
                    ReadOperation.Work =
                        (fun tx ->
                            work
                                { new IReadTx with
                                    member _.AccessRequestPage query = tx.AccessRequestPage query
                                    member _.ObservedAt = tx.ObservedAt
                                    member _.Maintenance() = tx.Maintenance()

                                    member _.GlobalReadFacts principal =
                                        task {
                                            let! facts = tx.GlobalReadFacts principal
                                            pause ()
                                            return facts
                                        }


                                    member _.Authority
                                        ({
                                             AuthorityQuery.Principal = principal
                                             CaseId = scope
                                         })
                                        =
                                        task {
                                            let! actor =
                                                tx.Authority(
                                                    {
                                                        AuthorityQuery.Principal = principal
                                                        CaseId = scope
                                                    }
                                                )

                                            pause ()
                                            return actor
                                        }

                                    member _.FindCase id = tx.FindCase id

                                    member _.FindCaseByReference reference =
                                        tx.FindCaseByReference reference

                                    member _.AuthorityCases(principal, ids) =
                                        tx.AuthorityCases(principal, ids)

                                    member _.FindWork id = tx.FindWork id
                                    member _.FindReminder id = tx.FindReminder id

                                    member _.WorkSummaries(ids, today) =
                                        tx.WorkSummaries(ids, today)

                                    member _.AssignedAccounts pairs = tx.AssignedAccounts pairs

                                    member _.ResponsibilityCounts(selection, actors) =
                                        tx.ResponsibilityCounts(selection, actors)

                                    member _.ReminderPage query = tx.ReminderPage query

                                    member _.WorkOverview(selection, mine) =
                                        tx.WorkOverview(selection, mine)

                                    member _.CaseViewCounts selection = tx.CaseViewCounts selection

                                    member _.ReminderViewCounts selection =
                                        tx.ReminderViewCounts selection

                                    member _.ActivityPage query = tx.ActivityPage query
                                    member _.TimelinePage query = tx.TimelinePage query
                                    member _.CaseStageFacts id = tx.CaseStageFacts id
                                    member _.PreviousEntries entries = tx.PreviousEntries entries
                                    member _.FindOperation id = tx.FindOperation id
                                    member _.NonAcceptance key = tx.NonAcceptance key

                                    member _.CasePage
                                        ({
                                             CasePageQuery.DateBasis = basis
                                             Visibility = visibility
                                             ActorId = actor
                                             ReadRoles = roles
                                             ForbiddenRoles = invalid
                                             AfterPosition = after
                                             Filter = filter
                                             Limit = limit
                                         })
                                        =
                                        tx.CasePage(
                                            {
                                                CasePageQuery.DateBasis = basis
                                                Visibility = visibility
                                                ActorId = actor
                                                ReadRoles = roles
                                                ForbiddenRoles = invalid
                                                AfterPosition = after
                                                Filter = filter
                                                Limit = limit
                                            }
                                        )

                                    member _.CaseSummary query = tx.CaseSummary query

                                    member _.HistoryPage
                                        ({
                                             HistoryPageQuery.CaseId = id
                                             AfterRevision = after
                                             Limit = limit
                                         })
                                        =
                                        tx.HistoryPage(
                                            {
                                                HistoryPageQuery.CaseId = id
                                                AfterRevision = after
                                                Limit = limit
                                            }
                                        )

                                    member _.AuthorityHistoryPage query =
                                        tx.AuthorityHistoryPage query

                                    member _.ActorPage
                                        ({
                                             ActorPageQuery.AfterActorId = after
                                             Limit = limit
                                         })
                                        =
                                        tx.ActorPage(
                                            {
                                                ActorPageQuery.AfterActorId = after
                                                Limit = limit
                                            }
                                        )

                                    member _.GrantPage
                                        ({
                                             GrantPageQuery.ActorId = id
                                             AfterGrant = after
                                             Limit = limit
                                         })
                                        =
                                        tx.GrantPage(
                                            {
                                                GrantPageQuery.ActorId = id
                                                AfterGrant = after
                                                Limit = limit
                                            }
                                        )

                                    member _.ActorRoleSummaries ids = tx.ActorRoleSummaries ids
                                    member _.ActorRefs ids = tx.ActorRefs ids
                                    member _.FindActor id = tx.FindActor id
                                    member _.AssigneePage query = tx.AssigneePage query
                                    member _.EnabledHumanOwners() = tx.EnabledHumanOwners()
                                    member _.ActorCount() = tx.ActorCount()
                                })
                    CancellationToken = ct
                }
            )

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
            "Plain continuation uses current authority and does not restore registrar advice"
    }

let private snapshot (database: PostgresFixture.Database) =
    task {
        let! admin, principal, actor = setup database

        let! promoted =
            grant
                admin
                (id 30)
                principal
                {
                    Role = Role.CaseRegistrar
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority promoted

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
                    (id 5)
                    principal
                    {
                        Role = Role.CaseRegistrar
                        Scope = GrantTarget.Installation
                    }
                    false

            acceptedAuthority revoked

            let! later =
                (Service.execute
                    admin
                    (ClaimCore.TestSupport.Inputs.caseOperation (
                        request 13 "CASE-AFTER" 0L (AuthoredCaseAct.Open registration)
                    ))
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
        "database cursor authority"
        [
            testCaseAsync
                "a real grant revocation refuses continuation on the next page"
                (run revocation)
            testCaseAsync
                "cursor validation and page fetch share one snapshot across accepted concurrent changes"
                (run snapshot)
        ]
