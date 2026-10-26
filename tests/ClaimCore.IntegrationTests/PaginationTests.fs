module ClaimCore.IntegrationTests.PaginationTests

open ClaimCore.TestSupport

open ClaimCore.Contract

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private query after limit : PageRequest = { AfterCursor = after; Limit = limit }

let private page (outcome: ReadReply<CaseSummaryPage>) =
    match outcome with
    | ReadReply.Succeeded value -> value
    | _ -> failtest "Expected authorized page"

let private reference (summary: CaseSummary) = CaseReference.format summary.Reference

let private setup (database: PostgresFixture.Database) =
    task {
        let! admin = caseWorker database.Fixture

        for n, reference in [ 10, "CASE-A"; 11, "CASE-B"; 12, "CASE-C" ] do
            let! result =
                (Core.execute
                    admin
                    (OperationInput.Case(
                        request n reference 0L (UnvalidatedCaseAct.Open registration)
                    ))
                    (token))

            accepted result |> ignore

        let principal =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith failtest

        let! registered =
            (Core.execute
                admin
                (OperationInput.Authority(
                    authorityRequest (id 3) principal AdmittedAuthorityAct.Register
                ))
                (token))

        acceptedAuthority registered

        let! granted =
            grant admin (id 4) principal Role.CaseReader GrantTarget.Installation true

        acceptedAuthority granted
        return admin, principal, core database.Fixture principal
    }

let private revocation (database: PostgresFixture.Database) =
    task {
        let! admin, principal, actor = setup database
        let! first = (Core.list actor (query None 1) (token))
        let cursor = (page first).NextCursor
        Expect.isSome cursor "Real database issues a continuation"

        let! revoked =
            grant admin (id 5) principal Role.CaseReader GrantTarget.Installation false

        acceptedAuthority revoked
        let! next = (Core.list actor (query cursor 2) (token))

        Expect.equal
            next
            (ReadReply.Rejected Refusal.ResourceUnavailable)
            "Current grants are rechecked at the real database boundary"
    }

type internal PausedPageStore
    (inner: IStore, entered: TaskCompletionSource, release: ManualResetEventSlim) =
    let pause () =
        entered.TrySetResult() |> ignore

        if not (release.Wait(TimeSpan.FromSeconds 3.0)) then
            failtest "Page release was not signalled"

    interface IStore with
        member _.Write(work, ct) = inner.Write(work, ct)

        member _.Read(work, ct) =
            inner.Read(
                (fun tx ->
                    work
                        { new IReadTx with
                            member _.ObservedAt = tx.ObservedAt

                            member _.Authority(principal, scope) =
                                task {
                                    let! actor = tx.Authority(principal, scope)
                                    pause ()
                                    return actor
                                }

                            member _.FindCase id = tx.FindCase id
                            member _.FindEntry id = tx.FindEntry id

                            member _.CasePage(visibility, actor, roles, invalid, after, limit) =
                                tx.CasePage(visibility, actor, roles, invalid, after, limit)

                            member _.HistoryPage(id, after, limit) =
                                tx.HistoryPage(id, after, limit)

                            member _.ActorPage(after, limit) = tx.ActorPage(after, limit)
                            member _.GrantPage(id, after, limit) = tx.GrantPage(id, after, limit)
                            member _.FindActor id = tx.FindActor id
                            member _.EnabledHumanOwners() = tx.EnabledHumanOwners()
                            member _.ActorCount() = tx.ActorCount()
                        }),
                ct
            )

let private freshAdvice (actor: HandlerContext) cursor current =
    task {
        Expect.isTrue
            ((page current).Offers |> List.exists (fun o -> o = InstallationOffer.Open))
            "Paused rows retain the same registrar snapshot advice"

        let! fresh = (Core.list actor (query None 50) (token))

        Expect.isFalse
            ((page fresh).Offers |> List.exists (fun o -> o = InstallationOffer.Open))
            "Fresh reader page has no OPEN advice"

        let! next = (Core.list actor (query cursor 50) (token))

        Expect.isFalse
            ((page next).Offers |> List.exists (fun offer -> offer = InstallationOffer.Open))
            "Plain continuation uses current authority and does not restore registrar advice"
    }

let private snapshot (database: PostgresFixture.Database) =
    task {
        let! admin, principal, actor = setup database

        let! promoted =
            grant admin (id 30) principal Role.CaseRegistrar GrantTarget.Installation true

        acceptedAuthority promoted
        let! first = (Core.list actor (query None 1) (token))
        let cursor = (page first).NextCursor

        let entered =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        use release = new ManualResetEventSlim(false)
        let store = PausedPageStore(database.Fixture.Store, entered, release)

        let facade =
            Handlers.context
                database.Fixture.InstallationId
                (store :> IStore)
                database.Fixture.Calendar

                principal

        let reading =
            Task.Run<ReadReply<CaseSummaryPage>>(
                Func<Task<ReadReply<CaseSummaryPage>>>(fun () ->
                    (Core.list facade (query cursor 50) (token)))
            )

        try
            do! entered.Task.WaitAsync(TimeSpan.FromSeconds 3.0)

            let! revoked =
                grant admin (id 5) principal Role.CaseRegistrar GrantTarget.Installation false

            acceptedAuthority revoked

            let! later =
                (Core.execute
                    admin
                    (OperationInput.Case(
                        request 13 "CASE-AFTER" 0L (UnvalidatedCaseAct.Open registration)
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
