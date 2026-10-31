module ClaimCore.IntegrationTests.WorklistCursorPrecisionTests

open System
open System.Globalization
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application
open ClaimCore.IntegrationTests.WorklistFixtures

let private moveOneTick (cursor: string) (time: DateTimeOffset) =
    let padded =
        cursor.Replace('-', '+').Replace('_', '/')
        + String('=', (4 - cursor.Length % 4) % 4)

    let original = Convert.FromBase64String padded |> Encoding.UTF8.GetString

    let format (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)

    let changed = original.Replace(format time, format (time.AddTicks 1L))

    Expect.notEqual
        original
        changed
        "The negative control changes the actual serialized keyset instant"

    Convert
        .ToBase64String(Encoding.UTF8.GetBytes changed)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_')

[<Tests>]
let tests =
    testCaseAsync
        "recent sub-microsecond cursors are refused by PostgreSQL and memory"
        (run (fun pair ->
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

                let! first = read pair.Postgres None

                let cursor =
                    match first with
                    | Outcome.Succeeded page -> page.NextCursor |> Option.get
                    | _ -> failtest "Fixture page"

                let position =
                    Cursors.decodeCases admitted cursor
                    |> Result.defaultWith (fun _ -> failtest "Fixture cursor")

                let time =
                    match position with
                    | CaseListPosition.Recent(time, _) -> time
                    | _ -> failtest "Recent position required"

                let forged = moveOneTick cursor time

                for actor in [ pair.Postgres; pair.Memory ] do
                    let! result = read actor (Some forged)

                    Expect.equal
                        result
                        (Outcome.Rejected Refusal.InvalidCursor)
                        "A plain cursor cannot select a precision with different store meanings"
            }))
