module ClaimCore.IntegrationTests.TransactionFailureTests

open System
open System.IO
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreContractTests

let private classify error before during =
    Expect.equal (Transactions.failure false error) before "Before commit"
    Expect.equal (Transactions.failure true error) during "During commit confirmation"

let private classification () =
    for error in
        [
            ArgumentException("synthetic") :> exn
            InvalidOperationException("synthetic")
            InvalidCastException("synthetic")
            FormatException("synthetic")
            OverflowException("synthetic")
        ] do
        classify error StoreFailure.Internal StoreFailure.Internal

    classify (RecordCorruptException "synthetic") StoreFailure.Integrity StoreFailure.Integrity

    for error in
        [
            IOException("synthetic") :> exn
            NpgsqlException("synthetic")
            TimeoutException("synthetic")
        ] do
        classify error StoreFailure.Unavailable StoreFailure.CommitOutcomeUnknown

    let schema = PostgresException("synthetic", "ERROR", "ERROR", "42P01")
    classify schema StoreFailure.SchemaMismatch StoreFailure.SchemaMismatch
    classify (OperationCanceledException()) StoreFailure.Cancelled StoreFailure.CommitOutcomeUnknown

let private programmerFailure () =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            let! before =
                Verification.verify database.OwnerSource None CancellationToken.None
                |> Async.AwaitTask

            let! outcome =
                database.Fixture.Store.WriteCase(
                    database.Fixture.Owner,
                    id 80,
                    "SYNTHETIC-INTERNAL",
                    (fun _ -> invalidArg "synthetic" "synthetic programming failure"),
                    CancellationToken.None
                )
                |> Async.AwaitTask

            Expect.equal
                outcome
                (Error StoreFailure.Internal)
                "Unexpected decision failure is not corruption"

            let! after =
                Verification.verify database.OwnerSource None CancellationToken.None
                |> Async.AwaitTask

            Expect.equal after before "Failed decision accepted nothing and left a verifiable store"
        finally
            database.Fixture.Dispose()
    }

[<Tests>]
let tests =
    testList
        "store fault boundaries"
        [
            testCase "failure classes preserve commit evidence" classification
            testCaseAsync
                "unexpected decision failure rolls back before commit"
                (programmerFailure ())
        ]
