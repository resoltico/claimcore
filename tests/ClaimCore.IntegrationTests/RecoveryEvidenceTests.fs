module ClaimCore.IntegrationTests.RecoveryEvidenceTests

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.IntegrationTests.Fixtures

let private openRuntime () =
    Runtime.OpenPostgres(appConnection (), CancellationToken.None)
    |> await
    |> Result.defaultWith (fun _ -> failtest "Synthetic recovery runtime must open.")

let private prepare (core: IClaimsCore) operationId =
    let command = openRequest operationId ("EVIDENCE-" + operationId.ToString("N"))

    match core.Prepare(command, CancellationToken.None) |> await with
    | PrepareOutcome.Prepared(details, _) ->
        details.Summary.RequestSha256
        |> Option.defaultWith (fun () -> failtest "Retained digest is required.")
    | _ -> failtest "Synthetic preparation must be retained."

let private inspect (core: IClaimsCore) operationId afterCursor limit =
    match
        core.Recovery.Inspect(operationId, afterCursor, limit, CancellationToken.None)
        |> await
    with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RetainedInspection details)) ->
        details
    | _ -> failtest "Retained recovery evidence must be inspectable."

let private resolve (core: IClaimsCore) operationId digest =
    match core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> await with
    | ResolveOutcome.ResolveCompleted(_, _, DefiniteExecution.Accepted _, _) -> ()
    | _ -> failtest "Synthetic exact resolution must be accepted."

let private settledAttempt =
    testCase "[CC-REC-001] inspect projects the accepted attempt and settlement" (fun () ->
        use runtime = openRuntime ()
        let operationId = Guid.NewGuid()
        let digest = prepare runtime.Core operationId
        let before = inspect runtime.Core operationId None recoveryPageLimit
        Expect.isEmpty before.Preparation.Attempts.Items "No attempt before submission"


        resolve runtime.Core operationId digest
        let after = inspect runtime.Core operationId None recoveryPageLimit

        match after.Preparation.Attempts.Items with
        | [ attempt ] ->
            Expect.notEqual attempt.AttemptId Guid.Empty "Durable attempt ID"
            Expect.equal attempt.Settlement (Some "ACCEPTED") "Accepted technical settlement"
            Expect.isSome attempt.SettledAt "Settlement timestamp"
        | _ -> failtest "Exactly one accepted attempt must be visible."

        match after.Observation with
        | Lookup.Found receipt -> Expect.equal receipt.OperationId operationId "Receipt separate"
        | Lookup.NotFound _ -> failtest "Accepted operation must remain observable.")

let private preserveUnsettledOnPrune operationId (core: IClaimsCore) earlierId =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use age =
        new NpgsqlCommand(
            "UPDATE claimcore.case_changes SET recorded_at=clock_timestamp()-interval '3 days' WHERE operation_id=@operation",
            connection
        )

    Sql.uuid age "operation" operationId
    Expect.equal (age.ExecuteNonQuery()) 1 "Only the synthetic accepted receipt is aged"

    PreparationPruning.prune
        (adminConnection ())
        { PreparationPruneOptions.defaults with
            SettledRetentionDays = 1
        }
    |> completedAdministration
    |> ignore

    let retained = inspect core operationId None recoveryPageLimit

    let unresolved =
        retained.Preparation.Attempts.Items
        |> List.find (fun item -> item.AttemptId = earlierId)

    Expect.isNone
        unresolved.Settlement
        "Pruning cannot erase an earlier unsettled attempt after acceptance"

let private unresolvedAttempt =
    testCase "[CC-REC-001] inspect preserves an earlier unsettled attempt" (fun () ->
        use runtime = openRuntime ()
        let operationId = Guid.NewGuid()
        let digest = prepare runtime.Core operationId
        use source = NpgsqlDataSource.Create(appConnection ())

        let recovery =
            PostgresRecoveryStore(source, PreparationLimits.defaults) :> IRecoveryStore

        let firstId =
            match recovery.Start(operationId, CancellationToken.None) |> await with
            | Ok(RecoveryStart.Started(attemptId, _)) -> attemptId
            | _ -> failtest "Technical attempt must be durably admitted."

        let before = inspect runtime.Core operationId None recoveryPageLimit

        match before.Preparation.Attempts.Items with
        | [ attempt ] ->
            Expect.equal attempt.AttemptId firstId "Unsettled attempt identity"
            Expect.isNone attempt.Settlement "No invented settlement"
            Expect.isNone attempt.SettledAt "No invented settlement time"
        | _ -> failtest "Unsettled attempt must appear in recovery details."

        resolve runtime.Core operationId digest
        let after = inspect runtime.Core operationId None recoveryPageLimit
        Expect.equal after.Preparation.Attempts.Items.Length 2 "Distinct exact retry attempt"

        let earlier =
            after.Preparation.Attempts.Items
            |> List.find (fun item -> item.AttemptId = firstId)

        Expect.isNone earlier.Settlement "Earlier uncertainty remains unresolved"

        Expect.equal
            (after.Preparation.Attempts.Items
             |> List.filter (fun item -> item.Settlement.IsSome))
                .Length
            1
            "Only definite later attempt is settled"

        preserveUnsettledOnPrune operationId runtime.Core firstId)

