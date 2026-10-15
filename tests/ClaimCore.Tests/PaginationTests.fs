module ClaimCore.Tests.PaginationTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
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

let private principalAndGrantBinding (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        do! cases admin
        let! first = admin.List({ AfterCursor = None; Limit = 1 }, CancellationToken.None)
        let token = (succeeded first).NextCursor
        let! _, another = reader fixture admin

        let! crossed =
            another.List({ AfterCursor = token; Limit = 1 }, CancellationToken.None)

        Expect.equal
            crossed
            (QueryOutcome.Rejected Rejection.InvalidCursor)
            "Another authorized principal cannot reuse continuation"

        let! extra =
            grant admin (id 22) owner Role.CaseReader (GrantTarget.CaseReference "CASE-C") true

        acceptedAuthority extra
        let! changed = admin.List({ AfterCursor = token; Limit = 1 }, CancellationToken.None)

        Expect.equal
            changed
            (QueryOutcome.Rejected Rejection.InvalidCursor)
            "Even an otherwise irrelevant current grant changes the binding"
    }

let private expiryAndRevocation (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        do! cases admin
        let! target, actor = reader fixture admin
        let! first = actor.List({ AfterCursor = None; Limit = 1 }, CancellationToken.None)
        let token = (succeeded first).NextCursor
        store.Instant <- instant.AddMinutes(15.0)
        let! expired = actor.List({ AfterCursor = token; Limit = 2 }, CancellationToken.None)

        Expect.equal
            expired
            (QueryOutcome.Rejected Rejection.InvalidCursor)
            "Expiry uses the read-snapshot database instant"

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
            "A signed token confers no rights after revocation"
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

        Expect.equal summary (QueryOutcome.Rejected Rejection.InvalidCursor) "Detail is bound"
        let! other = actor.Execute(openRequest 13 "OTHER", CancellationToken.None)
        accepted other |> ignore

        let! resource =
            actor.History(
                { query with
                    CaseReference = "OTHER"
                    AfterCursor = first.NextCursor
                },
                CancellationToken.None
            )

        Expect.equal resource (QueryOutcome.Rejected Rejection.InvalidCursor) "Resource is bound"
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
        let chars = original.ToCharArray()
        chars[chars.Length / 2] <- if chars[chars.Length / 2] = 'A' then 'B' else 'A'

        for malformed in [ String chars; original + "="; ""; "!"; String.replicate 513 "A" ] do
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

        let restarted =
            Core.create installation fixture.Store calendar (Array.rev cursorKey) owner

        let! old =
            restarted.List(
                {
                    AfterCursor = Some original
                    Limit = 1
                },
                CancellationToken.None
            )

        Expect.equal
            old
            (QueryOutcome.Rejected Rejection.InvalidCursor)
            "Another runtime key rejects old continuations"
    }

let private singleSnapshot (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        do! cases admin
        let proxy = AfterCaseWriteStore(fixture.Store, fun () -> Task.FromResult(()))

        let actor =
            Core.create installation (proxy :> ILedgerStore) calendar cursorKey owner

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
    }

[<Tests>]
let tests =
    testList
        "authorized continuation pages"
        [
            testCaseAsync
                "case continuation preserves key order and permits changed page sizes"
                (run caseLimits)
            testCaseAsync
                "case continuation binds principal and the complete current grant set"
                (run principalAndGrantBinding)
            testCaseAsync "expired and revoked case continuations refuse" (run expiryAndRevocation)
            testCaseAsync
                "history continuation binds normalized resource and detail while permitting changed limits"
                (run historyBinding)
            testCaseAsync
                "authority continuation pages actors once and cannot cross endpoints"
                (run authorityLimits)
            testCaseAsync
                "malformed tokens and runtime-key changes refuse without position disclosure"
                (run malformedAndRestart)
            testCaseAsync
                "all pagination endpoints authorize and fetch within one read operation"
                (run singleSnapshot)
        ]
