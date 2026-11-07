module ClaimCore.IntegrationTests.ReceiptObservationTests

open System
open System.Globalization
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

[<Tests>]
let tests =
    testCaseAsync
        "receipt observations advance independently of accepted event time"
        (async {
            let! database = PostgresFixture.create () |> Async.AwaitTask

            try
                let! _ = caseWorker database.Fixture |> Async.AwaitTask
                let observed = ObservedTransactions.Store(database.Fixture.Store)

                let fixture =
                    { database.Fixture with
                        Store = observed
                    }

                let service = core fixture fixture.Owner
                let token = CancellationToken.None

                let input =
                    Inputs.caseOperation (
                        request 100 "OBS-RECEIPT" 0L (AuthoredCaseAct.Open registration)
                    )

                let! reply = Service.execute service input token |> Async.AwaitTask
                let initial = accepted reply

                Expect.equal
                    initial.RecordedAt
                    observed.ObservedAt
                    "The event retains its accepting transaction instant"

                Expect.equal
                    initial.Basis.ObservedAt
                    observed.ObservedAt
                    "Initial basis belongs to the accepting snapshot"

                let check receipt =
                    expectReceipt receipt initial "Historical acceptance metadata stays unchanged"

                    Expect.equal
                        receipt.Basis.ObservedAt
                        observed.ObservedAt
                        "Each path uses its real current transaction snapshot"

                    Expect.equal
                        receipt.Basis.BusinessDate
                        (DateOnly.FromDateTime observed.ObservedAt.UtcDateTime)
                        "Business date follows that snapshot"

                    let wire = ReceiptCodecs.receipt.Write receipt

                    let format (value: DateTimeOffset) =
                        value
                            .ToUniversalTime()
                            .ToString(
                                "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                                CultureInfo.InvariantCulture
                            )

                    Expect.equal
                        (JsonValue.tryMember "basis" wire
                         |> Option.bind (JsonValue.tryMember "observedAt"))
                        (Some(JsonValue.String(format observed.ObservedAt)))
                        "Wire basis carries the current provider instant"

                    Expect.equal
                        (JsonValue.tryMember "recordedAt" wire)
                        (Some(JsonValue.String(format initial.RecordedAt)))
                        "Wire acceptance time stays historical"

                let! replay = Service.execute service input token |> Async.AwaitTask
                check (replayReceipt replay)

                let lookup =
                    {
                        OperationId = OperationInput.operationId input
                    }

                let! reply = Service.observe service lookup token |> Async.AwaitTask

                match reply.Outcome with
                | Outcome.Succeeded receipt -> check receipt
                | other -> failtestf "%A" other

                let! reply = Service.status service lookup token |> Async.AwaitTask

                match reply.Outcome with
                | Outcome.Succeeded(OperationStatus.Accepted receipt) -> check receipt
                | other -> failtestf "%A" other

                let! reply = Service.resolve service lookup token |> Async.AwaitTask

                match reply.Outcome with
                | Outcome.Succeeded(Resolved.Accepted receipt) -> check receipt
                | other -> failtestf "%A" other
            finally
                database.Fixture.Dispose()
        })