let private currentCanonicalImport =
    testCase
        "[CC-REC-001] current canonical import retains exact bytes with explicit import provenance"
        (fun () ->
            use runtime = openRuntime ()
            let request = newRequest ()
            let bytes = RequestRecord.encode request
            let digest = bytes |> SHA256.HashData |> Convert.ToHexStringLower

            match
                runtime.Core.Recovery.PreviewCanonicalRecordImport(bytes, CancellationToken.None)
                |> await
            with
            | RecoveryQueryOutcome.RecoverySucceeded preview ->
                Expect.equal preview.SourceSha256 digest "Source review binds the exact file"
                Expect.equal preview.DecodedEffect.CanonicalCommandFormat 3 "Current record format"
            | _ -> failtest "A current canonical record must be previewable."

            match
                runtime.Core.Recovery.RetainCanonicalRecordImport(
                    bytes,
                    digest,
                    CancellationToken.None
                )
                |> await
            with
            | RecoveryImportRetainOutcome.RetainedPreparation details ->
                Expect.equal
                    details.PreparingContractKind
                    "CANONICAL_RECORD_V3"
                    "Not fictitious old producer provenance"

                Expect.equal
                    details.Summary.State
                    PreparationState.Unsubmitted
                    "Import never submits"
            | _ -> failtest "A reviewed current canonical record must be retainable."

            let before = inspect runtime.Core request.OperationId None recoveryPageLimit
            Expect.isEmpty before.Preparation.Attempts.Items "No hidden execution"
            resolve runtime.Core request.OperationId digest)

let private provenanceReplay =
    testCase "[CC-REC-001] exact replay preserves first producer provenance" (fun () ->
        use source = NpgsqlDataSource.Create(appConnection ())

        let recovery =
            PostgresRecoveryStore(source, PreparationLimits.defaults) :> IRecoveryStore

        let request = newRequest ()
        let canonical = RequestRecord.encode request

        let fingerprint =
            SemanticContract.fingerprint SemanticContract.current
            |> SemanticCoreFingerprint.value

        let original =
            {
                OperationId = request.OperationId
                CanonicalRequestFormat = RecordVersions.CanonicalCommandFormat
                RequestSha256 = canonical |> SHA256.HashData |> Convert.ToHexStringLower
                CanonicalRequest = canonical
                PreparingApplicationVersion = BuildIdentity.current.Version
                PreparingContractFingerprint = fingerprint
                PreparingContractKind = PreparingContractKind.SemanticCoreV1
            }

        let first =
            match recovery.Retain(original, CancellationToken.None) |> await with
            | Ok(RecoveryRetain.Created value) -> value
            | _ -> failtest "First producer must create the exact request."

        let changedProducer =
            { original with
                PreparingApplicationVersion = "999.0.0"
                PreparingContractFingerprint = String.replicate 64 "b"
                PreparingContractKind = PreparingContractKind.CanonicalRecordV3
            }

        let replay =
            match recovery.Retain(changedProducer, CancellationToken.None) |> await with
            | Ok(RecoveryRetain.Existing value) -> value
            | _ -> failtest "Exact bytes must replay as existing across producers."

        Expect.equal replay.PreparedAt first.PreparedAt "First technical timestamp preserved"
        Expect.equal replay.CanonicalRequest first.CanonicalRequest "Exact request bytes preserved"
        Expect.equal replay.PreparingApplicationVersion first.PreparingApplicationVersion "Version"
        Expect.equal replay.PreparingContractFingerprint fingerprint "First fingerprint preserved"

        Expect.equal
            replay.PreparingContractKind
            first.PreparingContractKind
            "First kind preserved")

let tests =
    testList
        "PostgreSQL recovery evidence"
        [ settledAttempt; unresolvedAttempt; currentCanonicalImport; provenanceReplay ]
