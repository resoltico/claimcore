module ClaimCore.IntegrationTests.AcceptedReplayStorageTests

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.IntegrationTests.Fixtures

let private request operationId reference = openRequest operationId reference

let private openRuntime () =
    Runtime.OpenPostgres(appConnection (), CancellationToken.None)
    |> await
    |> Result.defaultWith (fun _ -> failtest "Synthetic runtime must open.")

let private rowCount table operationId =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            $"SELECT count(*) FROM claimcore.{table} WHERE operation_id = @operation",
            connection
        )

    Sql.uuid command "operation" operationId
    command.ExecuteScalar() :?> int64

let private ageAccepted operationId =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.case_changes SET recorded_at = clock_timestamp() - interval '3 days' WHERE operation_id = @operation",
            connection
        )

    Sql.uuid command "operation" operationId
    Expect.equal (command.ExecuteNonQuery()) 1 "Only this synthetic receipt is aged"

let private snapshot operationId =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT snapshot::text FROM claimcore.case_changes WHERE operation_id = @operation",
            connection
        )

    Sql.uuid command "operation" operationId

    match command.ExecuteScalar() with
    | :? string as value -> value
    | _ -> failtest "Synthetic receipt snapshot must exist."

let private replaceSnapshot operationId value =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.case_changes SET snapshot = @snapshot::jsonb WHERE operation_id = @operation",
            connection
        )

    Sql.uuid command "operation" operationId
    Sql.text command "snapshot" value
    Expect.equal (command.ExecuteNonQuery()) 1 "Only this synthetic snapshot is changed"

let private expectAccepted (core: IClaimsCore) input =
    match core.Prepare(input, CancellationToken.None) |> await with
    | PrepareOutcome.ObservedAccepted receipt ->
        Expect.equal receipt.OperationId input.OperationId "Prepare observes exact receipt"
    | _ -> failtest "Prepare must find the accepted receipt."

    match core.Execute(input, CancellationToken.None) |> await with
    | SubmissionOutcome.ObservedAccepted receipt ->
        Expect.equal receipt.Snapshot.Version 1L "Execute replays the first revision"
    | _ -> failtest "Execute must find the accepted receipt."

let private withPrunedAcceptedCase action =
    use runtime = openRuntime ()
    let input = request (Guid.NewGuid()) ("PRUNED-" + Guid.NewGuid().ToString("N"))

    let digest =
        match runtime.Core.Execute(input, CancellationToken.None) |> await with
        | SubmissionOutcome.Completed(summary, _, DefiniteExecution.Accepted _, _) ->
            summary.RequestSha256
            |> Option.defaultWith (fun () -> failtest "Synthetic digest is required.")
        | _ -> failtest "The synthetic operation must accept."

    Expect.equal (rowCount "request_preparations" input.OperationId) 1L "Preparation exists"
    ageAccepted input.OperationId

    PreparationPruning.prune
        (adminConnection ())
        { PreparationPruneOptions.defaults with
            SettledRetentionDays = 1
        }
    |> completedAdministration
    |> ignore

    Expect.equal (rowCount "request_preparations" input.OperationId) 0L "Preparation pruned"
    Expect.equal (rowCount "case_changes" input.OperationId) 1L "Accepted history retained"
    action runtime.Core input digest

let private pruneAcceptedPreparation =
    testCase
        "[CC-APP-002] PostgreSQL accepted replay survives owner pruning of preparation"
        (fun () ->
            withPrunedAcceptedCase (fun core input digest ->
                expectAccepted core input

                match
                    core.Recovery.Resolve(input.OperationId, digest, CancellationToken.None)
                    |> await
                with
                | ResolveOutcome.ResolveObservedAccepted receipt ->
                    Expect.equal
                        receipt.Snapshot.Version
                        1L
                        "Service submit observes accepted history"
                | _ -> failtest "Resolve must observe acceptance after preparation pruning."))

