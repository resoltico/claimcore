module ClaimCore.IntegrationTests.WorklistCursorPrecisionTests

open System
open System.Globalization
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.IntegrationTests.WorklistFixtures

[<Tests>]
let tests =
    testCaseAsync
        "recent PostgreSQL traversal authenticates its continuation and refuses the old grammar"
        (run (fun fixture ->
            task {
                let filter =
                    { CaseListFilter.empty<string> with
                        Order = CaseListOrder.Recent
                    }

                let admitted =
                    CaseListFilter.admit filter
                    |> Result.defaultWith (fun _ -> failtest "Fixture filter")

                let read actor cursor =
                    Service.list
                        actor
                        {
                            Page = { AfterCursor = cursor; Limit = 1 }
                            Filter = filter
                        }
                        token

                let! first = read fixture.Service None

                let cursor =
                    match first with
                    | Outcome.Succeeded page -> page.NextCursor |> Option.get
                    | _ -> failtest "Fixture page"

                let! baseline = read fixture.Service (Some cursor)

                match baseline with
                | Outcome.Succeeded _ -> ()
                | _ -> failtest "The unmodified opaque continuation must be admitted"

                let last = cursor[cursor.Length - 1]
                let forged = cursor[.. cursor.Length - 2] + (if last = 'A' then "B" else "A")
                let! result = read fixture.Service (Some forged)

                Expect.equal
                    result
                    (Outcome.Rejected Refusal.InvalidCursor)
                    "Changing even one encoded character cannot change the keyset"

                let legacy =
                    Cursors.encodeCases
                        fixture.Database.Fixture.InstallationId
                        admitted
                        (CaseListPosition.Recent(DateTimeOffset.UnixEpoch, "CASE-1"))

                let! rejected = read fixture.Service (Some legacy)

                Expect.equal
                    rejected
                    (Outcome.Rejected Refusal.InvalidCursor)
                    "A previous plain cursor is refused"
            }))
