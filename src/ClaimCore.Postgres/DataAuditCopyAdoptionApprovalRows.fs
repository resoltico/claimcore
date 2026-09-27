namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql

[<NoEquality; NoComparison>]
type internal CopyAdoptionApprovalAuditRow =
    {
        ApprovalId: Guid
        AdoptionEventId: Guid
        CopyId: Guid
        CaseId: Guid
        OriginKind: string
        ExportId: Guid option
        CiphertextSha256: byte array
        CiphertextBytes: int64
        PreFenceKind: string
        PreFenceSequence: int64
        PreFenceHash: byte array
        LocationCommitment: byte array
        RetainUntil: DateTimeOffset
        ActorId: Guid
        GrantRevision: int64
        ApprovedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        Canonical: byte array
        CandidateHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
        UsedEventId: Guid option
        RequestSequence: int64
        RequestedAt: DateTimeOffset
        ExportCaseId: Guid option
        ExportSha256: byte array option
        ExportSequence: int64 option
        ExportHash: byte array option
        ExportIssuedAt: DateTimeOffset option
    }

module internal DataAuditCopyAdoptionApprovalRows =
    let private query =
        "SELECT a.approval_id,a.adoption_event_id,a.copy_id,a.case_id,a.origin_kind,"
        + "a.export_id,a.ciphertext_sha256,a.ciphertext_bytes,a.pre_fence_kind,"
        + "a.pre_fence_sequence,a.pre_fence_hash,a.location_commitment,a.retain_until,"
        + "a.owner_actor_id,a.owner_grant_revision,a.approved_at,a.expires_at,"
        + "a.canonical_action,a.candidate_sha256,a.witness_sequence,a.witness_epoch,"
        + "a.witness_entry_hash,u.adoption_event_id,t.request_witness_sequence,t.created_at,"
        + "e.case_id,e.artifact_sha256,e.witness_sequence,e.witness_entry_hash,e.issued_at "
        + "FROM claimcore.managed_copy_adoption_approvals a "
        + "JOIN claimcore.case_erasure_tombstones t ON t.case_id=a.case_id "
        + "LEFT JOIN claimcore.managed_copy_adoption_approval_uses u "
        + "ON u.approval_id=a.approval_id "
        + "LEFT JOIN claimcore.recovery_artifact_exports e ON e.export_id=a.export_id "
        + "WHERE a.approval_id>@after ORDER BY a.approval_id LIMIT 50"

    let private bytes (reader: NpgsqlDataReader) index = reader.GetFieldValue<byte array>(index)

    let private optional read (reader: NpgsqlDataReader) index =
        if reader.IsDBNull(index) then
            None
        else
            Some(read reader index)

    let private read (reader: NpgsqlDataReader) =
        {
            ApprovalId = reader.GetGuid(0)
            AdoptionEventId = reader.GetGuid(1)
            CopyId = reader.GetGuid(2)
            CaseId = reader.GetGuid(3)
            OriginKind = reader.GetString(4)
            ExportId = optional (fun row index -> row.GetGuid(index)) reader 5
            CiphertextSha256 = bytes reader 6
            CiphertextBytes = reader.GetInt64(7)
            PreFenceKind = reader.GetString(8)
            PreFenceSequence = reader.GetInt64(9)
            PreFenceHash = bytes reader 10
            LocationCommitment = bytes reader 11
            RetainUntil = reader.GetFieldValue<DateTimeOffset>(12)
            ActorId = reader.GetGuid(13)
            GrantRevision = reader.GetInt64(14)
            ApprovedAt = reader.GetFieldValue<DateTimeOffset>(15)
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(16)
            Canonical = bytes reader 17
            CandidateHash = bytes reader 18
            WitnessSequence = reader.GetInt64(19)
            WitnessEpoch = reader.GetInt64(20)
            WitnessHash = bytes reader 21
            UsedEventId = optional (fun row index -> row.GetGuid(index)) reader 22
            RequestSequence = reader.GetInt64(23)
            RequestedAt = reader.GetFieldValue<DateTimeOffset>(24)
            ExportCaseId = optional (fun row index -> row.GetGuid(index)) reader 25
            ExportSha256 = optional bytes reader 26
            ExportSequence = optional (fun row index -> row.GetInt64(index)) reader 27
            ExportHash = optional bytes reader 28
            ExportIssuedAt =
                optional (fun row index -> row.GetFieldValue<DateTimeOffset>(index)) reader 29
        }

    let page connection transaction after (ct: CancellationToken) =
        task {
            use command = new NpgsqlCommand(query, connection, transaction)
            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<CopyAdoptionApprovalAuditRow>()

            while reader.Read() do
                rows.Add(read reader)

            return rows.ToArray()
        }
