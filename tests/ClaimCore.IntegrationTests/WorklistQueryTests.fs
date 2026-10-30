module ClaimCore.IntegrationTests.WorklistQueryTests

open System
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.WorklistFixtures

let private page actor filter limit cursor =
    task {
        let! result =
            Service.list
                actor
                {
                    Page = { AfterCursor = cursor; Limit = limit }
                    Filter = filter
                }
                token

        match result with
        | Outcome.Succeeded page -> return page
        | _ -> return failtestf "Worklist read failed: %A" result
    }

let private walk pair filter limit expected =
    task {
        let mutable cursor = None
        let mutable remaining = expected
        let mutable finished = false
        let mutable pages = 0

        while not finished do
            let! postgres = page pair.Postgres filter limit cursor
            let! memory = page pair.Memory filter limit cursor
            Expect.equal memory postgres "Both stores return the same complete page and cursor"
            let visible = remaining |> List.truncate limit

            Expect.equal
                (postgres.Items |> List.map (fun row -> CaseReference.format row.Reference))
                (visible |> List.map _.Reference)
                "Page references follow the independent facet table and order"

            Expect.equal
                (postgres.Items |> List.map (fun row -> Revision.value row.Revision))
                (visible |> List.map _.Revision)
                "Every row retains its independently declared revision"

            remaining <- remaining |> List.skip visible.Length

            Expect.equal
                postgres.NextCursor.IsSome
                (not remaining.IsEmpty)
                "A cursor exists exactly when another independent expected row remains"

            cursor <- postgres.NextCursor
            pages <- pages + 1
            Expect.isLessThanOrEqual pages (cases.Length + 1) "Paging always makes progress"
            finished <- cursor.IsNone
    }

let private coversInventory key token sets =
    let declared = Inventory.tokens key |> Set.ofList
    let actual = sets |> List.map (List.map token >> Set.ofList) |> Set.ofList

    Expect.equal
        actual.Count
        (1 <<< declared.Count)
        "The explicit facet table covers the complete independent power set"

    Expect.equal actual.Count (List.length sets) "The table has no repeated facet set"

    actual
    |> Set.iter (fun choices ->
        Expect.isTrue
            (Set.isSubset choices declared)
            "Every table member belongs to the independent inventory")

let private namedQueues pair =
    task {
        let queues =
            [
                "open and undecided",
                CaseListFilter.create
                    [ Lifecycle.Active ]
                    [ CaseStatus.Opened ]
                    [ CaseProgress.Undecided ]
                    None
                    CaseListOrder.Reference,
                [ "mix-04" ]
                "decided and unpaid",
                CaseListFilter.create
                    [ Lifecycle.Active ]
                    []
                    [ CaseProgress.Decided ]
                    None
                    CaseListOrder.Reference,
                [ "work-02"; "Work-11" ]
                "closed",
                CaseListFilter.create [] [ CaseStatus.Closed ] [] None CaseListOrder.Reference,
                [ "work-02"; "work-05"; "work-06"; "work-08"; "WORK-09"; "Work-10" ]
                "voided",
                CaseListFilter.create [ Lifecycle.Voided ] [] [] None CaseListOrder.Reference,
                [ "work-03"; "work-07"; "work-08"; "WORK-09"; "Work-10"; "Work-12" ]
                "erased",
                CaseListFilter.create [ Lifecycle.Erased ] [] [] None CaseListOrder.Reference,
                [ "work-13" ]
                "recent",
                CaseListFilter.create [] [] [] None CaseListOrder.Recent,
                [
                    "mix-04"
                    "work-13"
                    "work-07"
                    "work-05"
                    "work-08"
                    "Work-11"
                    "work-03"
                    "work-06"
                    "Work-10"
                    "work-01"
                    "Work-12"
                    "work-02"
                    "WORK-09"
                ]
            ]

        for name, filter, references in queues do
            let rows =
                references
                |> List.map (fun reference ->
                    cases |> List.find (fun row -> row.Reference = reference))

            Expect.isNonEmpty rows ("The literal named queue is populated: " + name)

            for limit in [ 1; 2 ] do
                do! walk pair filter limit rows
    }

