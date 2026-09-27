namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Application

type internal ManagedCopyEventEvidence =
    {
        CopyId: Guid
        Canonical: byte array
        Signature: byte array
        CandidateSha256: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessEntryHash: byte array
    }

[<NoEquality; NoComparison>]
type internal ManagedCopyCurrent =
    {
        State: string
        Revision: int64
        EventHash: byte array
        RetainUntil: DateTimeOffset
        SourceCaseId: Guid option
        VerificationProofSha256: byte array option
        LastVerifiedAt: DateTimeOffset option
        Registration: byte array
    }

/// Owner-only reads of immutable copy evidence and the independently registered signer roster.
module internal ManagedCopyOwnerRead =
    let current (connection: NpgsqlConnection) transaction copyId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT c.state,c.revision,c.event_hash,c.retain_until,c.source_case_id,"
                    + "c.verification_proof_sha256,c.last_verified_at,e.canonical_attestation "
                    + "FROM claimcore.managed_copies c JOIN claimcore.managed_copy_events e "
                    + "ON e.copy_id=c.copy_id AND e.revision=1 "
                    + "WHERE c.copy_id=@copy AND c.producer_kind='OWNER_ATTESTED' "
                    + "FOR UPDATE OF c",
                    connection,
                    transaction
                )

            Sql.uuid command "copy" copyId
            use! reader = command.ExecuteReaderAsync()

            return
                if reader.Read() then
                    Some
                        {
                            State = reader.GetString(0)
                            Revision = reader.GetInt64(1)
                            EventHash = reader.GetFieldValue<byte array>(2)
                            RetainUntil = reader.GetFieldValue<DateTimeOffset>(3)
                            SourceCaseId =
                                if reader.IsDBNull(4) then None else Some(reader.GetGuid(4))
                            VerificationProofSha256 =
                                if reader.IsDBNull(5) then
                                    None
                                else
                                    Some(reader.GetFieldValue<byte array>(5))
                            LastVerifiedAt =
                                if reader.IsDBNull(6) then
                                    None
                                else
                                    Some(reader.GetFieldValue<DateTimeOffset>(6))
                            Registration = reader.GetFieldValue<byte array>(7)
                        }
                else
                    None
        }

    let signer (connection: NpgsqlConnection) transaction keyId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT ed25519_public_key,public_key_sha256,active,signer_purpose "
                    + "FROM claimcore.managed_copy_signers WHERE signing_key_id=@key",
                    connection,
                    transaction
                )

            Sql.uuid command "key" keyId
            let! rows = command.ExecuteReaderAsync()
            use reader = rows
            let! found = reader.ReadAsync()

            return
                if found then
                    Some(
                        reader.GetFieldValue<byte array>(0),
                        reader.GetFieldValue<byte array>(1),
                        reader.GetBoolean(2),
                        ManagedCopySignerCandidate.purposeOfName (reader.GetString(3))
                    )
                else
                    None
        }

    let existingEvent (connection: NpgsqlConnection) transaction eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT copy_id,canonical_attestation,ed25519_signature,candidate_sha256,"
                    + "witness_sequence,witness_epoch,witness_entry_hash "
                    + "FROM claimcore.managed_copy_events WHERE event_id=@event "
                    + "AND producer_kind='OWNER_ATTESTED' AND revision=1",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            let! rows = command.ExecuteReaderAsync()
            use reader = rows
            let! found = reader.ReadAsync()

            return
                if found then
                    Some
                        {
                            CopyId = reader.GetGuid(0)
                            Canonical = reader.GetFieldValue<byte array>(1)
                            Signature = reader.GetFieldValue<byte array>(2)
                            CandidateSha256 = reader.GetFieldValue<byte array>(3)
                            WitnessSequence = reader.GetInt64(4)
                            WitnessEpoch = reader.GetInt64(5)
                            WitnessEntryHash = reader.GetFieldValue<byte array>(6)
                        }
                else
                    None
        }

    let transitionEvent (connection: NpgsqlConnection) transaction eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT copy_id,canonical_attestation,ed25519_signature,candidate_sha256,"
                    + "witness_sequence,witness_epoch,witness_entry_hash "
                    + "FROM claimcore.managed_copy_events WHERE event_id=@event "
                    + "AND producer_kind='OWNER_ATTESTED' AND revision>1",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            use! reader = command.ExecuteReaderAsync()

            return
                if reader.Read() then
                    Some
                        {
                            CopyId = reader.GetGuid(0)
                            Canonical = reader.GetFieldValue<byte array>(1)
                            Signature = reader.GetFieldValue<byte array>(2)
                            CandidateSha256 = reader.GetFieldValue<byte array>(3)
                            WitnessSequence = reader.GetInt64(4)
                            WitnessEpoch = reader.GetInt64(5)
                            WitnessEntryHash = reader.GetFieldValue<byte array>(6)
                        }
                else
                    None
        }

    let copyExists (connection: NpgsqlConnection) transaction copyId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS(SELECT 1 FROM claimcore.managed_copies WHERE copy_id=@copy)",
                    connection,
                    transaction
                )

            Sql.uuid command "copy" copyId
            let! found = command.ExecuteScalarAsync()
            return found :?> bool
        }
