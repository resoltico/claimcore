namespace ClaimCore.Postgres

open System
open Npgsql

[<NoEquality; NoComparison>]
type internal CopyAdoptionReceiptCore =
    {
        EventId: Guid
        CopyId: Guid
        CaseId: Guid
        OriginKind: string
        ExportId: Guid option
        CopyRevision: int64
        CiphertextSha256: byte array
        CiphertextBytes: int64
        CapturedAt: DateTimeOffset
        RetainUntil: DateTimeOffset
        PreFenceKind: string
        PreFenceSequence: int64
        PreFenceHash: byte array
        LocationCommitment: byte array
        CustodianCommitment: byte array
        CustodianKeyId: Guid
        RegistryKeyId: Guid
        InspectorKeyId: Guid
        CustodianHolderId: Guid
        RegistryHolderId: Guid
        InspectorHolderId: Guid
        OwnerActorId: Guid
        OwnerGrantRevision: int64
        OwnerApprovalId: Guid
        ExecutorKind: string
        ActorAuthorityRevision: int64
    }

[<NoEquality; NoComparison>]
type internal CopyAdoptionReceiptSignatures =
    {
        CustodianCanonical: byte array
        CustodianSignature: byte array
        RegistryCanonical: byte array
        RegistrySignature: byte array
        InspectionCanonical: byte array
        InspectionSignature: byte array
        InspectionReportSha256: byte array
    }

[<NoEquality; NoComparison>]
type internal CopyAdoptionReceiptWitness =
    {
        Canonical: byte array
        CandidateHash: byte array
        PreviousCopyHash: byte array
        CopyEventHash: byte array
        Sequence: int64
        Epoch: int64
        EntryHash: byte array
        AdoptedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal CopyAdoptionReceipt =
    {
        Core: CopyAdoptionReceiptCore
        Signatures: CopyAdoptionReceiptSignatures
        Witness: CopyAdoptionReceiptWitness
    }

module internal ManagedCopyAdoptionEvidenceRows =
    let private query =
        "SELECT adoption_event_id,copy_id,case_id,origin_kind,export_id,copy_revision,"
        + "ciphertext_sha256,ciphertext_bytes,captured_at,retain_until,pre_fence_kind,"
        + "pre_fence_sequence,pre_fence_hash,location_commitment,custodian_commitment,"
        + "custodian_signing_key_id,registry_signing_key_id,inspector_signing_key_id,"
        + "custodian_holder_actor_id,registry_holder_actor_id,inspector_holder_actor_id,"
        + "owner_actor_id,owner_grant_revision,owner_approval_id,executor_kind,"
        + "actor_authority_revision,custodian_canonical,custodian_signature,"
        + "registry_canonical,registry_signature,inspection_canonical,inspection_signature,"
        + "inspection_report_sha256,canonical_action,candidate_sha256,previous_copy_hash,"
        + "copy_event_hash,witness_sequence,witness_epoch,witness_entry_hash,"
        + "adopted_at,valid_until FROM claimcore.managed_copy_adoptions WHERE copy_id=@copy"

    let private bytes (reader: Data.Common.DbDataReader) index =
        reader.GetFieldValue<byte array>(index)

    let private core (reader: Data.Common.DbDataReader) =
        {
            EventId = reader.GetGuid(0)
            CopyId = reader.GetGuid(1)
            CaseId = reader.GetGuid(2)
            OriginKind = reader.GetString(3)
            ExportId = if reader.IsDBNull(4) then None else Some(reader.GetGuid(4))
            CopyRevision = reader.GetInt64(5)
            CiphertextSha256 = bytes reader 6
            CiphertextBytes = reader.GetInt64(7)
            CapturedAt = reader.GetFieldValue<DateTimeOffset>(8)
            RetainUntil = reader.GetFieldValue<DateTimeOffset>(9)
            PreFenceKind = reader.GetString(10)
            PreFenceSequence = reader.GetInt64(11)
            PreFenceHash = bytes reader 12
            LocationCommitment = bytes reader 13
            CustodianCommitment = bytes reader 14
            CustodianKeyId = reader.GetGuid(15)
            RegistryKeyId = reader.GetGuid(16)
            InspectorKeyId = reader.GetGuid(17)
            CustodianHolderId = reader.GetGuid(18)
            RegistryHolderId = reader.GetGuid(19)
            InspectorHolderId = reader.GetGuid(20)
            OwnerActorId = reader.GetGuid(21)
            OwnerGrantRevision = reader.GetInt64(22)
            OwnerApprovalId = reader.GetGuid(23)
            ExecutorKind = reader.GetString(24)
            ActorAuthorityRevision = reader.GetInt64(25)
        }

    let private signatures (reader: Data.Common.DbDataReader) =
        {
            CustodianCanonical = bytes reader 26
            CustodianSignature = bytes reader 27
            RegistryCanonical = bytes reader 28
            RegistrySignature = bytes reader 29
            InspectionCanonical = bytes reader 30
            InspectionSignature = bytes reader 31
            InspectionReportSha256 = bytes reader 32
        }

    let private witness (reader: Data.Common.DbDataReader) =
        {
            Canonical = bytes reader 33
            CandidateHash = bytes reader 34
            PreviousCopyHash = bytes reader 35
            CopyEventHash = bytes reader 36
            Sequence = reader.GetInt64(37)
            Epoch = reader.GetInt64(38)
            EntryHash = bytes reader 39
            AdoptedAt = reader.GetFieldValue<DateTimeOffset>(40)
            ValidUntil = reader.GetFieldValue<DateTimeOffset>(41)
        }

    let find connection transaction copyId =
        task {
            use command = new NpgsqlCommand(query, connection, transaction)
            Sql.uuid command "copy" copyId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                return None
            else
                let value =
                    {
                        Core = core reader
                        Signatures = signatures reader
                        Witness = witness reader
                    }

                return if reader.Read() then None else Some value
        }
