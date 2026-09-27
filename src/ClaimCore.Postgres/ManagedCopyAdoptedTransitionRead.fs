namespace ClaimCore.Postgres

open System
open Npgsql

[<NoEquality; NoComparison>]
type internal AdoptedCopyCurrent =
    {
        State: string
        Revision: int64
        EventHash: byte array
        RetainUntil: DateTimeOffset
        VerificationProofSha256: byte array option
        LastVerifiedAt: DateTimeOffset option
    }

[<NoEquality; NoComparison>]
type internal AdoptedCopyStoredEvent =
    {
        CopyId: Guid
        AdoptionEventId: Guid
        Revision: int64
        EventKind: string
        ProducerKind: string
        SigningKeyId: Guid
        CustodianKeyId: Guid
        Canonical: byte array
        Signature: byte array
        CandidateSha256: byte array
        PreviousHash: byte array
        EventHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessEntryHash: byte array
    }

/// Locked current state and exact prior event for adopted-copy owner transitions.
module internal ManagedCopyAdoptedTransitionRead =
    let current connection transaction (value: AdoptedCopyTransition) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT state,revision,event_hash,retain_until,verification_proof_sha256,"
                    + "last_verified_at FROM claimcore.managed_copies WHERE copy_id=@copy "
                    + "AND source_case_id=@case AND producer_kind=@producer FOR UPDATE",
                    connection,
                    transaction
                )

            Sql.uuid command "copy" value.CopyId
            Sql.uuid command "case" value.SourceCaseId
            Sql.text command "producer" value.ProducerKind
            use! reader = command.ExecuteReaderAsync()

            return
                if reader.Read() then
                    Some
                        {
                            State = reader.GetString(0)
                            Revision = reader.GetInt64(1)
                            EventHash = reader.GetFieldValue<byte array>(2)
                            RetainUntil = reader.GetFieldValue<DateTimeOffset>(3)
                            VerificationProofSha256 =
                                if reader.IsDBNull(4) then
                                    None
                                else
                                    Some(reader.GetFieldValue<byte array>(4))
                            LastVerifiedAt =
                                if reader.IsDBNull(5) then
                                    None
                                else
                                    Some(reader.GetFieldValue<DateTimeOffset>(5))
                        }
                else
                    None
        }

    let existing connection transaction eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT e.copy_id,a.adoption_event_id,e.revision,e.event_kind,e.producer_kind,e.signing_key_id,"
                    + "a.custodian_signing_key_id,e.canonical_attestation,e.ed25519_signature,"
                    + "e.candidate_sha256,e.previous_hash,e.event_hash,e.witness_sequence,"
                    + "e.witness_epoch,e.witness_entry_hash "
                    + "FROM claimcore.managed_copy_events e JOIN claimcore.managed_copy_adoptions a "
                    + "ON a.copy_id=e.copy_id WHERE e.event_id=@event "
                    + "AND e.producer_kind IN ('PRODUCT_EXPORT','ADOPTED_EXTERNAL') AND e.revision>1",
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
                            AdoptionEventId = reader.GetGuid(1)
                            Revision = reader.GetInt64(2)
                            EventKind = reader.GetString(3)
                            ProducerKind = reader.GetString(4)
                            SigningKeyId = reader.GetGuid(5)
                            CustodianKeyId = reader.GetGuid(6)
                            Canonical = reader.GetFieldValue<byte array>(7)
                            Signature = reader.GetFieldValue<byte array>(8)
                            CandidateSha256 = reader.GetFieldValue<byte array>(9)
                            PreviousHash = reader.GetFieldValue<byte array>(10)
                            EventHash = reader.GetFieldValue<byte array>(11)
                            WitnessSequence = reader.GetInt64(12)
                            WitnessEpoch = reader.GetInt64(13)
                            WitnessEntryHash = reader.GetFieldValue<byte array>(14)
                        }
                else
                    None
        }
