namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Witness
open WitnessProtocolReconciliation

type private LatestStartEvidence =
    {
        AttemptId: Guid
        Ordinal: int64
        ActorId: Guid
        Role: string
        GrantRevision: int64
        Sequence: int64
        Epoch: int64
        Hash: byte array
        ExpectedDigest: byte array
    }

module internal WitnessTechnicalStartReconcile =
    let private readLatest connection transaction operationId =
        use command =
            new NpgsqlCommand(
                "SELECT attempt_id,attempt_ordinal,submitter_actor_id,resolver_actor_id,"
                + "grant_revision,witness_event_id,witness_sequence,witness_epoch,"
                + "witness_entry_hash,witness_candidate_sha256 "
                + "FROM claimcore.request_submission_attempts WHERE operation_id=@operation "
                + "ORDER BY attempt_ordinal DESC LIMIT 1",
                connection,
                transaction
            )

        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId)
        |> ignore

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            None
        else
            let attemptId = reader.GetGuid(0)
            let ordinal = reader.GetInt64(1)

            let actorId, role =
                if reader.IsDBNull(2) then
                    reader.GetGuid(3), "RESOLVER"
                else
                    reader.GetGuid(2), "SUBMITTER"

            let eventId = reader.GetGuid(5)

            let value =
                {
                    AttemptId = attemptId
                    Ordinal = ordinal
                    ActorId = actorId
                    Role = role
                    GrantRevision = reader.GetInt64(4)
                    Sequence = reader.GetInt64(6)
                    Epoch = reader.GetInt64(7)
                    Hash = reader.GetFieldValue<byte array>(8)
                    ExpectedDigest = reader.GetFieldValue<byte array>(9)
                }

            if reader.Read() || eventId <> attemptId then
                raise WitnessPending

            Some value

    let reconcileLatestStart
        (witness: WitnessProtocol)
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (header: RetainedPreparation)
        =
        match readLatest connection transaction header.OperationId with
        | None -> None
        | Some value ->
            if
                value.AttemptId
                <> WitnessEventIdentity.startEventId header.OperationId value.Ordinal
            then
                raise WitnessPending

            let settlementWasMissing =
                witness.EvidenceStore.TryReadEvidence(value.AttemptId, SettledAuthority).IsNone

            let bytes =
                WitnessTechnical.startCandidate
                    header.OperationId
                    value.AttemptId
                    value.Ordinal
                    header.CaseId
                    value.ActorId
                    value.Role
                    value.GrantRevision
                    header.RequestSha256

            try
                if SHA256.HashData(bytes) <> value.ExpectedDigest then
                    raise WitnessPending

                witness.ReconcileAuthority(
                    value.AttemptId,
                    value.Sequence,
                    value.Epoch,
                    value.Hash,
                    bytes
                )
            finally
                CryptographicOperations.ZeroMemory(bytes)

            if settlementWasMissing then
                Some(value.AttemptId, value.ActorId, value.Role)
            else
                None
