namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql

[<NoEquality; NoComparison>]
type internal PurgedErasureAuditRow =
    {
        CaseId: Guid
        Phase: string
        RequestEventId: Guid
        SuppressionKeyId: Guid
        ReferenceCommitment: byte array
        RequestRevision: int64
        RequestDisposition: string
        RequestLifecycleSequence: int64
        RequestLifecycleHash: byte array
        RequestDenialCount: int64
        RequestDenialDigest: byte array
        RequestCandidateCommitment: byte array
        RequestWitnessSequence: int64
        RequestWitnessEpoch: int64
        RequestWitnessHash: byte array
        PurgeEventId: Guid
        ExecutorKind: string
        ProposalCommitment: byte array
        ValidUntil: DateTimeOffset
        PurgeRevision: int64
        PurgeLifecycleSequence: int64
        PurgeLifecycleHash: byte array
        CutoffSequence: int64
        CutoffHash: byte array
        SubjectIntentCount: int64
        SubjectIntentDigest: byte array
        DenialCount: int64
        DenialDigest: byte array
        CopyInventoryDigest: byte array
        Canonical: byte array
        CandidateHash: byte array
        PurgeWitnessSequence: int64
        PurgeWitnessEpoch: int64
        PurgeWitnessHash: byte array
        LivePurgedAt: DateTimeOffset
    }

module internal DataAuditPurgedErasureRows =
    let private query =
        "SELECT case_id,phase,request_event_id,suppression_key_id,reference_commitment,"
        + "source_revision,source_disposition,lifecycle_sequence,lifecycle_hash,"
        + "denial_count,denial_set_sha256,request_candidate_sha256,request_candidate_commitment,"
        + "request_witness_sequence,request_witness_epoch,request_witness_entry_hash,"
        + "purge_event_id,purge_executor_kind,purge_proposal_commitment,purge_valid_until,"
        + "purge_source_revision,purge_lifecycle_sequence,purge_lifecycle_hash,"
        + "purge_witness_cutoff_sequence,purge_witness_cutoff_hash,"
        + "purge_subject_intent_count,purge_subject_intent_sha256,"
        + "purge_denial_count,purge_denial_set_sha256,purge_copy_inventory_sha256,"
        + "purge_canonical_action,purge_candidate_sha256,purge_witness_sequence,"
        + "purge_witness_epoch,purge_witness_entry_hash,live_purged_at "
        + "FROM claimcore.case_erasure_tombstones "
        + "WHERE phase<>'ERASURE_REQUESTED' AND case_id>@after ORDER BY case_id LIMIT 50"

    let private row (reader: Data.Common.DbDataReader) =
        let bytes index = reader.GetFieldValue<byte array>(index)

        if not (reader.IsDBNull(11)) then
            invalidOp "Purged case retained a bare request digest."

        {
            CaseId = reader.GetGuid(0)
            Phase = reader.GetString(1)
            RequestEventId = reader.GetGuid(2)
            SuppressionKeyId = reader.GetGuid(3)
            ReferenceCommitment = bytes 4
            RequestRevision = reader.GetInt64(5)
            RequestDisposition = reader.GetString(6)
            RequestLifecycleSequence = reader.GetInt64(7)
            RequestLifecycleHash = bytes 8
            RequestDenialCount = reader.GetInt64(9)
            RequestDenialDigest = bytes 10
            RequestCandidateCommitment = bytes 12
            RequestWitnessSequence = reader.GetInt64(13)
            RequestWitnessEpoch = reader.GetInt64(14)
            RequestWitnessHash = bytes 15
            PurgeEventId = reader.GetGuid(16)
            ExecutorKind = reader.GetString(17)
            ProposalCommitment = bytes 18
            ValidUntil = reader.GetFieldValue<DateTimeOffset>(19)
            PurgeRevision = reader.GetInt64(20)
            PurgeLifecycleSequence = reader.GetInt64(21)
            PurgeLifecycleHash = bytes 22
            CutoffSequence = reader.GetInt64(23)
            CutoffHash = bytes 24
            SubjectIntentCount = reader.GetInt64(25)
            SubjectIntentDigest = bytes 26
            DenialCount = reader.GetInt64(27)
            DenialDigest = bytes 28
            CopyInventoryDigest = bytes 29
            Canonical = bytes 30
            CandidateHash = bytes 31
            PurgeWitnessSequence = reader.GetInt64(32)
            PurgeWitnessEpoch = reader.GetInt64(33)
            PurgeWitnessHash = bytes 34
            LivePurgedAt = reader.GetFieldValue<DateTimeOffset>(35)
        }

    let page connection transaction after (ct: CancellationToken) =
        task {
            use command = new NpgsqlCommand(query, connection, transaction)
            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let values = ResizeArray<PurgedErasureAuditRow>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(ct)
                reading <- found

                if found then
                    values.Add(row reader)

            return values |> Seq.toList
        }
