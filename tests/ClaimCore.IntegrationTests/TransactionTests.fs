module ClaimCore.IntegrationTests.TransactionTests

open System.Threading
open System
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.Fixtures

let private concurrentAccepted =
    function
    | Ok receipt -> receipt
    | Error failure ->
        let category =
            match failure with
            | CoreFailure.Domain _ -> "DOMAIN"
            | CoreFailure.ResourceUnavailable -> "RESOURCE_UNAVAILABLE"
            | CoreFailure.InvalidCaseListCursor -> "INVALID_CURSOR"
            | CoreFailure.IdempotencyConflict -> "IDENTITY_CONFLICT"
            | CoreFailure.StoreUnavailable -> "STORE_UNAVAILABLE"
            | CoreFailure.CommitOutcomeUnknown _ -> "COMMIT_OUTCOME_UNKNOWN"
            | CoreFailure.StoreCorrupt -> "STORE_CORRUPT"
            | CoreFailure.SchemaMismatch -> "SCHEMA_MISMATCH"

        failtestf "Concurrent exact operation was not accepted: %s." category

let private sameIdConcurrencyTests =
    testList
        "same operation concurrency"
        [
            testCase "concurrent same-ID attempts produce one accepted change" (fun () ->
                use database = store ()
                let service = database :> IClaimStore
                let request = newRequest ()

                let attempts =
                    [| for _ in 1..8 -> CommandExecution.executeAsync service clock request |]

                let results = Task.WhenAll(attempts) |> await |> Array.map concurrentAccepted

                Expect.equal
                    (results |> Array.filter (fun item -> not item.Replayed) |> Array.length)
                    1
                    "Only one new commit"

                let history =
                    service.History(request.CaseReference, 0L, CancellationToken.None)
                    |> await
                    |> accepted

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
                        [| first; second |]
                        |> Array.map (CommandExecution.executeAsync service clock)

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

                    let categories =
                        outcomes
                        |> Array.map (function
                            | Ok _ -> "ACCEPTED"
                            | Error(CoreFailure.Domain(DomainError.VersionConflict _)) ->
                                "REVISION_CONFLICT"
                            | Error CoreFailure.ResourceUnavailable -> "RESOURCE_UNAVAILABLE"
                            | Error(CoreFailure.Domain _) -> "DOMAIN_REJECTION"
                            | Error CoreFailure.StoreUnavailable -> "STORE_UNAVAILABLE"
                            | Error CoreFailure.StoreCorrupt -> "STORE_CORRUPT"
                            | Error(CoreFailure.CommitOutcomeUnknown _) -> "COMMIT_UNKNOWN"
                            | Error _ -> "OTHER_FAILURE")

                    Expect.equal
                        conflicts.Length
                        1
                        ("The contender observes revision one; safe categories: "
                         + String.concat "," categories)

                    let history =
                        service.History(first.CaseReference, 0L, CancellationToken.None)
                        |> await
                        |> accepted

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

                    CommandExecution.executeAsync service clock initial
                    |> await
                    |> accepted
                    |> ignore

                    let decide =
                        Command.Decide
                            {
                                PaymentDecisionDate = "2026-08-15"
                                PayableAmount = "800"
                                PayableCurrency = "EUR"
                            }

                    let attempts =
                        [| next initial 1L decide; next initial 1L Command.Close |]
                        |> Array.map (CommandExecution.executeAsync service clock)

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

                CommandExecution.executeAsync service clock initial
                |> await
                |> accepted
                |> ignore

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
                    CommandExecution.executeAsync
                        service
                        clock
                        (next initial (int64 index + 1L) action)
                    |> await
                    |> accepted
                    |> ignore

                let history =
                    service.History(initial.CaseReference, 0L, CancellationToken.None)
                    |> await
                    |> accepted

                Expect.equal history.Items.Length 6 "Every accepted version retained"

                let current =
                    service.Get(initial.CaseReference, CancellationToken.None)
                    |> await
                    |> accepted
                    |> Option.map Claim.view

                Expect.isTrue
                    ((current |> Option.bind (fun value -> value.Fields.PaymentDate)).IsNone)
                    "Payment record corrected"

                let currency = current |> Option.bind (fun value -> value.Fields.PayableCurrency)
                Expect.isTrue (currency = Some "USD") "Independent decision currency")
        ]

let tests =
    testList
        "PostgreSQL transactions"
        [
            TransactionReplayTests.tests
            concurrencyQualificationTests
            lifecycleTests
            OperationLookupTests.tests
        ]
