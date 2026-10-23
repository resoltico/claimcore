module ClaimCore.Tests.PaginationTests

open ClaimCore.Contract

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application
open ClaimCore.TestSupport.StoreContractTests
open ClaimCore.Tests.ApplicationFixtures

let private cases (actor: IActorClaimsCore) =
    task {
        for n, reference in [ 10, "Case-c"; 11, "case-A"; 12, "CASE-B" ] do
            let! result = actor.Execute(openRequest n reference, CancellationToken.None)
            accepted result |> ignore
    }

let private succeeded =
    function
    | QueryOutcome.Succeeded page -> page
    | _ -> failtest "Missing case page"

let private reference =
    function
    | CaseSummary.Live(value, _, _, _)
    | CaseSummary.Erased(value, _) -> value

let private cursor =
    function
    | Some value -> value
    | None -> failtest "Missing continuation"

let private reader fixture (admin: IActorClaimsCore) =
    task {
        let target =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith failtest

        let! registered =
            admin.ExecuteAuthority(
                authorityRequest (id 20) target AuthorityAct.RegisterActor,
                CancellationToken.None
            )

        acceptedAuthority registered

        let! granted =
            grant admin (id 21) target Role.CaseReader GrantTarget.Installation true

        acceptedAuthority granted
        return target, core fixture target
    }

let private caseLimits (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        do! cases actor
        let! first = actor.List({ AfterCursor = None; Limit = 1 }, CancellationToken.None)
        let first = succeeded first

        Expect.equal
            (first.Items |> List.map reference)
            [ "case-A" ]
            "Folded order retains authored spelling"

        let! rest =
            actor.List(
                {
                    AfterCursor = first.NextCursor
                    Limit = 2
                },
                CancellationToken.None
            )

        let rest = succeeded rest

        Expect.equal
            (rest.Items |> List.map reference)
            [ "CASE-B"; "Case-c" ]
            "Limits may change between pages"

        Expect.isNone rest.NextCursor "No omitted row"
    }

let private sharedPosition (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        do! cases admin
        let! first = admin.List({ AfterCursor = None; Limit = 1 }, CancellationToken.None)
        let token = (succeeded first).NextCursor
        let! _, another = reader fixture admin

        let! crossed =
            another.List({ AfterCursor = token; Limit = 1 }, CancellationToken.None)

        Expect.equal
            (succeeded crossed).Items.Length
            1
            "Current authority permits sharing a position"

        let! extra =
            grant admin (id 22) owner Role.CaseReader (GrantTarget.CaseReference "CASE-C") true

        acceptedAuthority extra
        let! changed = admin.List({ AfterCursor = token; Limit = 1 }, CancellationToken.None)

        Expect.equal
            (succeeded changed).Items.Length
            1
            "Irrelevant grant changes do not invalidate a position"
    }

let private elapsedTimeAndRevocation (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        do! cases admin
        let! target, actor = reader fixture admin
        let! first = actor.List({ AfterCursor = None; Limit = 1 }, CancellationToken.None)
        let token = (succeeded first).NextCursor
        store.Instant <- instant.AddMinutes(15.0)

        let! continued =
            actor.List({ AfterCursor = token; Limit = 2 }, CancellationToken.None)

        Expect.equal (succeeded continued).Items.Length 2 "Positions have no lifetime"

        store.Instant <- instant

        let! disabled =
            admin.ExecuteAuthority(
                authorityRequest (id 22) target (AuthorityAct.SetEnabled false),
                CancellationToken.None
            )

        acceptedAuthority disabled
        let! denied = actor.List({ AfterCursor = token; Limit = 1 }, CancellationToken.None)

        Expect.equal
            denied
            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
            "A position confers no rights after revocation"
    }

let private createHistory (actor: IActorClaimsCore) =
    task {
        let! opened = actor.Execute(openRequest 10 "HISTORY-1", CancellationToken.None)
        accepted opened |> ignore

        for n, revision in [ 11, 1L; 12, 2L ] do
            let values =
                { registration with
                    ClaimantName = "Party " + string n
                }

            let! amended =
                actor.Execute(
                    request n "HISTORY-1" revision (BusinessCommand.AmendRegistration values),
                    CancellationToken.None
                )

            accepted amended |> ignore
    }

