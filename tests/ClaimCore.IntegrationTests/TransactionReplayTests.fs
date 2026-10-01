module ClaimCore.IntegrationTests.TransactionReplayTests

open System
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.Fixtures

let private reopenPersistenceTest =
    testCase "reopen connection and read all stored fields" (fun () ->
        let request = newRequest ()

        do
            use database = store ()

            CommandExecution.executeAsync (database :> IClaimStore) clock request
            |> await
            |> accepted
            |> ignore

        use reopened = store ()

        let snapshot =
            (reopened :> IClaimStore).Get(request.CaseReference)
            |> await
            |> accepted
            |> Option.map Claim.view

        let expected =
            Claim.decide (clock.Capture().EffectiveBusinessDate) request None
            |> accepted
            |> Claim.view

        Expect.isTrue
            (snapshot = Some expected)
            "All thirteen fields and technical revision survive a real connection reopen")

let private exactReplayTest =
    testCase "[CC-APP-002] exact replay returns original receipt, not current state" (fun () ->
        use database = store ()
        let service = database :> IClaimStore
        let request = newRequest ()

        let original =
            CommandExecution.executeAsync service clock request |> await |> accepted

        CommandExecution.executeAsync service clock (next request 1L Command.Close)
        |> await
        |> accepted
        |> ignore

        let replay =
            CommandExecution.executeAsync service clock request |> await |> accepted

        Expect.isTrue replay.Replayed "Replay"
        Expect.equal (Claim.view replay.Case).Version 1L "Historical receipt"
        Expect.equal replay.RecordedAt original.RecordedAt "Original acceptance time"

        let current =
            service.Get(request.CaseReference) |> await |> accepted |> Option.map Claim.view

        Expect.equal
            (current |> Option.map (fun value -> value.Version))
            (Some 2L)
            "Current state separate")

let private idempotencyConflictTest =
    testCase "[CC-APP-002] same ID with different content fails" (fun () ->
        use database = store ()
        let service = database :> IClaimStore
        let request = newRequest ()

        CommandExecution.executeAsync service clock request
        |> await
        |> accepted
        |> ignore

        let result =
            CommandExecution.executeAsync service clock { request with Command = Command.Close }
            |> await

        Expect.equal
            (result |> Result.map (fun value -> value.OperationId))
            (Error CoreFailure.IdempotencyConflict)
            "No ID reuse")

let tests =
    testList
        "persistence and replay"
        [ reopenPersistenceTest; exactReplayTest; idempotencyConflictTest ]
