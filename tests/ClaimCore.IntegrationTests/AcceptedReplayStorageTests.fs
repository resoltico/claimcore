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
    witnessedOpen (appConnection ()) CancellationToken.None
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
            "SELECT snapshot FROM claimcore.case_changes WHERE operation_id = @operation",
            connection
        )

    Sql.uuid command "operation" operationId

    match command.ExecuteScalar() with
    | :? (byte array) as value -> value
    | _ -> failtest "Synthetic receipt snapshot must exist."

let private acceptedEvidence operationId =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT canonical_request, effective_business_date - DATE '2000-01-01', observed_utc_instant FROM claimcore.case_changes WHERE operation_id = @operation",
            connection
        )

    Sql.uuid command "operation" operationId
    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        failtest "Synthetic accepted evidence must exist."

    reader.GetFieldValue<byte array>(0), reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2)

let private replaceSnapshot operationId (value: byte array) =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.case_changes SET snapshot = @snapshot WHERE operation_id = @operation",
            connection
        )

    Sql.uuid command "operation" operationId
    Sql.add command "snapshot" NpgsqlTypes.NpgsqlDbType.Bytea (box value)
    Expect.equal (command.ExecuteNonQuery()) 1 "Only this synthetic snapshot is changed"

let private expectAccepted (core: IActorClaimsCore) input =
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
        match (actorCore runtime).Execute(input, CancellationToken.None) |> await with
        | SubmissionOutcome.Completed(summary, _, DefiniteExecution.Accepted _, _) ->
            summary.RequestSha256
            |> Option.defaultWith (fun () -> failtest "Synthetic digest is required.")
        | SubmissionOutcome.RejectedBeforeAttempt _ ->
            failtest "The synthetic operation was rejected before attempt."
        | SubmissionOutcome.Completed(_, _, DefiniteExecution.FailedBeforeCommit _, _) ->
            failtest "The synthetic operation failed before commit."
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
    action (actorCore runtime) input digest

let private pruneAcceptedPreparation =
    testCase
        "[CC-APP-002] PostgreSQL accepted replay survives owner pruning of preparation"
        (fun () ->
            withPrunedAcceptedCase (fun core input digest ->
                let bytes, businessDays, observedAt = acceptedEvidence input.OperationId

                Expect.sequenceEqual
                    bytes
                    (RequestRecord.encode input)
                    "Accepted history retains exact canonical request bytes"

                Expect.equal
                    businessDays
                    (DateOnly.FromDateTime(observedAt.UtcDateTime).DayNumber
                     - DateOnly(2000, 1, 1).DayNumber)
                    "Accepted event retains execution business date"

                Expect.equal observedAt.Offset TimeSpan.Zero "Accepted instant is UTC"

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
            let bytes, businessDays, observedAt = acceptedEvidence input.OperationId

            Expect.sequenceEqual
                bytes
                (RequestRecord.encode input)
                "Direct commit retains request bytes"

            Expect.equal
                businessDays
                (DateOnly(2026, 9, 7).DayNumber - DateOnly(2000, 1, 1).DayNumber)
                "Direct commit retains business date"

            Expect.equal
                observedAt
                (DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero))
                "Direct commit retains capture instant"

            use runtime = openRuntime ()
            expectAccepted (actorCore runtime) input
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
                replaceSnapshot input.OperationId [| 123uy; 125uy |]

                match
                    (claims :> IClaimStore).Accepted(input.OperationId, wrongDigest) |> await
                with
                | Error CoreFailure.IdempotencyConflict -> ()
                | _ -> failtest "Wrong digest must be refused without snapshot decoding."

                match (claims :> IClaimStore).Accepted(input.OperationId, digest) |> await with
                | Error CoreFailure.StoreCorrupt -> ()
                | _ -> failtest "Exact digest must still fail closed on corrupt snapshot."

                replaceSnapshot input.OperationId (Array.append original [| 32uy |])

                match (claims :> IClaimStore).Accepted(input.OperationId, digest) |> await with
                | Error CoreFailure.StoreCorrupt -> ()
                | _ -> failtest "Semantically valid noncanonical snapshot bytes must fail closed."
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
