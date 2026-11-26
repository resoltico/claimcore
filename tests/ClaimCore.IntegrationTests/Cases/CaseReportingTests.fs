module ClaimCore.IntegrationTests.CaseReportingTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Wire.Export

let private success =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected authorized report data: %A" other

let private date raw =
    CalendarDate.parse raw
    |> Result.defaultWith (fun issue -> failtestf "Synthetic date is invalid: %A" issue)

let private read owner filter group =
    task {
        let! result =
            Service.caseSummary
                owner
                {
                    Selection =
                        {
                            Filter = filter
                            GroupBy = Some group
                        }
                    Page =
                        {
                            AfterCursor = None
                            Limit = Paging.MaximumPageSize
                        }
                }
                token

        return success result
    }

let private openCaseInCurrency fixture owner number received currency claimed =
    let values =
        [
            match received with
            | Some received ->
                Submissions.field BusinessField.ReceivedDate, SubmissionValue.Text received
            | None -> ()
            match claimed with
            | Some claimed ->
                Submissions.field BusinessField.ClaimedAmount, SubmissionValue.Text claimed
                Submissions.field BusinessField.ClaimedCurrency, SubmissionValue.Text currency
            | None -> ()
        ]
        |> Map.ofList

    Service.executeSubmission
        owner
        (request fixture number ($"REPORT-{number}") 0L "case.open" values)
        token

let private openCase fixture owner number received claimed =
    openCaseInCurrency fixture owner number received "EUR" claimed

let private groups (report: CaseWorklistSummary) =
    report.Rows
    |> List.choose (fun row ->
        row.Group |> Option.map (fun group -> CaseGroupKey.token group, row.Count))
    |> Map.ofList

