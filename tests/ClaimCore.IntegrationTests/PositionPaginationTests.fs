module ClaimCore.IntegrationTests.PositionPaginationTests

open ClaimCore.TestSupport
open ClaimCore.Protocol

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures
open ClaimCore.IntegrationTests.PaginationScenarios


let private caseLimits (_: PostgresFixture.Database) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        do! cases actor

        let! first =
            (Service.list
                actor
                ((ClaimCore.TestSupport.Inputs.caseListPage ({ AfterCursor = None; Limit = 1 })))
                (CancellationToken.None))

        let first = succeeded first

        Expect.equal
            (first.Items |> List.map reference)
            [ "case-A" ]
            "Folded order retains authored spelling"

        let! rest =
            (Service.list
                actor
                ((ClaimCore.TestSupport.Inputs.caseListPage (
                    {
                        AfterCursor = first.NextCursor
                        Limit = 2
                    }
                )))
                (CancellationToken.None))

        let rest = succeeded rest

        Expect.equal
            (rest.Items |> List.map reference)
            [ "CASE-B"; "Case-c" ]
            "Limits may change between pages"

        Expect.isNone rest.NextCursor "No omitted row"
    }

let private sharedPosition (_: PostgresFixture.Database) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        do! cases admin

        let! first =
            (Service.list
                admin
                ((ClaimCore.TestSupport.Inputs.caseListPage ({ AfterCursor = None; Limit = 1 })))
                (CancellationToken.None))

        let token = (succeeded first).NextCursor
        let! _, another = reader fixture admin

        let! crossed =
            (Service.list
                another
                ((ClaimCore.TestSupport.Inputs.caseListPage ({ AfterCursor = token; Limit = 1 })))
                (CancellationToken.None))

        Expect.equal
            (succeeded crossed).Items.Length
            1
            "Current authority permits sharing a position"

        let! extra =
            grant
                admin
                (id 22)
                owner
                {
                    Role = Role.CaseReader
                    Scope = (GrantTarget.CaseReference "CASE-C")
                }
                true

        acceptedAuthority extra

        let! changed =
            (Service.list
                admin
                ((ClaimCore.TestSupport.Inputs.caseListPage ({ AfterCursor = token; Limit = 1 })))
                (CancellationToken.None))

        Expect.equal
            (succeeded changed).Items.Length
            1
            "Irrelevant grant changes do not invalidate a position"
    }

let private historyBinding (_: PostgresFixture.Database) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        do! createHistory actor

        let query =
            {
                CaseReference = "HISTORY-1"
                AfterCursor = None
                Limit = 1
                Detail = HistoryDetail.Full
            }

        let! first = (Service.history actor (query) (CancellationToken.None))

        let first = succeeded first

        let! rest =
            (Service.history
                actor
                ({ query with
                    CaseReference = "history-1"
                    AfterCursor = first.NextCursor
                    Limit = 2
                })
                (CancellationToken.None))

        match rest with
        | Outcome.Succeeded page ->
            Expect.equal
                page.Entries.Length
                2
                "Reference recasing and changed limits retain query identity"
        | _ -> failtest "History continuation failed"

        let! summary =
            (Service.history
                actor
                ({ query with
                    AfterCursor = first.NextCursor
                    Detail = HistoryDetail.Summary
                })
                (CancellationToken.None))

        Expect.equal
            (succeeded summary).Entries.Length
            1
            "Detail may change after independent authorization"

        do! differentHistoryResource actor query first.NextCursor
    }

let private malformedAndRestart (_: PostgresFixture.Database) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        do! cases actor

        let! first =
            (Service.list
                actor
                ((ClaimCore.TestSupport.Inputs.caseListPage ({ AfterCursor = None; Limit = 1 })))
                (CancellationToken.None))

        let original = (succeeded first).NextCursor |> cursor

        for malformed in [ original + "="; ""; "!"; String.replicate 513 "A" ] do
            let! outcome =
                (Service.list
                    actor
                    ((ClaimCore.TestSupport.Inputs.caseListPage (
                        {
                            AfterCursor = Some malformed
                            Limit = 1
                        }
                    )))
                    (CancellationToken.None))

            Expect.equal
                outcome
                (Outcome.Rejected Refusal.InvalidCursor)
                "No malformed position is trusted"

        let restarted =
            Service.create
                BuildIdentity.current.Version
                fixture.InstallationId
                fixture.Store
                fixture.Calendar
                owner

        let! old =
            (Service.list
                restarted
                ((ClaimCore.TestSupport.Inputs.caseListPage (
                    {
                        AfterCursor = Some original
                        Limit = 1
                    }
                )))
                (CancellationToken.None))

        Expect.equal (succeeded old).Items.Length 1 "Plain continuation survives facade restart"
    }

