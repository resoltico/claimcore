namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Application

[<NoEquality; NoComparison>]
type internal CopyAdoptionOriginState =
    {
        PreviousEventHash: byte array
        EncryptionKeyId: Guid option
        CopyRevision: int64
        CopyState: string option
    }

[<NoEquality; NoComparison>]
type internal StoredCopyAdoptionEvent =
    {
        EventId: Guid
        CopyId: Guid
        CaseId: Guid
        ApprovalId: Guid
        Canonical: byte array
        CandidateHash: byte array
        CustodianCanonical: byte array
        CustodianSignature: byte array
        RegistryCanonical: byte array
        RegistrySignature: byte array
        InspectionCanonical: byte array
        InspectionSignature: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
    }

module internal ManagedCopyAdoptionOwnerRead =
    let private productSql =
        "SELECT c.event_hash,c.encryption_key_id,c.state,c.revision,c.source_case_id,"
        + "c.ciphertext_sha256,c.ciphertext_bytes,c.captured_at,c.retain_until,"
        + "e.witness_sequence,e.witness_entry_hash,e.artifact_sha256 "
        + "FROM claimcore.managed_copies c "
        + "JOIN claimcore.recovery_artifact_exports e ON e.export_id=c.product_export_id "
        + "WHERE c.copy_id=@copy AND c.producer_kind='PRODUCT_EXPORT' FOR UPDATE OF c"

    let private product
        connection
        transaction
        (request: CopyAdoptionApprovalRequest)
        sequence
        hash
        =
        task {
            use command = new NpgsqlCommand(productSql, connection, transaction)
            Sql.uuid command "copy" request.CopyId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                return None
            else
                let previous = reader.GetFieldValue<byte array>(0)
                let key = reader.GetGuid(1)
                let state = reader.GetString(2)
                let revision = reader.GetInt64(3)

                let valid =
                    state = "UNKNOWN"
                    && revision = 1L
                    && reader.GetGuid(4) = request.CaseId
                    && reader.GetFieldValue<byte array>(5) = request.CiphertextSha256
                    && reader.GetInt64(6) = request.CiphertextBytes
                    && reader.GetFieldValue<DateTimeOffset>(7) = request.CapturedAt
                    && reader.GetFieldValue<DateTimeOffset>(8) <= request.RetainUntil
                    && reader.GetInt64(9) = sequence
                    && reader.GetFieldValue<byte array>(10) = hash
                    && reader.GetFieldValue<byte array>(11) = request.CiphertextSha256
                    && not (reader.Read())

                return
                    if valid then
                        Some
                            {
                                PreviousEventHash = previous
                                EncryptionKeyId = Some key
                                CopyRevision = 2L
                                CopyState = Some state
                            }
                    else
                        None
        }

    let private externalAbsent connection transaction copyId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT NOT EXISTS(SELECT 1 FROM claimcore.managed_copies WHERE copy_id=@copy)",
                    connection,
                    transaction
                )

            Sql.uuid command "copy" copyId
            let! value = command.ExecuteScalarAsync()

            return
                if unbox<bool> value then
                    Some
                        {
                            PreviousEventHash = Array.zeroCreate<byte> 32
                            EncryptionKeyId = None
                            CopyRevision = 1L
                            CopyState = None
                        }
                else
                    None
        }

    let origin connection transaction (request: CopyAdoptionApprovalRequest) =
        match request.Origin with
        | CopyAdoptionOrigin.ProductExport(_, sequence, hash) ->
            product connection transaction request sequence hash
        | CopyAdoptionOrigin.AdoptedExternal _ ->
            externalAbsent connection transaction request.CopyId

    let used connection transaction approvalId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS(SELECT 1 FROM claimcore.managed_copy_adoption_approval_uses "
                    + "WHERE approval_id=@approval)",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approvalId
            let! value = command.ExecuteScalarAsync()
            return unbox<bool> value
        }

    let existing connection transaction eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT adoption_event_id,copy_id,case_id,owner_approval_id,canonical_action,"
                    + "candidate_sha256,custodian_canonical,custodian_signature,registry_canonical,"
                    + "registry_signature,inspection_canonical,inspection_signature,"
                    + "witness_sequence,witness_epoch,witness_entry_hash "
                    + "FROM claimcore.managed_copy_adoptions WHERE adoption_event_id=@event",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                return None
            else
                let bytes index = reader.GetFieldValue<byte array>(index)

                let value =
                    {
                        EventId = reader.GetGuid(0)
                        CopyId = reader.GetGuid(1)
                        CaseId = reader.GetGuid(2)
                        ApprovalId = reader.GetGuid(3)
                        Canonical = bytes 4
                        CandidateHash = bytes 5
                        CustodianCanonical = bytes 6
                        CustodianSignature = bytes 7
                        RegistryCanonical = bytes 8
                        RegistrySignature = bytes 9
                        InspectionCanonical = bytes 10
                        InspectionSignature = bytes 11
                        WitnessSequence = reader.GetInt64(12)
                        WitnessEpoch = reader.GetInt64(13)
                        WitnessHash = bytes 14
                    }

                return if reader.Read() then None else Some value
        }