let private facets pair =
    task {
        do! namedQueues pair
        coversInventory "caseLifecycles" Lifecycle.token lifecycleSets
        coversInventory "caseStatuses" CaseStatus.token statusSets
        coversInventory "caseProgress" CaseProgress.token progressSets

        Expect.equal
            (orders |> List.map CaseListOrder.token |> Set.ofList)
            (Inventory.tokens "caseOrders" |> Set.ofList)
            "Every independently inventoried order executes"

        let mutable scenarios = 0

        for lifecycle in lifecycleSets do
            for status in statusSets do
                for progress in progressSets do
                    for order in orders do
                        let filter = CaseListFilter.create lifecycle status progress None order

                        for limit in limits do
                            do! walk pair filter limit (expected filter)
                            scenarios <- scenarios + 1

        Expect.equal
            scenarios
            ((1 <<< (Inventory.tokens "caseLifecycles").Length)
             * (1 <<< (Inventory.tokens "caseStatuses").Length)
             * (1 <<< (Inventory.tokens "caseProgress").Length)
             * (Inventory.tokens "caseOrders").Length
             * limits.Length)
            "All 8 lifecycle sets × 4 status sets × 8 progress sets × 2 orders × 5 page sizes execute"
    }

let private search pair =
    task {
        // Expectations are literal: no product normalization or matching helper computes them.
        let samples =
            [
                "%", [ "mix-04" ]
                "_", [ "WORK-09" ]
                "\\", [ "work-02" ]
                "full", [ "Work-12" ]
                "RĪGAS", [ "mix-04"; "WORK-09" ]
                "¨", [ "mix-04"; "work-01" ]
                "WORK-02", [ "work-02" ]
                "Private erased", []
            ]

        for text, references in samples do
            for order in orders do
                let filter = CaseListFilter.create [] [] [] (Some text) order

                let rows =
                    expected filter
                    |> List.filter (fun row -> List.contains row.Reference references)

                for limit in [ 1; 2 ] do
                    do! walk pair filter limit rows
    }

let private erasure pair =
    task {
        use! connection = pair.Database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT status,progress,search_text,changed_at,notified_on,claimed_amount,claimed_currency,payable_amount,payable_currency FROM claimcore.cases WHERE case_reference='work-13'"
                [||]
                token

        let row = List.exactlyOne rows

        for index in [ 0; 1; 2; 4; 5; 6; 7; 8 ] do
            Expect.isTrue
                (Convert.IsDBNull row[index])
                "Erasure physically clears every searchable/facet value"
        // Compare with the actual last case event, rather than relying on a guessed operation sequence.
        let! latest =
            DatabaseSession.rows
                connection
                "SELECT recorded_at FROM claimcore.ledger WHERE subject_id=(SELECT case_id FROM claimcore.cases WHERE case_reference='work-13') ORDER BY case_revision DESC LIMIT 1"
                [||]
                token

        Expect.equal
            (DatabaseSession.instant 3 row)
            (DatabaseSession.instant 0 (List.exactlyOne latest))
            "Erasure advances changed_at to its accepted ledger instant"
    }

let private cursorBinding pair =
    task {
        let all = CaseListFilter.empty<string>
        let! first = page pair.Postgres all 1 None

        let cursor =
            first.NextCursor
            |> Option.defaultWith (fun () -> failtest "The seeded table must cross a page boundary")

        let changes =
            [
                CaseListFilter.create [ Lifecycle.Active ] [] [] None CaseListOrder.Reference
                CaseListFilter.create [] [ CaseStatus.Opened ] [] None CaseListOrder.Reference
                CaseListFilter.create [] [] [ CaseProgress.Decided ] None CaseListOrder.Reference
                CaseListFilter.create [] [] [] (Some "Rīgas") CaseListOrder.Reference
                CaseListFilter.create [] [] [] None CaseListOrder.Recent
            ]

        for filter in changes do
            for actor in [ pair.Postgres; pair.Memory ] do
                let! result =
                    Service.list
                        actor
                        {
                            Page = { AfterCursor = Some cursor; Limit = 1 }
                            Filter = filter
                        }
                        token

                Expect.equal
                    result
                    (Outcome.Rejected Refusal.InvalidCursor)
                    "A cursor cannot be replayed under a different facet, search or order"
    }

[<Tests>]
let tests =
    testList
        "worklist store equivalence"
        [
            testCaseAsync "all facet sets orders and page boundaries agree" (run facets)
            testCaseAsync
                "literal wildcard Unicode normalization and erased-name search agree"
                (run search)
            testCaseAsync
                "erasure clears projected text and facets at its ledger instant"
                (run erasure)
            testCaseAsync "cursors reject every changed filter dimension" (run cursorBinding)
        ]
    |> testSequenced
