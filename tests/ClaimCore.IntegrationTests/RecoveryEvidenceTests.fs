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
    let command =
        {
            OperationId = operationId
            CaseReference = "EVIDENCE-" + operationId.ToString("N")
            ExpectedVersion = 0L
            Kind = CommandKind.Open
            Values =
                [
                    "incidentDate", registration.IncidentDate
                    "incidentNotificationDate", registration.IncidentNotificationDate
                    "incidentCountry", registration.IncidentCountry
                    "claimantName", registration.ClaimantName
                    "insurerName", registration.InsurerName
                    "claimedAmount", registration.ClaimedAmount
                    "claimedCurrency", registration.ClaimedCurrency
                ]
        }

    match core.Prepare(command, CancellationToken.None) |> await with
    | PrepareOutcome.Prepared(details, _) ->
        details.Summary.RequestSha256
        |> Option.defaultWith (fun () -> failtest "Retained digest is required.")
    | _ -> failtest "Synthetic preparation must be retained."

let private inspect (core: IClaimsCore) operationId =
    match core.Recovery.Inspect(operationId, CancellationToken.None) |> await with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found details) -> details
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
        let before = inspect runtime.Core operationId
        Expect.isEmpty before.Preparation.Attempts "No attempt before submission"
        Expect.isFalse before.Preparation.LegacyUncertainty "New preparation is not legacy"
        resolve runtime.Core operationId digest
        let after = inspect runtime.Core operationId

        match after.Preparation.Attempts with
        | [ attempt ] ->
            Expect.notEqual attempt.AttemptId Guid.Empty "Durable attempt ID"
            Expect.equal attempt.Settlement (Some "ACCEPTED") "Accepted technical settlement"
            Expect.isSome attempt.SettledAt "Settlement timestamp"
        | _ -> failtest "Exactly one accepted attempt must be visible."

        match after.Observation with
        | Lookup.Found receipt -> Expect.equal receipt.OperationId operationId "Receipt separate"
        | Lookup.NotFound _ -> failtest "Accepted operation must remain observable.")

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

        let before = inspect runtime.Core operationId

        match before.Preparation.Attempts with
        | [ attempt ] ->
            Expect.equal attempt.AttemptId firstId "Unsettled attempt identity"
            Expect.isNone attempt.Settlement "No invented settlement"
            Expect.isNone attempt.SettledAt "No invented settlement time"
        | _ -> failtest "Unsettled attempt must appear in recovery details."

        resolve runtime.Core operationId digest
        let after = inspect runtime.Core operationId
        Expect.equal after.Preparation.Attempts.Length 2 "Distinct exact retry attempt"

        let earlier =
            after.Preparation.Attempts |> List.find (fun item -> item.AttemptId = firstId)

        Expect.isNone earlier.Settlement "Earlier uncertainty remains unresolved"

        Expect.equal
            (after.Preparation.Attempts |> List.filter (fun item -> item.Settlement.IsSome)).Length
            1
            "Only definite later attempt is settled")

let private legacyMarker =
    testCase
        "[CC-REC-001] inspect reads pre-003 uncertainty marker independently of provenance"
        (fun () ->
            use runtime = openRuntime ()
            let operationId = Guid.NewGuid()
            prepare runtime.Core operationId |> ignore

            use connection = new NpgsqlConnection(adminConnection ())
            connection.Open()

            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.request_preparation_lifecycle (operation_id, state) VALUES (@operation, 'SUBMISSION_STARTED'); "
                    + "INSERT INTO claimcore.request_submission_legacy_uncertainty (operation_id) VALUES (@operation)",
                    connection
                )

            Sql.uuid command "operation" operationId

            Expect.equal
                (command.ExecuteNonQuery())
                2
                "Synthetic pre-003 start and marker inserted"

            let details = inspect runtime.Core operationId

            Expect.equal
                details.Preparation.PreparingContractKind
                "SEMANTIC_CORE_V1"
                "Producer provenance remains independent"

            Expect.isTrue
                details.Preparation.LegacyUncertainty
                "Legacy uncertainty marker surfaced"

            Expect.equal
                details.Preparation.Summary.State
                PreparationState.SubmissionStarted
                "Inherited start remains visible"

            Expect.isEmpty details.Preparation.Attempts "Pre-003 start has no identified attempt")

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
                PreparingContractKind = PreparingContractKind.LegacyUnclassified
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
        [ settledAttempt; unresolvedAttempt; legacyMarker; provenanceReplay ]
