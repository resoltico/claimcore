module ClaimCore.IntegrationTests.PaginationTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreContractTests
open ClaimCore.IntegrationTests.VerificationSupport

let private query after limit : CaseListRequest = { AfterCursor = after; Limit = limit }

let private page =
    function
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
                admin.Execute(request n reference 0L (BusinessCommand.Open registration), token)

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

type private PausedPageStore
    (inner: ILedgerStore, entered: TaskCompletionSource, release: ManualResetEventSlim) =
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

        member _.ListActors(principal, select, limit, ct) =
            inner.ListActors(principal, select, limit, ct)

        member _.ListCases(principal, select, limit, ct) =
            let paused view =
                let selected = select view
                entered.TrySetResult() |> ignore

                if not (release.Wait(TimeSpan.FromSeconds 3.0)) then
                    failtest "Page release was not signalled"

                selected

            inner.ListCases(principal, paused, limit, ct)

let private snapshot (database: PostgresFixture.Database) =
    task {
        let! admin, principal, actor = setup database
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
                cursorKey
                principal

        let reading =
            Task.Run<QueryOutcome<CaseSummaryPage>>(
                Func<Task<QueryOutcome<CaseSummaryPage>>>(fun () ->
                    facade.List(query cursor 50, token))
            )

        try
            do! entered.Task.WaitAsync(TimeSpan.FromSeconds 3.0)

            let! revoked =
                grant admin (id 5) principal Role.CaseReader GrantTarget.Installation false

            acceptedAuthority revoked

            let! later =
                admin.Execute(request 13 "CASE-AFTER" 0L (BusinessCommand.Open registration), token)

            accepted later |> ignore
        finally
            release.Set()

        let! current = reading

        Expect.equal
            ((page current).Items |> List.map reference)
            [ "CASE-B"; "CASE-C" ]
            "Authorization and data share the earlier read snapshot"

        let! next = actor.List(query cursor 50, token)

        Expect.equal
            next
            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
            "Fresh pages observe revoked authority"
    }

[<Tests>]
let tests =
    testSequenced
    <| testList
        "database cursor authority"
        [
            testCaseAsync
                "a real grant revocation refuses continuation on the next page"
                (run revocation)
            testCaseAsync
                "cursor validation and page fetch share one snapshot across accepted concurrent changes"
                (run snapshot)
        ]