let private concurrentPrunedReplayAndConflict =
    testCase
        "[CC-APP-002] PostgreSQL concurrent pruned replay and conflicts keep one accepted revision"
        (fun () ->
            withPrunedAcceptedCase (fun core input digest ->
                let concurrent =
                    [| for _ in 1..8 -> core.Execute(input, CancellationToken.None) |]
                    |> Task.WhenAll
                    |> await

                for outcome in concurrent do
                    match outcome with
                    | SubmissionOutcome.ObservedAccepted receipt ->
                        Expect.equal receipt.Snapshot.Version 1L "Concurrent exact replay"
                    | _ ->
                        failtest "Every concurrent exact retry must observe the accepted receipt."

                let conflict =
                    { input with
                        CaseReference = "DIFFERENT-CASE"
                    }

                match core.Execute(conflict, CancellationToken.None) |> await with
                | SubmissionOutcome.RejectedBeforeAttempt(None, rejection) ->
                    Expect.equal
                        rejection.Code
                        RejectionCode.IdempotencyConflict
                        "No receipt disclosure"
                | _ -> failtest "A conflicting request must not replay the accepted receipt."

                let wrongDigest = (if digest[0] = 'a' then "b" else "a") + digest.Substring(1)

                match
                    core.Recovery.Resolve(input.OperationId, wrongDigest, CancellationToken.None)
                    |> await
                with
                | ResolveOutcome.RefusedBeforeAttempt(None, rejection) ->
                    Expect.equal
                        rejection.Code
                        RecoveryRejectionCode.RecoveryIdempotencyConflict
                        "Wrong digest has no receipt disclosure"
                | _ -> failtest "Wrong digest must not disclose an accepted receipt."

                Expect.equal (rowCount "case_changes" input.OperationId) 1L "No second acceptance"))

let private acceptedWithoutTechnicalPreparation =
    testCase
        "[CC-APP-002] PostgreSQL accepted history without preparation remains replayable"
        (fun () ->
            let input = request (Guid.NewGuid()) ("HISTORY-" + Guid.NewGuid().ToString("N"))

            use claims = store ()

            match Service.executeAsync claims clock input |> await with
            | Ok _ -> ()
            | Error _ -> failtest "Synthetic direct history write must accept."

            Expect.equal (rowCount "request_preparations" input.OperationId) 0L "No preparation"
            use runtime = openRuntime ()
            expectAccepted runtime.Core input
            Expect.equal (rowCount "case_changes" input.OperationId) 1L "Accepted history retained")

let private conflictPrecedesSnapshotProjection =
    testCase
        "[CC-APP-002] PostgreSQL rejects wrong digest before parsing accepted snapshot"
        (fun () ->
            let input = request (Guid.NewGuid()) ("CORRUPT-" + Guid.NewGuid().ToString("N"))

            use claims = store ()

            match Service.executeAsync claims clock input |> await with
            | Ok _ -> ()
            | Error _ -> failtest "Synthetic operation must accept."

            let digest =
                input |> RequestRecord.encode |> SHA256.HashData |> Convert.ToHexStringLower

            let wrongDigest = (if digest[0] = 'a' then "b" else "a") + digest.Substring(1)

            let original = snapshot input.OperationId

            try
                replaceSnapshot input.OperationId "{}"

                match
                    (claims :> IClaimStore).Accepted(input.OperationId, wrongDigest) |> await
                with
                | Error CoreFailure.IdempotencyConflict -> ()
                | _ -> failtest "Wrong digest must be refused without snapshot decoding."

                match (claims :> IClaimStore).Accepted(input.OperationId, digest) |> await with
                | Error CoreFailure.StoreCorrupt -> ()
                | _ -> failtest "Exact digest must still fail closed on corrupt snapshot."
            finally
                replaceSnapshot input.OperationId original)

let tests =
    testList
        "PostgreSQL accepted receipt identity"
        [
            pruneAcceptedPreparation
            concurrentPrunedReplayAndConflict
            acceptedWithoutTechnicalPreparation
            conflictPrecedesSnapshotProjection
        ]