let private registerActors (actor: IActorClaimsCore) =
    task {
        for n in 10..12 do
            let target =
                PrincipalKey.human "https://issuer.example/" ("actor-" + string n)
                |> Result.defaultWith failtest

            let! registered =
                actor.ExecuteAuthority(
                    authorityRequest (id n) target AuthorityAct.RegisterActor,
                    CancellationToken.None
                )

            acceptedAuthority registered
    }

let private differentHistoryResource (actor: IActorClaimsCore) (query: HistoryRequest) cursor =
    task {
        let! other = actor.Execute(openRequest 13 "OTHER", CancellationToken.None)
        accepted other |> ignore

        let! resource =
            actor.History(
                { query with
                    CaseReference = "OTHER"
                    AfterCursor = cursor
                },
                CancellationToken.None
            )

        Expect.equal resource (QueryOutcome.Rejected Rejection.InvalidCursor) "Resource is bound"
    }

let private historyBinding (_: MemoryLedgerStore.Store) (fixture: Fixture) =
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

        let! first = actor.History(query, CancellationToken.None)

        let first = succeeded first

        let! rest =
            actor.History(
                { query with
                    CaseReference = "history-1"
                    AfterCursor = first.NextCursor
                    Limit = 2
                },
                CancellationToken.None
            )

        match rest with
        | QueryOutcome.Succeeded page ->
            Expect.equal
                page.Entries.Length
                2
                "Reference recasing and changed limits retain query identity"
        | _ -> failtest "History continuation failed"

        let! summary =
            actor.History(
                { query with
                    AfterCursor = first.NextCursor
                    Detail = HistoryDetail.Summary
                },
                CancellationToken.None
            )

        Expect.equal
            (succeeded summary).Entries.Length
            1
            "Detail may change after independent authorization"

        do! differentHistoryResource actor query first.NextCursor
    }

let private authorityLimits (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture

        do! registerActors actor

        let! first =
            actor.ListActors({ AfterCursor = None; Limit = 1 }, CancellationToken.None)

        let first = succeeded first

        Expect.equal (first.Items |> List.map _.ActorId) [ id 1 ] "Stable actor-ID order"

        let! second =
            actor.ListActors(
                {
                    AfterCursor = first.NextCursor
                    Limit = 2
                },
                CancellationToken.None
            )

        let second = succeeded second

        Expect.equal (second.Items |> List.map _.ActorId) [ id 10; id 11 ] "Changed page size"

        let! third =
            actor.ListActors(
                {
                    AfterCursor = second.NextCursor
                    Limit = 1
                },
                CancellationToken.None
            )

        match third with
        | QueryOutcome.Succeeded page ->
            Expect.equal (page.Items |> List.map _.ActorId) [ id 12 ] "No omission"
            Expect.isNone page.NextCursor "End of review"
        | _ -> failtest "Missing final review page"

        let! wrongEndpoint =
            actor.List(
                {
                    AfterCursor = first.NextCursor
                    Limit = 1
                },
                CancellationToken.None
            )

        Expect.equal
            wrongEndpoint
            (QueryOutcome.Rejected Rejection.InvalidCursor)
            "Authority continuation is endpoint-bound even when its position is valid ASCII"
    }

let private malformedAndRestart (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        do! cases actor
        let! first = actor.List({ AfterCursor = None; Limit = 1 }, CancellationToken.None)
        let original = (succeeded first).NextCursor |> cursor

        for malformed in [ original + "="; ""; "!"; String.replicate 513 "A" ] do
            let! outcome =
                actor.List(
                    {
                        AfterCursor = Some malformed
                        Limit = 1
                    },
                    CancellationToken.None
                )

            Expect.equal
                outcome
                (QueryOutcome.Rejected Rejection.InvalidCursor)
                "No malformed position is trusted"

        let restarted = Core.create installation fixture.Store calendar owner

        let! old =
            restarted.List(
                {
                    AfterCursor = Some original
                    Limit = 1
                },
                CancellationToken.None
            )

        Expect.equal (succeeded old).Items.Length 1 "Plain continuation survives facade restart"
    }

