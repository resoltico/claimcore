module ClaimCore.Tests.CaseReportingTests

open System
open Expecto
open ClaimCore.Domain

let private date raw =
    CalendarDate.parse raw
    |> Result.defaultWith (fun issue -> failtestf "Synthetic date is invalid: %A" issue)

[<Tests>]
let tests =
    testList
        "case reporting semantics"
        [
            testCase "observed facts use the accepted opening and latest closing dates" (fun () ->
                let origin = date "2026-08-19"
                let closedDate = date "2026-11-18"
                let meeting = date "2026-11-19"
                let opened = CaseReportDates.record origin (Some CaseStatus.Opened) None

                let closed =
                    CaseReportDates.record closedDate (Some CaseStatus.Closed) (Some opened)

                let later = CaseReportDates.record meeting (Some CaseStatus.Closed) (Some closed)

                Expect.equal later.Registered origin "Registration date stays fixed"
                Expect.equal later.Closed (Some closedDate) "Later work preserves the closing date"
                Expect.equal later.LastActivity meeting "Activity uses the latest operation date"

                Expect.equal
                    (DaysOpenBand.daysOpen meeting later)
                    91
                    "Closed cases stop accumulating days"

                let reopened = CaseReportDates.record meeting (Some CaseStatus.Opened) (Some later)
                Expect.isNone reopened.Closed "Reopened cases have no current closure date")

            testCase
                "calendar-date filters compare inclusively and exclude missing facts"
                (fun () ->
                    let limit = Some(date "2026-11-18")

                    Expect.isTrue
                        (CaseDateFilter.matches CaseDateFilter.ReceivedFrom limit limit)
                        "Lower endpoint is included"

                    Expect.isTrue
                        (CaseDateFilter.matches CaseDateFilter.ReceivedThrough limit limit)
                        "Upper endpoint is included"

                    Expect.isFalse
                        (CaseDateFilter.matches CaseDateFilter.ReceivedThrough limit None)
                        "Unrecorded dates cannot match a bounded period"

                    Expect.isTrue
                        (CaseDateFilter.matches CaseDateFilter.ReceivedThrough None None)
                        "An omitted filter leaves an unrecorded fact in the selection"

                    Expect.isFalse
                        (CaseDateFilter.matches
                            CaseDateFilter.InactiveSince
                            limit
                            (Some(date "2026-11-19")))
                        "Actions after the inactivity filter are excluded")

            testCase "elapsed days use plain business dates and inclusive band ceilings" (fun () ->
                let opened =
                    CaseReportDates.record (date "2026-10-19") (Some CaseStatus.Opened) None

                Expect.equal (DaysOpenBand.daysOpen (date "2026-11-18") opened) 30 "30 days open"

                for days, band in
                    [
                        0, DaysOpenBand.Through30
                        30, DaysOpenBand.Through30
                        31, DaysOpenBand.Through90
                        90, DaysOpenBand.Through90
                        91, DaysOpenBand.Through180
                        180, DaysOpenBand.Through180
                        181, DaysOpenBand.Through365
                        365, DaysOpenBand.Through365
                        366, DaysOpenBand.Over365
                    ] do
                    Expect.equal (DaysOpenBand.ofDays days) band (sprintf "Days %d" days))
        ]