let private singleSnapshot (_: PostgresFixture.Database) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        do! cases admin
        let proxy = AfterCaseWriteStore(fixture.Store, fun () -> Task.FromResult(()))

        let actor =
            Service.create
                BuildIdentity.current.Version
                fixture.InstallationId
                (proxy :> IStore)
                fixture.Calendar
                owner

        let! first =
            (Service.list
                actor
                ((ClaimCore.TestSupport.Inputs.caseListPage ({ AfterCursor = None; Limit = 1 })))
                (CancellationToken.None))

        succeeded first |> ignore
        Expect.equal proxy.ReadCalls 1 "List authorizes and fetches in one read"

        let! history =
            (Service.history
                actor
                ({
                    CaseReference = "CASE-A"
                    AfterCursor = None
                    Limit = 1
                    Detail = HistoryDetail.Summary
                })
                (CancellationToken.None))

        match history with
        | Outcome.Succeeded _ -> ()
        | _ -> failtest "History unavailable"

        Expect.equal proxy.ReadCalls 2 "History performs no separate actor read"

        let! authority =
            (Service.actors actor ({ AfterCursor = None; Limit = 1 }) (CancellationToken.None))

        match authority with
        | Outcome.Succeeded _ -> ()
        | _ -> failtest "Authority review unavailable"

        Expect.equal proxy.ReadCalls 3 "Authority review performs no separate actor read"

        let! grants =
            (Service.grants
                actor
                ({
                    ActorId = id 1
                    AfterCursor = None
                    Limit = 1
                })
                (CancellationToken.None))

        succeeded grants |> ignore
        Expect.equal proxy.ReadCalls 4 "Target grant review performs no separate actor read"
    }

let private pageAdvice (_: PostgresFixture.Database) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        do! cases admin
        let! principal, actor = reader fixture admin

        let! scoped =
            grant
                admin
                (id 22)
                principal
                {
                    Role = Role.CaseRegistrar
                    Scope = (GrantTarget.CaseReference "CASE-A")
                }
                true

        acceptedAuthority scoped

        let! readers =
            (Service.list
                actor
                ((ClaimCore.TestSupport.Inputs.caseListPage ({ AfterCursor = None; Limit = 50 })))
                (CancellationToken.None))

        Expect.isFalse
            ((succeeded readers).Offers |> List.contains InstallationOffer.Open)
            "Case-scoped registrar has no installation registration advice"

        let! owners =
            (Service.list
                admin
                ((ClaimCore.TestSupport.Inputs.caseListPage ({ AfterCursor = None; Limit = 50 })))
                (CancellationToken.None))

        let offers = (succeeded owners).Offers

        Expect.contains
            offers
            InstallationOffer.Open
            "Same-page authority supplies registration advice"

        Expect.contains
            offers
            (InstallationOffer.AuthorityAct AuthorityKind.Grant)
            "Authority act advice is owned by core"
    }

let private fullHistoryLimit (_: PostgresFixture.Database) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture

        let! opened =
            (Service.execute
                actor
                (OperationInput.Case(openRequest 10 "LONG-HISTORY"))
                (CancellationToken.None))

        accepted opened |> ignore

        do! appendHistory actor

        let query =
            {
                CaseReference = "LONG-HISTORY"
                AfterCursor = None
                Limit = 50
                Detail = HistoryDetail.Full
            }

        let! first = (Service.history actor (query) (CancellationToken.None))
        let first = succeeded first
        Expect.equal first.Entries.Length 50 "FULL shares the public page capacity"

        let! rest =
            (Service.history
                actor
                ({ query with
                    AfterCursor = first.NextCursor
                })
                (CancellationToken.None))

        let rest = succeeded rest

        let revision =
            function
            | HistoryEntry.FullEntry r ->
                match r.Subject with
                | ReceiptSubject.Case c -> Revision.value c.Revision
                | _ -> failtest "case"
            | _ -> failtest "full"

        Expect.equal
            (first.Entries |> List.map revision)
            (List.rev [ 4L .. 53L ])
            "First FULL page contains the newest fifty accepted revisions"

        Expect.isNone rest.NextCursor "The oldest page ends at opening revision"

        Expect.equal
            (rest.Entries |> List.map revision)
            [ 3L; 2L; 1L ]
            "Newest-first continuation follows the last included revision"

        let! summary =
            (Service.history
                actor
                ({ query with
                    Detail = HistoryDetail.Summary
                })
                (CancellationToken.None))

        Expect.equal (succeeded summary).Entries.Length 50 "SUMMARY shares the public page capacity"
    }

[<Tests>]
let tests =
    testList
        "authorized continuation pages"
        [
            testCaseAsync "case pages carry same-snapshot owned advice" (run pageAdvice)
            testCaseAsync
                "FULL history uses fifty and continues after its last included revision"
                (run fullHistoryLimit)
            testCaseAsync
                "case continuation preserves key order and permits changed page sizes"
                (run caseLimits)
            testCaseAsync
                "case positions may be shared under current authority and survive irrelevant grant changes"
                (run sharedPosition)
            testCaseAsync
                "history positions bind the normalized resource and permit changed detail and limits"
                (run historyBinding)
            testCaseAsync
                "malformed tokens refuse and plain positions survive restart"
                (run malformedAndRestart)
            testCaseAsync
                "all pagination endpoints authorize and fetch within one read operation"
                (run singleSnapshot)
        ]
