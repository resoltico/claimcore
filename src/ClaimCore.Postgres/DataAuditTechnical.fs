namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open DataAuditCommon

module internal DataAuditTechnical =
    let private verifyPreparationRow
        (witness: WitnessProtocol)
        cutoff
        (reader: NpgsqlDataReader)
        ct
        =
        task {
            let operationId = reader.GetGuid(0)
            let eventId = reader.GetGuid(1)
            let sequence = reader.GetInt64(2)
            let candidateDigest = reader.GetFieldValue<byte array>(5)

            if eventId <> WitnessEventIdentity.prepareEventId operationId || sequence > cutoff then
                corrupt ()

            let importer = if reader.IsDBNull(8) then None else Some(reader.GetGuid(8))

            let candidate =
                WitnessTechnical.prepareCandidate
                    operationId
                    (reader.GetGuid(6))
                    (reader.GetGuid(7))
                    importer
                    (reader.GetInt64(9))
                    (reader.GetString(10))
                    (reader.GetFieldValue<byte array>(11))
                    (reader.GetString(12))
                    (reader.GetString(13))

            try
                if SHA256.HashData(candidate) <> candidateDigest then
                    corrupt ()

                do!
                    witnessProofAsync (fun () ->
                        witness.VerifyAuthorityEvidenceForCase(
                            eventId,
                            sequence,
                            reader.GetInt64(3),
                            reader.GetFieldValue<byte array>(4),
                            candidateDigest,
                            reader.GetGuid(6),
                            ct
                        ))
            finally
                CryptographicOperations.ZeroMemory(candidate)
        }

    let verifyPreparations
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        witness
        cutoff
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT operation_id,witness_event_id,witness_sequence,witness_epoch,"
                    + "witness_entry_hash,witness_candidate_sha256,case_id,preparer_actor_id,"
                    + "importer_actor_id,preparer_grant_revision,request_sha256,canonical_request,"
                    + "prepared_application_version,preparing_contract_fingerprint "
                    + "FROM claimcore.request_preparations ORDER BY operation_id",
                    connection,
                    transaction
                )

            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(cancellationToken)
                reading <- found

                if found then
                    do! verifyPreparationRow witness cutoff reader cancellationToken
        }

    let private verifyAttemptRow (witness: WitnessProtocol) cutoff (reader: NpgsqlDataReader) ct =
        task {
            let operationId = reader.GetGuid(0)
            let attemptId = reader.GetGuid(1)
            let ordinal = reader.GetInt64(2)
            let eventId = reader.GetGuid(3)
            let sequence = reader.GetInt64(4)
            let submitter = if reader.IsDBNull(8) then None else Some(reader.GetGuid(8))
            let resolver = if reader.IsDBNull(9) then None else Some(reader.GetGuid(9))

            if
                eventId <> attemptId
                || eventId <> WitnessEventIdentity.startEventId operationId ordinal
                || sequence > cutoff
                || submitter.IsSome = resolver.IsSome
            then
                corrupt ()

            let actorId, role =
                match submitter, resolver with
                | Some value, None -> value, "SUBMITTER"
                | None, Some value -> value, "RESOLVER"
                | _ -> corrupt ()

            let candidate =
                WitnessTechnical.startCandidate
                    operationId
                    attemptId
                    ordinal
                    (reader.GetGuid(11))
                    actorId
                    role
                    (reader.GetInt64(10))
                    (reader.GetString(12))

            let candidateDigest = reader.GetFieldValue<byte array>(7)

            try
                if SHA256.HashData(candidate) <> candidateDigest then
                    corrupt ()

                do!
                    witnessProofAsync (fun () ->
                        witness.VerifyAuthorityEvidenceForCase(
                            eventId,
                            sequence,
                            reader.GetInt64(5),
                            reader.GetFieldValue<byte array>(6),
                            candidateDigest,
                            reader.GetGuid(11),
                            ct
                        ))
            finally
                CryptographicOperations.ZeroMemory(candidate)
        }

    let verifyAttempts
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        witness
        cutoff
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT a.operation_id,a.attempt_id,a.attempt_ordinal,a.witness_event_id,"
                    + "a.witness_sequence,a.witness_epoch,a.witness_entry_hash,"
                    + "a.witness_candidate_sha256,a.submitter_actor_id,a.resolver_actor_id,"
                    + "a.grant_revision,p.case_id,p.request_sha256 "
                    + "FROM claimcore.request_submission_attempts a "
                    + "JOIN claimcore.request_preparations p ON p.operation_id=a.operation_id "
                    + "ORDER BY a.operation_id,a.attempt_ordinal",
                    connection,
                    transaction
                )

            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let mutable reading = true
            let mutable previousOperation = Guid.Empty
            let mutable previousOrdinal = 0L

            while reading do
                let! found = reader.ReadAsync(cancellationToken)
                reading <- found

                if found then
                    let operation = reader.GetGuid(0)
                    let ordinal = reader.GetInt64(2)

                    if
                        ordinal
                        <> (if operation = previousOperation then
                                previousOrdinal + 1L
                            else
                                1L)
                    then
                        corrupt ()

                    do! verifyAttemptRow witness cutoff reader cancellationToken
                    previousOperation <- operation
                    previousOrdinal <- ordinal
        }