let private singleSnapshot (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        do! cases admin
        let proxy = AfterCaseWriteStore(fixture.Store, fun () -> Task.FromResult(()))

        let actor = Core.create installation (proxy :> ILedgerStore) calendar owner

        let! first = actor.List({ AfterCursor = None; Limit = 1 }, CancellationToken.None)
        succeeded first |> ignore
        Expect.equal proxy.ReadCalls 1 "List authorizes and fetches in one read"

        let! history =
            actor.History(
                {
                    CaseReference = "CASE-A"
                    AfterCursor = None
                    Limit = 1
                    Detail = HistoryDetail.Summary
                },
                CancellationToken.None
            )

        match history with
        | QueryOutcome.Succeeded _ -> ()
        | _ -> failtest "History unavailable"

        Expect.equal proxy.ReadCalls 2 "History performs no separate actor read"

        let! authority =
            actor.ListActors({ AfterCursor = None; Limit = 1 }, CancellationToken.None)

        match authority with
        | QueryOutcome.Succeeded _ -> ()
        | _ -> failtest "Authority review unavailable"

        Expect.equal proxy.ReadCalls 3 "Authority review performs no separate actor read"

        let! grants =
            actor.ListGrants(
                {
                    ActorId = id 1
                    AfterCursor = None
                    Limit = 1
                },
                CancellationToken.None
            )

        succeeded grants |> ignore
        Expect.equal proxy.ReadCalls 4 "Target grant review performs no separate actor read"
    }

let private pageAdvice (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        do! cases admin
        let! principal, actor = reader fixture admin

        let! scoped =
            grant
                admin
                (id 22)
                principal
                Role.CaseRegistrar
                (GrantTarget.CaseReference "CASE-A")
                true

        acceptedAuthority scoped
        let! readers = actor.List({ AfterCursor = None; Limit = 50 }, CancellationToken.None)

        Expect.isFalse
            ((succeeded readers).Offers |> List.contains InstallationOffer.Open)
            "Case-scoped registrar has no installation registration advice"

        let! owners = admin.List({ AfterCursor = None; Limit = 50 }, CancellationToken.None)
        let offers = (succeeded owners).Offers

        Expect.contains
            offers
            InstallationOffer.Open
            "Same-page authority supplies registration advice"

        Expect.contains
            offers
            (InstallationOffer.AuthorityAct AuthorityKind.SetGrant)
            "Authority act advice is owned by core"
    }

let private appendHistory (actor: IActorClaimsCore) =
    task {
        for revision in 1L .. 52L do
            let act =
                if revision % 2L = 1L then
                    BusinessCommand.Close
                else
                    BusinessCommand.Reopen

            let! result =
                actor.Execute(
                    request (20 + int revision) "LONG-HISTORY" revision act,
                    CancellationToken.None
                )

            accepted result |> ignore

    }

let private fullHistoryLimit (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let! opened = actor.Execute(openRequest 10 "LONG-HISTORY", CancellationToken.None)
        accepted opened |> ignore

        do! appendHistory actor

        let query =
            {
                CaseReference = "LONG-HISTORY"
                AfterCursor = None
                Limit = 50
                Detail = HistoryDetail.Full
            }

        let! first = actor.History(query, CancellationToken.None)
        let first = succeeded first
        Expect.equal first.Entries.Length 50 "FULL shares the public page capacity"

        let! rest =
            actor.History(
                { query with
                    AfterCursor = first.NextCursor
                },
                CancellationToken.None
            )

        let rest = succeeded rest

        let revision =
            function
            | HistoryEntry.FullEntry r ->
                match r.Scope with
                | ReceiptScope.Case c -> c.Revision
                | _ -> failtest "case"
            | _ -> failtest "full"

        Expect.equal
            (rest.Entries |> List.map revision)
            [ 51L; 52L; 53L ]
            "Continuation follows last included row"

        let! summary =
            actor.History(
                { query with
                    Detail = HistoryDetail.Summary
                },
                CancellationToken.None
            )

        Expect.equal (succeeded summary).Entries.Length 50 "SUMMARY shares the public page capacity"
    }

let private disabledGrantTarget
    (store: MemoryLedgerStore.Store)
    (admin: IActorClaimsCore)
    target
    (query: AuthorityGrantListRequest)
    cursor
    =
    task {
        let! crossed =
            admin.ListGrants(
                { query with
                    ActorId = id 1
                    AfterCursor = cursor
                },
                CancellationToken.None
            )

        Expect.equal crossed (QueryOutcome.Rejected Rejection.InvalidCursor) "Cursor binds target"

        let! disabled =
            admin.ExecuteAuthority(
                authorityRequest (id 31) target (AuthorityAct.SetEnabled false),
                CancellationToken.None
            )

        acceptedAuthority disabled
        let! visible = admin.ListGrants(query, CancellationToken.None)
        Expect.isFalse (succeeded visible).Actor.Enabled "Disabled targets remain reviewable"
        store.Instant <- instant.AddMinutes 15.0

        let! continued =
            admin.ListGrants({ query with AfterCursor = cursor }, CancellationToken.None)

        Expect.equal (succeeded continued).Items.Length 1 "Grant positions have no lifetime"
    }