let private monthReporting (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = caseWorker fixture

        for number in 10..19 do
            let received =
                if number < 16 then Some "2026-11-01"
                elif number < 18 then Some "2026-10-01"
                else None

            let claimed =
                if number = 10 then Some "100"
                elif number = 11 then Some "0"
                else None

            let! opened = openCase fixture owner number received claimed
            accepted opened |> ignore

        let values =
            [
                Submissions.field BusinessField.PaymentDate, SubmissionValue.Text "2026-11-02"
                Submissions.field BusinessField.PaidAmount, SubmissionValue.Text "20"
                Submissions.field BusinessField.PaidCurrency, SubmissionValue.Text "EUR"
                Submissions.input InputTarget.PaymentDirection, SubmissionValue.Text "RETURNED"
            ]
            |> Map.ofList

        let! recorded =
            Service.executeSubmission
                owner
                (request fixture 23 "REPORT-10" 1L "case.recordPayment" values)
                token

        accepted recorded |> ignore

        let all = CaseListFilter.empty<string>
        let! summary = read owner all CaseGrouping.ReceivedMonth

        Expect.equal
            (groups summary)
            (Map.ofList [ "2026-10", 2L; "2026-11", 6L; "NOT_RECORDED", 2L ])
            "October, November, and unrecorded receipts are distinct groups"

        let money = summary.Rows |> List.choose _.Claimed |> List.exactlyOne

        Expect.equal
            (Currency.format money.Currency, DecimalTotal.format money.Amount, money.Cases)
            ("EUR", "100", 2L)
            "Explicit zero contributes to the count and absent money does not"

        let net = summary.Rows |> List.choose _.NetPaid |> List.exactlyOne

        Expect.equal
            (Currency.format net.Currency, DecimalTotal.format net.Amount, net.Cases)
            ("EUR", "-20", 1L)
            "Net paid remains signed and counts its contributing case"

        let human = CaseTotalsScreen.document summary

        Expect.isOk
            (HumanDocumentCodecs.document.Write human
             |> HumanDocumentCodecs.document.Read "$")
            "The human summary admits grouped signed amounts and outcome labels"

        let csv = CaseSummaryCsv.rows summary |> Encoding.UTF8.GetString

        Expect.stringContains
            csv
            "2026-11,6,EUR,100,2,,,-20,1\r\n"
            "One row carries the November currency amount and count"

        Expect.stringContains
            csv
            "NOT_RECORDED,2,,,,,,,\r\n"
            "A group without money still has one row"

        let actualDate = CalendarDate.ofDate summary.Basis.BusinessDate
        let throughYesterday = CalendarDate.ofDate (summary.Basis.BusinessDate.AddDays(-1))

        let filters =
            { all with
                Dates =
                    Map.ofList
                        [
                            CaseDateFilter.RegisteredFrom, actualDate
                            CaseDateFilter.RegisteredThrough, actualDate
                        ]
            }

        let! selected = read owner filters CaseGrouping.RegisteredMonth
        Expect.equal selected.Counts.Total 10L "Both endpoints include their registration day"

        let! more = read owner all CaseGrouping.DaysOpen

        Expect.equal
            (groups more)
            (Map.ofList [ "0-30", 10L ])
            "The SQL day bands come from the domain"

        let! listed =
            Service.list
                owner
                {
                    Filter = filters
                    Page = { AfterCursor = None; Limit = 20 }
                }
                token

        Expect.equal (success listed).Items.Length 10 "List and summary apply the same bounds"

        let excluded =
            { all with
                Dates = Map.ofList [ CaseDateFilter.RegisteredThrough, throughYesterday ]
            }

        let! none = read owner excluded CaseGrouping.RegisteredMonth
        Expect.equal none.Counts.Total 0L "The previous day excludes later registrations"

        let received =
            { all with
                Dates = Map.ofList [ CaseDateFilter.ReceivedFrom, date "2026-10-01" ]
            }

        let! missing = read owner received CaseGrouping.ReceivedMonth
        Expect.equal missing.Counts.Total 8L "A bounded received date excludes cases without a date"

        let! datedList =
            Service.list
                owner
                {
                    Filter = received
                    Page = { AfterCursor = None; Limit = 20 }
                }
                token

        Expect.equal
            ((success datedList).Items |> List.map (_.Reference >> CaseReference.format))
            [
                "REPORT-10"
                "REPORT-11"
                "REPORT-12"
                "REPORT-13"
                "REPORT-14"
                "REPORT-15"
                "REPORT-16"
                "REPORT-17"
            ]
            "The fixed received lower bound includes dated cases and excludes the undated companions"

        let reader =
            PrincipalKey.human "https://issuer.example/" "scoped-report-reader"
            |> Result.defaultWith (fun issue -> failtestf "Synthetic reader: %A" issue)

        let! registration =
            personSubmission
                fixture.Store
                (id fixture 20)
                reader
                None
                "people.register"
                (Submissions.profile DomainInputs.actorProfile)

        let! registered = Service.executeSubmission owner registration token
        acceptedPeople registered

        for ix, target in [ 21, 10; 22, 16 ] do
            let! granted =
                grant
                    owner
                    (id fixture ix)
                    reader
                    {
                        GrantInput.Role = Role.CaseReader
                        Scope = GrantTarget.CaseId(CaseFixtures.caseId (id fixture target))
                    }
                    true

            acceptedPeople granted

        let! restricted = read (core fixture reader) all CaseGrouping.ReceivedMonth
        Expect.equal restricted.Counts.Total 2L "Reporting keeps the same case-read selection"

        Expect.equal
            (groups restricted)
            (Map.ofList [ "2026-10", 1L; "2026-11", 1L ])
            "Groups cannot reveal other cases"

        Expect.equal
            (restricted.Rows |> List.choose _.Claimed |> List.map _.Cases)
            [ 1L ]
            "Totals cannot include excluded cases"

        let scoped = core fixture reader

        let selection =
            {
                Filter = all
                GroupBy = Some CaseGrouping.ReceivedMonth
            }

        let request =
            {
                Selection = selection
                Page = { AfterCursor = None; Limit = 1 }
            }

        let! initial = Service.caseSummary scoped request token
        let initial = success initial
        Expect.equal (groups initial) (Map.ofList [ "2026-10", 1L ]) "The first observed source"
        Expect.isSome initial.NextCursor "The scoped reader can continue"

        let! added = openCase fixture owner 24 (Some "2026-11-14") (Some "0")
        accepted added |> ignore

        for number, target, enabled in [ 25, 10, false; 26, 19, true; 27, 24, true ] do
            let! changed =
                grant
                    owner
                    (id fixture number)
                    reader
                    {
                        Role = Role.CaseReader
                        Scope = GrantTarget.CaseId(CaseFixtures.caseId (id fixture target))
                    }
                    enabled

            acceptedPeople changed

        let! continued =
            Service.caseSummary
                scoped
                { request with
                    Page =
                        { request.Page with
                            AfterCursor = initial.NextCursor
                        }
                }
                token

        Expect.equal
            continued
            (Outcome.Rejected Refusal.TraversalChanged)
            "Changed case access requires a fresh report traversal"

        let! refreshed = read scoped all CaseGrouping.ReceivedMonth

        Expect.equal
            refreshed.Counts.Total
            3L
            "A fresh report counts the newly authorized selection"

        Expect.equal
            (groups refreshed)
            (Map.ofList [ "2026-10", 1L; "2026-11", 1L; "NOT_RECORDED", 1L ])
            "A fresh report groups only currently readable cases"

        let claim = refreshed.Rows |> List.choose _.Claimed |> List.exactlyOne

        Expect.equal
            (DecimalTotal.format claim.Amount, claim.Cases)
            ("0", 1L)
            "New zero claim replaces the revoked 100 amount"

        Expect.isEmpty
            (refreshed.Rows |> List.choose _.NetPaid)
            "The revoked case's return is excluded"

        Expect.isTrue
            (refreshed.Basis.ObservedAt >= initial.Basis.ObservedAt)
            "The fresh report states its own observation"
    }

