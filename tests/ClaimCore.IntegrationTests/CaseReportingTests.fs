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
                    Filter = filter
                    GroupBy = Some group
                }
                token

        return success result
    }

let private openCase fixture owner number received claimed =
    let values =
        [
            match received with
            | Some received ->
                Submissions.field BusinessField.ReceivedDate, SubmissionValue.Text received
            | None -> ()
            match claimed with
            | Some claimed ->
                Submissions.field BusinessField.ClaimedAmount, SubmissionValue.Text claimed
                Submissions.field BusinessField.ClaimedCurrency, SubmissionValue.Text "EUR"
            | None -> ()
        ]
        |> Map.ofList

    Service.executeSubmission
        owner
        (request fixture number ($"REPORT-{number}") 0L "case.open" values)
        token

let private groups (report: CaseWorklistSummary) =
    report.Groups
    |> List.map (fun group -> CaseGroupKey.token group.Group, group.Count)
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

        let money = summary.Claimed |> List.exactlyOne

        Expect.equal
            (Currency.format money.Currency, DecimalTotal.format money.Amount, money.Cases)
            ("EUR", "100", 2L)
            "Explicit zero contributes to the count and absent money does not"

        let net = summary.NetPaid |> List.exactlyOne

        Expect.equal
            (Currency.format net.Currency, DecimalTotal.format net.Amount, net.Cases)
            ("EUR", "-20", 1L)
            "Net paid remains signed and counts its contributing case"

        let human = HumanReadDocuments.summary summary

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

        let! missing =
            read
                owner
                { all with
                    Dates = Map.ofList [ CaseDateFilter.ReceivedFrom, date "2026-10-01" ]
                }
                CaseGrouping.ReceivedMonth

        Expect.equal missing.Counts.Total 8L "A bounded received date excludes cases without a date"

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
            (restricted.Claimed |> List.map _.Cases)
            [ 1L ]
            "Totals cannot include excluded cases"
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
        ]
