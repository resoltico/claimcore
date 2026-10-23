module ClaimCore.IntegrationTests.PaginationTests

open ClaimCore.Contract

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreContractTests
open ClaimCore.IntegrationTests.VerificationSupport

let private query after limit : PageRequest = { AfterCursor = after; Limit = limit }

let private page (outcome: QueryOutcome<CaseSummaryPage>) =
    match outcome with
    | QueryOutcome.Succeeded value -> value
    | _ -> failtest "Expected authorized page"

let private reference =
    function
    | CaseSummary.Live(value, _, _, _)
    | CaseSummary.Erased(value, _) -> value

let private setup (database: PostgresFixture.Database) =
    task {
        let! admin = caseWorker database.Fixture

        for n, reference in [ 10, "CASE-A"; 11, "CASE-B"; 12, "CASE-C" ] do
            let! result =
                admin.Execute(request n reference 0L (CaseAct.Open registration), token)

            accepted result |> ignore

        let principal =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith failtest

        let! registered =
            admin.ExecuteAuthority(
                authorityRequest (id 3) principal AuthorityAct.RegisterActor,
                token
            )

        acceptedAuthority registered

        let! granted =
            grant admin (id 4) principal Role.CaseReader GrantTarget.Installation true

        acceptedAuthority granted
        return admin, principal, core database.Fixture principal
    }

let private revocation (database: PostgresFixture.Database) =
    task {
        let! admin, principal, actor = setup database
        let! first = actor.List(query None 1, token)
        let cursor = (page first).NextCursor
        Expect.isSome cursor "Real database issues a continuation"

        let! revoked =
            grant admin (id 5) principal Role.CaseReader GrantTarget.Installation false

        acceptedAuthority revoked
        let! next = actor.List(query cursor 2, token)

        Expect.equal
            next
            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
            "Current grants are rechecked at the real database boundary"
    }

type internal PausedPageStore
    (inner: ILedgerStore, entered: TaskCompletionSource, release: ManualResetEventSlim) =
    let paused select view =
        let selected = select view
        entered.TrySetResult() |> ignore

        if not (release.Wait(TimeSpan.FromSeconds 3.0)) then
            failtest "Page release was not signalled"

        selected

    interface ILedgerStore with
        member _.WriteCase(principal, operationId, reference, decide, ct) =
            inner.WriteCase(principal, operationId, reference, decide, ct)

        member _.WriteAuthority(principal, operationId, target, reference, decide, ct) =
            inner.WriteAuthority(principal, operationId, target, reference, decide, ct)

        member _.ReadCase(principal, operationId, reference, ct) =
            inner.ReadCase(principal, operationId, reference, ct)

        member _.ReadActor(principal, ct) = inner.ReadActor(principal, ct)

        member _.ReadOperation(principal, operationId, ct) =
            inner.ReadOperation(principal, operationId, ct)

        member _.ReadHistory(principal, reference, select, limit, ct) =
            inner.ReadHistory(principal, reference, select, limit, ct)

        member _.ListGrants(principal, target, select, limit, ct) =
            inner.ListGrants(principal, target, paused select, limit, ct)

        member _.ListActors(principal, select, limit, ct) =
            inner.ListActors(principal, select, limit, ct)

        member _.ListCases(principal, readRoles, select, limit, ct) =
            inner.ListCases(principal, readRoles, paused select, limit, ct)

let private freshAdvice (actor: IActorClaimsCore) cursor current =
    task {
        Expect.isTrue
            ((page current).Offers |> List.exists (fun o -> o = InstallationOffer.Open))
            "Paused rows retain the same registrar snapshot advice"

        let! fresh = actor.List(query None 50, token)

        Expect.isFalse
            ((page fresh).Offers |> List.exists (fun o -> o = InstallationOffer.Open))
            "Fresh reader page has no OPEN advice"

        let! next = actor.List(query cursor 50, token)

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
        let! first = actor.List(query None 1, token)
        let cursor = (page first).NextCursor

        let entered =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        use release = new ManualResetEventSlim(false)
        let store = PausedPageStore(database.Fixture.Store, entered, release)

        let facade =
            Core.create
                database.Fixture.InstallationId
                (store :> ILedgerStore)
                database.Fixture.Calendar

                principal

        let reading =
            Task.Run<QueryOutcome<CaseSummaryPage>>(
                Func<Task<QueryOutcome<CaseSummaryPage>>>(fun () ->
                    facade.List(query cursor 50, token))
            )

        try
            do! entered.Task.WaitAsync(TimeSpan.FromSeconds 3.0)

            let! revoked =
                grant admin (id 5) principal Role.CaseRegistrar GrantTarget.Installation false

            acceptedAuthority revoked

            let! later =
                admin.Execute(request 13 "CASE-AFTER" 0L (CaseAct.Open registration), token)

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