let private outcomes (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = caseWorker fixture

        for number in 10..13 do
            let! opened = openCase fixture owner number None None
            accepted opened |> ignore

        for ix, target, outcome in [ 20, 10, "SETTLED"; 21, 11, "SETTLED"; 22, 12, "DECLINED" ] do
            let! closed =
                Service.executeSubmission
                    owner
                    (request
                        fixture
                        ix
                        ($"REPORT-{target}")
                        1L
                        "case.close"
                        (Map.ofList
                            [
                                Submissions.field BusinessField.Outcome,
                                SubmissionValue.Text outcome
                            ]))
                    token

            accepted closed |> ignore

        let! grouped = read owner CaseListFilter.empty CaseGrouping.Outcome

        Expect.equal
            (groups grouped)
            (Map.ofList [ "SETTLED", 2L; "DECLINED", 1L; "NOT_CLOSED", 1L ])
            "Only a recorded closure outcome joins its group"

        let! _ = verified database
        ()
    }

let private traversal (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = caseWorker fixture
        let month = DateOnly(2018, 1, 1)

        for index in 0..50 do
            let day =
                month
                    .AddMonths(index)
                    .ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)

            let! opened = openCase fixture owner (index + 10) (Some day) (Some "1")
            accepted opened |> ignore

        let selection =
            {
                Filter = CaseListFilter.empty<string>
                GroupBy = Some CaseGrouping.ReceivedMonth
            }

        let read selection cursor =
            Service.caseSummary
                owner
                {
                    Selection = selection
                    Page = { AfterCursor = cursor; Limit = 50 }
                }
                token

        let! firstResult = read selection None
        let first = success firstResult
        let! tailResult = read selection first.NextCursor
        let tail = success tailResult

        let month (row: CaseMoneyRow) =
            row.Group |> Option.map CaseGroupKey.token

        Expect.equal first.Rows.Length 50 "First report page uses the declared row bound"
        Expect.equal (first.Rows |> List.map month |> List.head) (Some "2018-01") "First month"

        Expect.equal
            (first.Rows |> List.map month |> List.last)
            (Some "2022-02")
            "Last month before the boundary"

        Expect.equal (tail.Rows |> List.map month) [ Some "2022-03" ] "Only the remaining month"

        Expect.equal
            (first.Counts.Total, tail.Counts.Total)
            (51L, 51L)
            "Counts describe the whole selection"

        Expect.isNone tail.NextCursor "Final page terminates"

        for page in [ first; tail ] do
            let human = CaseTotalsScreen.document page

            Expect.equal
                (human.Screen.Regions
                 |> List.filter (fun region -> region.Role = RegionRole.Results)
                 |> List.length)
                1
                "One Results region"

            Expect.equal human.NextCursor page.NextCursor "HUMAN preserves the page continuation"

        let! wrongGrouping =
            read
                { selection with
                    GroupBy = Some CaseGrouping.RegisteredMonth
                }
                first.NextCursor

        Expect.equal
            wrongGrouping
            (Outcome.Rejected Refusal.InvalidCursor)
            "Grouping binds the cursor"

        let! wrongFilter =
            read
                { selection with
                    Filter =
                        { selection.Filter with
                            Text = Some "REPORT-10"
                        }
                }
                first.NextCursor

        Expect.equal wrongFilter (Outcome.Rejected Refusal.InvalidCursor) "Filter binds the cursor"
        let! malformed = read selection (Some "bad!")

        Expect.equal
            malformed
            (Outcome.Rejected Refusal.InvalidCursor)
            "Malformed positions are refused"

        let empty =
            { selection with
                Filter =
                    { selection.Filter with
                        Text = Some "UNMATCHED-FIELD-REPORT"
                    }
            }

        let! groupedEmpty = read empty None
        let groupedEmpty = success groupedEmpty
        Expect.equal groupedEmpty.Counts.Total 0L "The empty selection has zero cases"
        Expect.isEmpty groupedEmpty.Rows "An empty grouped report has no data rows"
        let! overallEmpty = read { empty with GroupBy = None } None
        let overallEmpty = success overallEmpty

        Expect.equal
            (Encoding.UTF8.GetString(CaseSummaryCsv.rows overallEmpty))
            "ALL_CASES,0,,,,,,,\r\n"
            "The ungrouped empty scope retains exactly one blank-currency row"

        let! exported =
            ClaimCore.Server.CsvExport.traversal owner (ExportSelection.CaseSummary selection)
            |> ClaimCore.Server.CsvExport.materialize 65536 token

        let document = exported |> Result.defaultWith (sprintf "%A" >> failtest)
        let csv = document.Bytes

        Expect.equal
            document.RowCount
            51
            "Audit count covers actual encoded rows across continuations"

        let expected =
            [ CaseSummaryCsv.header; CaseSummaryCsv.rows first; CaseSummaryCsv.rows tail ]
            |> Array.concat

        Expect.equal
            csv
            expected
            "Complete export includes one header and the same two ordered pages"

        let lines =
            Encoding.UTF8.GetString(csv).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)

        Expect.equal lines.Length 52 "Exactly 51 records and one header"
        Expect.equal lines[51] "2022-03,1,EUR,1,1,,,," "Authored final record"
    }

