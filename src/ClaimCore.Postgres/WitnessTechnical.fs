namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Witness
open WitnessProtocolReconciliation

type internal TechnicalCandidateMetadata =
    {
        Kind: string
        OperationId: Guid
        CaseId: Guid
        WitnessEventId: Guid
        RequestSha256: string
        AttemptOrdinal: int64 option
    }

/// Technical PREPARE and START use distinct deterministic witness identities. An orphan INTENT
/// therefore survives process loss and cannot be mistaken for the accepted command's INTENT.
module internal WitnessTechnical =
    let private encoded (write: Utf8JsonWriter -> unit) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        write writer
        writer.Flush()
        let bytes = stream.ToArray()
        CryptographicOperations.ZeroMemory(stream.GetBuffer().AsSpan(0, int stream.Length))
        bytes

    let prepareCandidate
        (operationId: Guid)
        (caseId: Guid)
        (preparer: Guid)
        (importer: Guid option)
        (grantRevision: int64)
        (requestSha: string)
        (canonicalRequest: byte array)
        (applicationVersion: string)
        (contractFingerprint: string)
        =
        encoded (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("version", 1)
            writer.WriteString("kind", "PREPARE")
            writer.WriteString("operationId", operationId)
            writer.WriteString("caseId", caseId)
            writer.WriteString("preparerActorId", preparer)

            match importer with
            | Some value -> writer.WriteString("importerActorId", value)
            | None -> writer.WriteNull("importerActorId")

            writer.WriteNumber("grantRevision", grantRevision)
            writer.WriteString("requestSha256", requestSha)
            writer.WriteBase64String("canonicalRequest", ReadOnlySpan<byte>(canonicalRequest))
            writer.WriteString("applicationVersion", applicationVersion)
            writer.WriteString("contractFingerprint", contractFingerprint)
            writer.WriteString("contractKind", "SEMANTIC_CORE_V1")
            writer.WriteEndObject())

    let startCandidate
        (operationId: Guid)
        (attemptId: Guid)
        (ordinal: int64)
        (caseId: Guid)
        (actorId: Guid)
        (role: string)
        (grantRevision: int64)
        (requestSha: string)
        =
        encoded (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("version", 1)
            writer.WriteString("kind", "START")
            writer.WriteString("operationId", operationId)
            writer.WriteString("attemptId", attemptId)
            writer.WriteNumber("attemptOrdinal", ordinal)
            writer.WriteString("caseId", caseId)
            writer.WriteString("actorId", actorId)
            writer.WriteString("actorRole", role)
            writer.WriteNumber("grantRevision", grantRevision)
            writer.WriteString("requestSha256", requestSha)
            writer.WriteEndObject())

    let beginPrepare (witness: WitnessProtocol) (draft: RecoveryPreparationDraft) =
        let eventId = WitnessEventIdentity.prepareEventId draft.OperationId

        let bytes =
            prepareCandidate
                draft.OperationId
                draft.CaseId
                draft.PreparerActorId
                draft.ImporterActorId
                draft.PreparerGrantRevision
                draft.RequestSha256
                draft.CanonicalRequest
                draft.PreparingApplicationVersion
                draft.PreparingContractFingerprint

        try
            eventId, witness.BeginAuthority(eventId, bytes, Some draft.CaseId)
        finally
            CryptographicOperations.ZeroMemory(bytes)

    let beginStart
        (witness: WitnessProtocol)
        (header: RetainedPreparation)
        ordinal
        actorId
        role
        grantRevision
        =
        let attemptId = WitnessEventIdentity.startEventId header.OperationId ordinal

        let bytes =
            startCandidate
                header.OperationId
                attemptId
                ordinal
                header.CaseId
                actorId
                role
                grantRevision
                header.RequestSha256

        try
            attemptId, witness.BeginAuthority(attemptId, bytes, Some header.CaseId)
        finally
            CryptographicOperations.ZeroMemory(bytes)

    let reconcilePrepare
        (witness: WitnessProtocol)
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (header: RetainedPreparation)
        =
        use command =
            new NpgsqlCommand(
                "SELECT witness_event_id,witness_sequence,witness_epoch,witness_entry_hash,"
                + "witness_candidate_sha256 FROM claimcore.request_preparations "
                + "WHERE operation_id=@operation",
                connection,
                transaction
            )

        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, header.OperationId)
        |> ignore

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            raise WitnessPending

        let eventId = reader.GetGuid(0)
        let sequence = reader.GetInt64(1)
        let epoch = reader.GetInt64(2)
        let hash = reader.GetFieldValue<byte array>(3)
        let candidateHash = reader.GetFieldValue<byte array>(4)

        if
            reader.Read()
            || eventId <> WitnessEventIdentity.prepareEventId header.OperationId
        then
            raise WitnessPending

        reader.Close()

        let bytes =
            prepareCandidate
                header.OperationId
                header.CaseId
                header.PreparerActorId
                header.ImporterActorId
                header.PreparerGrantRevision
                header.RequestSha256
                header.CanonicalRequest
                header.PreparingApplicationVersion
                header.PreparingContractFingerprint

        try
            if SHA256.HashData(bytes) <> candidateHash then
                raise WitnessPending

            witness.ReconcileAuthority(eventId, sequence, epoch, hash, bytes)
        finally
            CryptographicOperations.ZeroMemory(bytes)
