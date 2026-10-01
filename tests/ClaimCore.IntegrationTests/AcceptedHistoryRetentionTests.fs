module ClaimCore.IntegrationTests.AcceptedHistoryRetentionTests

open System
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.AcceptedReplayStorageTests

let tests =
    testCase
        "[CC-APP-002] PostgreSQL accepted history without preparation remains replayable"
        (fun () ->
            let input = openRequest (Guid.NewGuid()) ("HISTORY-" + Guid.NewGuid().ToString("N"))

            use claims = store ()

            match CommandExecution.executeAsync claims clock input |> await with
            | Ok _ -> ()
            | Error _ -> failtest "Synthetic witnessed command must accept."

            ageAccepted input.OperationId

            PreparationPruning.prune
                (adminConnection ())
                { PreparationPruneOptions.defaults with
                    SettledRetentionDays = 1
                    BatchLimit = 100
                }
            |> completedAdministration
            |> ignore

            Expect.equal
                (rowCount "request_preparations" input.OperationId)
                0L
                "Terminal preparation is explicitly pruned"

            let bytes, businessDays, observedAt = acceptedEvidence input.OperationId

            Expect.sequenceEqual
                bytes
                (RequestRecord.encode input)
                "Witnessed commit retains request bytes"

            Expect.equal
                businessDays
                (DateOnly(2026, 9, 7).DayNumber - DateOnly(2000, 1, 1).DayNumber)
                "Witnessed commit retains business date"

            Expect.equal
                observedAt
                (DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero))
                "Witnessed commit retains capture instant"

            use runtime = openRuntime ()
            expectAccepted (actorCore runtime) input
            Expect.equal (rowCount "case_changes" input.OperationId) 1L "Accepted history retained")
