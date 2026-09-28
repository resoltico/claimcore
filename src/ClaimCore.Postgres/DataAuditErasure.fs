namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Witness
open DataAuditCommon

type private ErasureAuditRow =
    {
        CaseId: Guid
        EventId: Guid
        KeyId: Guid
        ReferenceCommitment: byte array
        Phase: string
        SourceRevision: int64
        SourceDisposition: string
        Sequence: int64
        EventHash: byte array
        DenialCount: int64
        DenialDigest: byte array
        CandidateHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
    }

/// A pending erasure is audited while its primary payload still exists. Later purge states
/// require their own independent absence/copy proof and deliberately fail closed here.
module internal DataAuditErasure =
    let private row (reader: Npgsql.NpgsqlDataReader) =
        {
            CaseId = reader.GetGuid(0)
            EventId = reader.GetGuid(1)
            KeyId = reader.GetGuid(2)
            ReferenceCommitment = reader.GetFieldValue<byte array>(3)
            Phase = reader.GetString(4)
            SourceRevision = reader.GetInt64(5)
            SourceDisposition = reader.GetString(6)
            Sequence = reader.GetInt64(7)
            EventHash = reader.GetFieldValue<byte array>(8)
            DenialCount = reader.GetInt64(9)
            DenialDigest = reader.GetFieldValue<byte array>(10)
            CandidateHash = reader.GetFieldValue<byte array>(11)
            WitnessSequence = reader.GetInt64(12)
            WitnessEpoch = reader.GetInt64(13)
            WitnessHash = reader.GetFieldValue<byte array>(14)
        }

    let private page connection transaction after (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT case_id,request_event_id,suppression_key_id,reference_commitment,"
                    + "phase,source_revision,source_disposition,lifecycle_sequence,lifecycle_hash,"
                    + "denial_count,denial_set_sha256,request_candidate_sha256,"
                    + "request_witness_sequence,request_witness_epoch,request_witness_entry_hash "
                    + "FROM claimcore.case_erasure_tombstones "
                    + "WHERE phase='ERASURE_REQUESTED' AND case_id>@after "
                    + "ORDER BY case_id LIMIT 50",
                    connection,
                    transaction
                )

            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let items = ResizeArray<ErasureAuditRow>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(ct)
                reading <- found

                if found then
                    items.Add(row reader)

            return items |> Seq.toList
        }

    let private requireNoMissingFence connection transaction (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.case_lifecycle_events e "
                    + "WHERE e.action_name='REQUEST_ERASURE' AND NOT EXISTS "
                    + "(SELECT 1 FROM claimcore.case_erasure_tombstones t "
                    + "WHERE t.case_id=e.case_id AND t.request_event_id=e.event_id))",
                    connection,
                    transaction
                )

            let! missing = command.ExecuteScalarAsync(ct)

            if unbox<bool> missing then
                corrupt ()
        }

    let private identityMatches (value: ErasureAuditRow) (event: LifecycleAuditEventRow) =
        value.Phase = "ERASURE_REQUESTED"
        && value.CaseId = event.CaseId
        && value.Sequence = event.Sequence
        && value.SourceRevision = event.BusinessRevision
        && value.EventHash = event.EventHash
        && event.ActionName = "REQUEST_ERASURE"

    let private witnessMatches cutoff (value: ErasureAuditRow) (event: LifecycleAuditEventRow) =
        value.CandidateHash = event.CandidateHash
        && value.WitnessSequence = event.WitnessSequence
        && value.WitnessEpoch = event.WitnessEpoch
        && value.WitnessHash = event.WitnessHash
        && value.WitnessSequence <= cutoff

    let private suppressionMatches
        (commitments: ISuppressionCommitments)
        (value: ErasureAuditRow)
        (event: LifecycleAuditEventRow)
        =
        let decoded = CaseLifecycleAuditCodec.decodeEvent event.Canonical

        value.KeyId = commitments.KeyId
        && commitments.Reference event.Reference = value.ReferenceCommitment
        && value.SourceDisposition = CaseLifecycleCandidate.dispositionName decoded.Disposition

    let private verifyDenials
        connection
        transaction
        (commitments: ISuppressionCommitments)
        (value: ErasureAuditRow)
        (ct: CancellationToken)
        =
        task {
            let! count, scannedCount, digest =
                task {
                    try
                        let! count =
                            CaseErasureDenials.verify
                                connection
                                transaction
                                value.CaseId
                                commitments
                                ct

                        let! scannedCount, digest =
                            CaseErasureDenials.scan
                                connection
                                transaction
                                value.CaseId
                                commitments
                                false
                                ct

                        return count, scannedCount, digest
                    with :? InvalidOperationException ->
                        return corrupt ()
                }

            if
                count <> value.DenialCount
                || scannedCount <> count
                || not (
                    CryptographicOperations.FixedTimeEquals(
                        ReadOnlySpan<byte>(digest),
                        ReadOnlySpan<byte>(value.DenialDigest)
                    )
                )
            then
                corrupt ()
        }

    let private verifyRow
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (commitments: ISuppressionCommitments)
        (value: ErasureAuditRow)
        (ct: CancellationToken)
        =
        task {
            let! found = CaseLifecycleAuditRows.eventById connection transaction value.EventId ct

            let event = found |> Option.defaultWith corrupt

            if
                not (identityMatches value event)
                || not (witnessMatches cutoff value event)
                || not (suppressionMatches commitments value event)
            then
                corrupt ()

            witnessProof (fun () ->
                witness.VerifyAuthorityEvidenceForCase(
                    value.EventId,
                    value.WitnessSequence,
                    value.WitnessEpoch,
                    value.WitnessHash,
                    value.CandidateHash,
                    value.CaseId
                ))

            do! verifyDenials connection transaction commitments value ct
        }

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        (commitments: ISuppressionCommitments option)
        (ct: CancellationToken)
        =
        task {
            do! requireNoMissingFence connection transaction ct
            let mutable after = Guid.Empty
            let mutable more = true
            let mutable count = 0L

            while more do
                let! items = page connection transaction after ct

                for item in items do
                    let port = commitments |> Option.defaultWith corrupt
                    port.Admit()
                    do! verifyRow connection transaction witness cutoff port item ct
                    count <- count + 1L

                match List.tryLast items with
                | None -> more <- false
                | Some item -> after <- item.CaseId

            return count
        }