let private currencyBoundary (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = caseWorker fixture
        let currencies = CodeVocabularies.currencies |> List.take 54 |> List.sort

        for index, currency in List.indexed currencies do
            let received = if index < 51 then "2026-08-01" else "2026-09-01"

            let! opened =
                openCaseInCurrency fixture owner (index + 10) (Some received) currency (Some "1")

            accepted opened |> ignore

        for grouping in [ Some CaseGrouping.ReceivedMonth; None ] do
            let request =
                {
                    Selection =
                        {
                            Filter = CaseListFilter.empty<string>
                            GroupBy = grouping
                        }
                    Page = { AfterCursor = None; Limit = 50 }
                }

            let! first = Service.caseSummary owner request token
            let first = success first

            let! next =
                Service.caseSummary
                    owner
                    { request with
                        Page =
                            { request.Page with
                                AfterCursor = first.NextCursor
                            }
                    }
                    token

            let next = success next

            let subject (row: CaseMoneyRow) =
                row.Currency |> Option.map Currency.format |> Option.defaultValue ""

            Expect.equal
                (first.Rows |> List.map subject)
                (List.take 50 currencies)
                "The first 50 ordinal currencies"

            Expect.equal
                (next.Rows |> List.map subject)
                (List.skip 50 currencies)
                "The remaining currencies"

            Expect.equal
                (first.Counts.Total, next.Counts.Total)
                (54L, 54L)
                "Page boundary does not change the scope count"

            Expect.isNone next.NextCursor "No further rows"

            if grouping.IsSome then
                Expect.equal
                    (first.Rows.Head.Count, next.Rows.Head.Count)
                    (51L, 51L)
                    "The same group straddles the row boundary"

        let who = CaseFixtures.actorId (id fixture 1)

        for number in 10..11 do
            let! updated =
                Service.executeSubmission
                    owner
                    (Submissions.createRaw
                        "work.update"
                        (id fixture (number + 70))
                        (OperationBinding.Work(CaseSelector.Reference($"REPORT-{number}"), 0L))
                        (Submissions.work
                            {
                                Responsible = WorkValue.Set(ActorId.format who)
                                Summary = WorkValue.Keep
                            }))
                    token

            accepted updated |> ignore

        let request =
            {
                Selection =
                    {
                        Filter = CaseListFilter.empty<string>
                        GroupBy = Some CaseGrouping.Responsible
                    }
                Page = { AfterCursor = None; Limit = 1 }
            }

        let! first = Service.caseSummary owner request token
        let first = success first
        Expect.isSome first.NextCursor "The responsible account spans multiple currency rows"

        let account row =
            match (row: CaseMoneyRow).Group with
            | Some(CaseGroupKey.Responsible account) -> account
            | _ -> failtest "Concrete responsible groups precede missing"

        let! rename =
            personSubmission
                fixture.Store
                (id fixture 90)
                fixture.Owner
                None
                "people.setProfile"
                (Submissions.profile
                    { DomainInputs.actorProfile with
                        DisplayName = "Renamed report owner"
                    })

        let! changed = Service.executeSubmission owner rename token
        acceptedPeople changed

        let! continued =
            Service.caseSummary
                owner
                { request with
                    Page =
                        { request.Page with
                            AfterCursor = first.NextCursor
                        }
                }
                token

        Expect.equal
            continued
            (Outcome.Rejected Refusal.TraversalChanged)
            "A changed responsible-account caption requires a fresh traversal"

        Expect.equal
            (account first.Rows.Head).ActorId
            who
            "The first row uses immutable actor identity"

        let! refreshed = Service.caseSummary owner request token
        let refreshed = success refreshed
        Expect.isSome refreshed.NextCursor "The renamed account still spans multiple currency rows"

        let! next =
            Service.caseSummary
                owner
                { request with
                    Page =
                        { request.Page with
                            AfterCursor = refreshed.NextCursor
                        }
                }
                token

        let next = success next

        Expect.equal
            (account next.Rows.Head).ActorId
            who
            "A fresh traversal preserves the renamed account's group identity"

        Expect.equal
            (next.Rows.Head.Currency |> Option.map Currency.format)
            (Some currencies[1])
            "The next row is still the second currency"

        Expect.equal
            (account next.Rows.Head).DisplayName
            (DisplayName.Named(
                PartyName.parse "Renamed report owner"
                |> Result.defaultWith (string >> failtest)
            ))
            "The name is hydrated anew for the visible group"

    }

[<Tests>]
let tests =
    testList
        "reporting with the published case selection"
        [
            testCase "Europe/Riga business date crosses midnight before UTC" (fun () ->
                let instant = DateTimeOffset(2026, 11, 30, 22, 30, 0, TimeSpan.Zero)
                let calendar = InstallationCheck.calendar "Europe/Riga"

                let result =
                    CaseReportDates.record
                        (CalendarDate.ofDate (calendar.DateAt instant))
                        (Some CaseStatus.Opened)
                        None

                Expect.equal
                    result.Registered
                    (date "2026-12-01")
                    "Registration reports the local date")
            testCaseAsync
                "month grouping, date predicates, currency counts, access and CSV share authorized cases"
                (run monthReporting)
            testCaseAsync "current outcome projection groups the recorded closures" (run outcomes)
            testCaseAsync
                "51 Received months traverse bounded HUMAN and complete CSV"
                (run traversal)
            testCaseAsync
                "report row cursor splits currencies across the group boundary"
                (run currencyBoundary)
        ]