let private grantBinding (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        do! cases admin
        let! target, _ = reader fixture admin

        let! added =
            grant admin (id 30) target Role.CaseReader (GrantTarget.CaseReference "CASE-A") true

        acceptedAuthority added

        let! actors =
            admin.ListActors({ AfterCursor = None; Limit = 50 }, CancellationToken.None)

        let targetId =
            (succeeded actors).Items
            |> List.find (fun a -> a.Principal = target)
            |> _.ActorId

        let query =
            {
                ActorId = targetId
                AfterCursor = None
                Limit = 1
            }

        let! first = admin.ListGrants(query, CancellationToken.None)
        let first = succeeded first

        Expect.equal
            first.Items
            [
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.CaseReference "case-A"
                }
            ]
            "Case UUIDs precede installation-null within role"

        let! next =
            admin.ListGrants(
                { query with
                    AfterCursor = first.NextCursor
                },
                CancellationToken.None
            )

        Expect.equal
            (succeeded next).Items
            [
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
            ]
            "NULLS LAST position is distinct"

        do! disabledGrantTarget store admin target query first.NextCursor
    }

let private invalidGrantPositions =
    [
        "{\"caseId\":null,\"role\":\"NOT_A_ROLE\"}"
        "{\"caseId\":\"00000000-0000-0000-0000-000000000000\",\"role\":\"CASE_READER\"}"
        "{\"caseId\":\"A0000000-0000-4000-8000-000000000001\",\"role\":\"CASE_READER\"}"
        "{ \"caseId\":null,\"role\":\"CASE_READER\"}"
        "{\"caseId\":null,\"role\":\"CASE_READER\",\"extra\":false}"
    ]

let private grantCursorRejections (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        let! view = fixture.Store.ReadActor(owner, CancellationToken.None)
        let view = view |> Result.defaultWith (fun _ -> failtest "Caller authority")
        let authority = view.Actor.Actor |> Option.get

        for position in invalidGrantPositions do
            let cursor =
                let json =
                    $"{{\"actor\":\"{authority.ActorId:D}\",\"after\":{position},\"kind\":\"GRANTS\",\"v\":1}}"

                Convert
                    .ToBase64String(System.Text.Encoding.UTF8.GetBytes json)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_')

            let! refused =
                admin.ListGrants(
                    {
                        ActorId = authority.ActorId
                        AfterCursor = Some cursor
                        Limit = 50
                    },
                    CancellationToken.None
                )

            Expect.equal
                refused
                (QueryOutcome.Rejected Rejection.InvalidCursor)
                "A plain envelope does not admit an invalid grant position"

        let! invalidLimit =
            admin.ListGrants(
                {
                    ActorId = authority.ActorId
                    AfterCursor = None
                    Limit = 51
                },
                CancellationToken.None
            )

        Expect.equal
            invalidLimit
            (QueryOutcome.Rejected(Rejection.PageLimitOutOfRange 50))
            "Public grant page is bounded"
    }

[<Tests>]
let tests =
    testList
        "authorized continuation pages"
        [
            testCaseAsync
                "plain grant positions retain primitive and semantic admission"
                (run grantCursorRejections)
            testCaseAsync "case pages carry same-snapshot owned advice" (run pageAdvice)
            testCaseAsync
                "FULL history uses fifty and continues after its last included revision"
                (run fullHistoryLimit)
            testCaseAsync
                "grant pages bind target preserve null ordering and review disabled targets"
                (run grantBinding)
            testCaseAsync
                "case continuation preserves key order and permits changed page sizes"
                (run caseLimits)
            testCaseAsync
                "case positions may be shared under current authority and survive irrelevant grant changes"
                (run sharedPosition)
            testCaseAsync
                "case positions survive elapsed time and still enforce revocation"
                (run elapsedTimeAndRevocation)
            testCaseAsync
                "history positions bind the normalized resource and permit changed detail and limits"
                (run historyBinding)
            testCaseAsync
                "authority continuation pages actors once and cannot cross endpoints"
                (run authorityLimits)
            testCaseAsync
                "malformed tokens refuse and plain positions survive restart"
                (run malformedAndRestart)
            testCaseAsync
                "all pagination endpoints authorize and fetch within one read operation"
                (run singleSnapshot)
        ]
