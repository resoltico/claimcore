namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open DataAuditCommon

/// The producer's immutable original event is retained. ADOPT is a distinct signed revision,
/// never a synthetic owner-attributed REGISTER or rewritten export provenance.
module internal ManagedCopyAdoptionEventEvidence =
    let private eventSql =
        "SELECT copy_id,revision,event_kind,producer_kind,canonical_attestation,"
        + "signing_key_id,ed25519_signature,candidate_sha256,previous_hash,event_hash,"
        + "witness_sequence,witness_epoch,witness_entry_hash "
        + "FROM claimcore.managed_copy_events WHERE event_id=@event"

    let private eventIdentity (reader: Data.Common.DbDataReader) (receipt: CopyAdoptionReceipt) =
        let c = receipt.Core
        let s = receipt.Signatures

        reader.GetGuid(0) = c.CopyId
        && reader.GetInt64(1) = c.CopyRevision
        && reader.GetString(2) =
            (if c.OriginKind = "PRODUCT_EXPORT" then
                 "ADOPT"
             else
                 "REGISTER")
        && reader.GetString(3) = c.OriginKind
        && reader.GetFieldValue<byte array>(4) = s.CustodianCanonical
        && reader.GetGuid(5) = c.CustodianKeyId
        && reader.GetFieldValue<byte array>(6) = s.CustodianSignature

    let private eventWitness (reader: Data.Common.DbDataReader) (receipt: CopyAdoptionReceipt) =
        let w = receipt.Witness

        reader.GetFieldValue<byte array>(7) = w.CandidateHash
        && reader.GetFieldValue<byte array>(8) = w.PreviousCopyHash
        && reader.GetFieldValue<byte array>(9) = w.CopyEventHash
        && reader.GetInt64(10) = w.Sequence
        && reader.GetInt64(11) = w.Epoch
        && reader.GetFieldValue<byte array>(12) = w.EntryHash

    let private eventRow connection transaction (receipt: CopyAdoptionReceipt) =
        task {
            use command = new NpgsqlCommand(eventSql, connection, transaction)
            Sql.uuid command "event" receipt.Core.EventId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                corrupt ()

            if
                not (eventIdentity reader receipt && eventWitness reader receipt)
                || reader.Read()
            then
                corrupt ()
        }

    let private originalSql =
        "SELECT c.producer_kind,c.source_case_id,c.ciphertext_sha256,c.ciphertext_bytes,"
        + "c.encryption_key_id,c.revision,c.state,c.event_hash,c.captured_at,c.retain_until,"
        + "e.witness_sequence,e.witness_entry_hash,e.artifact_sha256,"
        + "first.event_hash,first.revision,first.event_kind,first.producer_kind "
        + "FROM claimcore.managed_copies c "
        + "LEFT JOIN claimcore.recovery_artifact_exports e ON e.export_id=c.product_export_id "
        + "LEFT JOIN claimcore.managed_copy_events first "
        + "ON first.copy_id=c.copy_id AND first.revision=1 "
        + "WHERE c.copy_id=@copy"

    let private projection
        connection
        transaction
        (receipt: CopyAdoptionReceipt)
        (custody: CopyAdoptionCustody)
        =
        task {
            use command = new NpgsqlCommand(originalSql, connection, transaction)
            Sql.uuid command "copy" receipt.Core.CopyId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                corrupt ()

            let c = receipt.Core
            let w = receipt.Witness
            let product = c.OriginKind = "PRODUCT_EXPORT"
            let revision = reader.GetInt64(5)

            let current =
                revision <> c.CopyRevision
                || reader.GetString(6) <> (if product then "UNVERIFIED" else "UNKNOWN")
                || reader.GetFieldValue<byte array>(7) <> w.CopyEventHash

            let common =
                reader.GetString(0) <> c.OriginKind
                || reader.GetGuid(1) <> c.CaseId
                || reader.GetFieldValue<byte array>(2) <> c.CiphertextSha256
                || reader.GetInt64(3) <> c.CiphertextBytes
                || reader.GetGuid(4) <> custody.EncryptionKeyId
                || revision < c.CopyRevision
                || reader.GetFieldValue<DateTimeOffset>(8) <> c.CapturedAt
                || reader.GetFieldValue<DateTimeOffset>(9) <> c.RetainUntil

            let origin =
                if product then
                    reader.IsDBNull(10)
                    || reader.GetInt64(10) <> c.PreFenceSequence
                    || reader.GetFieldValue<byte array>(11) <> c.PreFenceHash
                    || reader.GetFieldValue<byte array>(12) <> c.CiphertextSha256
                    || reader.GetFieldValue<byte array>(13) <> w.PreviousCopyHash
                    || reader.GetInt64(14) <> 1L
                    || reader.GetString(15) <> "REGISTER"
                    || reader.GetString(16) <> "PRODUCT_EXPORT"
                else
                    not (reader.IsDBNull(10))
                    || w.PreviousCopyHash <> Array.zeroCreate<byte> 32
                    || reader.GetInt64(14) <> 1L
                    || reader.GetString(15) <> "REGISTER"
                    || reader.GetString(16) <> "ADOPTED_EXTERNAL"

            if common || origin || (revision = c.CopyRevision && current) || reader.Read() then
                corrupt ()
        }

    let verify connection transaction receipt custody =
        task {
            let w = receipt.Witness

            if
                w.CopyEventHash
                <> ManagedCopyEventHash.compute
                    w.PreviousCopyHash
                    receipt.Signatures.CustodianCanonical
                    (Some receipt.Signatures.CustodianSignature)
                || w.CandidateHash <> SHA256.HashData(w.Canonical)
            then
                corrupt ()

            do! eventRow connection transaction receipt
            do! projection connection transaction receipt custody
        }
