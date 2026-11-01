module ClaimCore.IntegrationTests.WorkDateBasisTests

open System
open System.Globalization
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected an actual successful read: %A" other

let private dates (database: PostgresFixture.Database) (basis: WorkDateBasis) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT to_char($1::timestamptz AT TIME ZONE $2,'YYYY-MM-DD'),to_char($1::timestamptz AT TIME ZONE 'UTC','YYYY-MM-DD')"
                [| box basis.ObservedAt; box basis.TimeZoneId |]
                token

        return
            DateOnly.ParseExact(
                DatabaseSession.value<string> 0 rows.Head,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture
            ),
            DateOnly.ParseExact(
                DatabaseSession.value<string> 1 rows.Head,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture
            )
    }

let private qualify () =
    task {
        let mutable crossedUtcDay = false
        // This order covers the UTC date boundary even if the two fresh installations straddle midnight.
        for zone in [ "Pacific/Kiritimati"; "Pacific/Pago_Pago" ] do
            let! database = PostgresFixture.createWithZone zone

            try
                let! actor = caseWorker database.Fixture

                let! opened =
                    Service.execute
                        actor
                        (Inputs.caseOperation (
                            request 50 "ZONE-WORK" 0L (AuthoredCaseAct.Open registration)
                        ))
                        token

                let opened = accepted opened |> caseReceipt
                let caseId = opened.CaseId
                let! initial = Service.get actor { Target = CaseSelector.Id caseId } token
                let initial = read initial
                let! expected, utc = dates database initial.Work.Basis

                Expect.equal
                    initial.Work.Basis.TimeZoneId
                    zone
                    "The admitted installation zone reaches the actual case read"

                Expect.equal
                    initial.Work.Basis.BusinessDate
                    expected
                    "PostgreSQL independently establishes the date at the database observation instant"

                crossedUtcDay <- crossedUtcDay || expected <> utc

                let! changed =
                    Service.execute
                        actor
                        (Inputs.caseOperation (
                            request
                                51
                                "ZONE-WORK"
                                1L
                                (AuthoredCaseAct.UpdateWork
                                    {
                                        Responsible = WorkValue.Keep
                                        FollowUp =
                                            WorkValue.Set(
                                                utc.ToString(
                                                    "yyyy-MM-dd",
                                                    CultureInfo.InvariantCulture
                                                )
                                            )
                                    })
                        ))
                        token

                accepted changed |> ignore
                let! current = Service.get actor { Target = CaseSelector.Id caseId } token
                let current = read current
                let! expected, _ = dates database current.Work.Basis

                let due =
                    if utc < expected then CaseDue.Overdue
                    elif utc > expected then CaseDue.Upcoming
                    else CaseDue.Today

                Expect.equal
                    current.Work.Basis.BusinessDate
                    expected
                    "The work observation uses its own database instant"

                Expect.equal
                    current.Work.Due
                    due
                    "Due status follows the installation date rather than the UTC or presentation calendar"

                let! page =
                    Service.list
                        actor
                        {
                            Page = { AfterCursor = None; Limit = 50 }
                            Filter = CaseListFilter.empty
                        }
                        token

                let page = read page
                let! pageDate, _ = dates database page.Basis

                Expect.equal
                    page.Basis.BusinessDate
                    pageDate
                    "List basis is independently valid for its separate observation"

                let! summary = Service.caseSummary actor CaseListFilter.empty token
                let summary = read summary
                let! summaryDate, _ = dates database summary.Basis

                Expect.equal
                    summary.Basis.BusinessDate
                    summaryDate
                    "Summary basis is independently valid for its separate observation"
            finally
                database.Fixture.Dispose()

        Expect.isTrue
            crossedUtcDay
            "At least one actual installation date differs from the UTC calendar day"
    }

[<Tests>]
let tests =
    testCaseAsync
        "database-observed work dates follow non-UTC installation zones across UTC calendar boundaries"
        (async { do! qualify () |> Async.AwaitTask })
