module ClaimCore.IntegrationTests.TransactionTests

open System
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.IntegrationTests.Fixtures

let private reopenPersistenceTest =
    testCase "reopen connection and read all stored fields" (fun () ->
        let request = newRequest ()

        do
            use database = store ()

            Service.executeAsync (database :> IClaimStore) clock request
            |> await
            |> accepted
            |> ignore

        use reopened = store ()

        let snapshot =
            (reopened :> IClaimStore).Get(request.CaseReference)
            |> await
            |> accepted
            |> Option.map Claim.view

        let expected = Claim.decide (clock.Today()) request None |> accepted |> Claim.view

        Expect.isTrue
            (snapshot = Some expected)
            "All thirteen fields and technical revision survive a real connection reopen")

let private exactReplayTest =
    testCase "[CC-APP-002] exact replay returns original receipt, not current state" (fun () ->
        use database = store ()
        let service = database :> IClaimStore
        let request = newRequest ()
        let original = Service.executeAsync service clock request |> await |> accepted

        Service.executeAsync service clock (next request 1L Command.Close)
        |> await
        |> accepted
        |> ignore

        let replay = Service.executeAsync service clock request |> await |> accepted
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
        Service.executeAsync service clock request |> await |> accepted |> ignore

        let result =
            Service.executeAsync service clock { request with Command = Command.Close }
            |> await

        Expect.equal
            (result |> Result.map (fun value -> value.OperationId))
            (Error CoreFailure.IdempotencyConflict)
            "No ID reuse")

let private replayTests =
    testList
        "persistence and replay"
        [ reopenPersistenceTest; exactReplayTest; idempotencyConflictTest ]

let private sameIdConcurrencyTests =
    testList
        "same operation concurrency"
        [
            testCase "concurrent same-ID attempts produce one accepted change" (fun () ->
                use database = store ()
                let service = database :> IClaimStore
                let request = newRequest ()

                let attempts = [| for _ in 1..8 -> Service.executeAsync service clock request |]

                let results = Task.WhenAll(attempts) |> await |> Array.map accepted

                Expect.equal
                    (results |> Array.filter (fun item -> not item.Replayed) |> Array.length)
                    1
                    "Only one new commit"

                let history = service.History(request.CaseReference, 0L) |> await |> accepted

                Expect.equal history.Items.Length 1 "One audit row")
        ]

let private absentCaseConcurrencyTests =
    testList
        "absent case concurrency"
        [
            testCase
                "concurrent distinct opens of one absent reference accept exactly one revision"
                (fun () ->
                    use database = store ()
                    let service = database :> IClaimStore
                    let first = newRequest ()

                    let second =
                        { first with
                            OperationId = Guid.NewGuid()
                        }

                    let attempts =
                        [| first; second |] |> Array.map (Service.executeAsync service clock)

                    let outcomes = Task.WhenAll(attempts) |> await

                    Expect.equal
                        (outcomes |> Array.filter Result.isOk |> Array.length)
                        1
                        "One open wins"

                    let conflicts =
                        outcomes
                        |> Array.choose (function
                            | Error(CoreFailure.Domain(DomainError.VersionConflict 1L)) -> Some()
                            | _ -> None)

                    Expect.equal conflicts.Length 1 "The contender observes revision one"

                    let history = service.History(first.CaseReference, 0L) |> await |> accepted

                    Expect.equal history.Items.Length 1 "One current row and one retained receipt")
        ]

let private revisionConcurrencyTests =
    testList
        "revision concurrency"
        [
            testCase
                "concurrent different commands against same revision cannot lose an update"
                (fun () ->
                    use database = store ()
                    let service = database :> IClaimStore
                    let initial = newRequest ()
                    Service.executeAsync service clock initial |> await |> accepted |> ignore

                    let decide =
                        Command.Decide
                            {
                                PaymentDecisionDate = "2026-08-15"
                                PayableAmount = "800"
                                PayableCurrency = "EUR"
                            }

                    let attempts =
                        [| next initial 1L decide; next initial 1L Command.Close |]
                        |> Array.map (Service.executeAsync service clock)

                    let outcomes = Task.WhenAll(attempts) |> await

                    Expect.equal
                        (outcomes |> Array.filter Result.isOk |> Array.length)
                        1
                        "One winner"

                    let conflicts =
                        outcomes
                        |> Array.choose (function
                            | Error(CoreFailure.Domain(DomainError.VersionConflict _)) -> Some()
                            | _ -> None)

                    Expect.equal conflicts.Length 1 "One explicit stale revision")
        ]

let concurrencyQualificationTests =
    testList
        "concurrency qualification"
        [ sameIdConcurrencyTests; absentCaseConcurrencyTests; revisionConcurrencyTests ]

let private lifecycleTests =
    testList
        "lifecycle transactions"
        [
            testCase "full lifecycle and payment correction retain all versions" (fun () ->
                use database = store ()
                let service = database :> IClaimStore
                let initial = newRequest ()
                Service.executeAsync service clock initial |> await |> accepted |> ignore

                let actions =
                    [
                        Command.Decide
                            {
                                PaymentDecisionDate = "2026-08-15"
                                PayableAmount = "750"
                                PayableCurrency = "USD"
                            }
                        Command.RecordPayment "2026-08-20"
                        Command.Close
                        Command.Reopen
                        Command.ClearPayment
                    ]

                for index, action in actions |> List.indexed do
                    Service.executeAsync service clock (next initial (int64 index + 1L) action)
                    |> await
                    |> accepted
                    |> ignore

                let history = service.History(initial.CaseReference, 0L) |> await |> accepted

                Expect.equal history.Items.Length 6 "Every accepted version retained"

                let current =
                    service.Get(initial.CaseReference)
                    |> await
                    |> accepted
                    |> Option.map Claim.view

                Expect.isTrue
                    ((current |> Option.bind (fun value -> value.Fields.PaymentDate)).IsNone)
                    "Payment record corrected"

                let currency = current |> Option.bind (fun value -> value.Fields.PayableCurrency)
                Expect.isTrue (currency = Some "USD") "Independent decision currency")
        ]

let private operationTests =
    testList
        "operation lookup"
        [
            testCase "operation lookup identifies exact accepted result" (fun () ->
                use database = store ()
                let service = database :> IClaimStore
                let request = newRequest ()
                Service.executeAsync service clock request |> await |> accepted |> ignore

                let result = service.Operation(request.OperationId) |> await |> accepted

                let reference =
                    result
                    |> Option.map (fun value -> (Claim.view value.Case).Fields.CaseReference)

                Expect.isTrue (reference = Some request.CaseReference) "Operation identity")
        ]

let tests =
    testList
        "PostgreSQL transactions"
        [ replayTests; concurrencyQualificationTests; lifecycleTests; operationTests ]
