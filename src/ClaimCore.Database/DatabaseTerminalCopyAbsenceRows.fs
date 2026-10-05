namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Postgres.WitnessProtocolReconciliation
open ClaimCore.Witness

/// Every current copy must end in one witnessed VERIFIED_DELETED event with an exact consumed
/// independent approval. The first REGISTER and deletion must precede the signed inventory cutoff.
module internal DatabaseTerminalCopyAbsenceRows =
    [<NoEquality; NoComparison>]
    type private DeletionProof =
        {
            CopyId: Guid
            EventId: Guid
            ApprovalId: Guid
            SourceCaseId: Guid option
            EventSequence: int64
            EventEpoch: int64
            EventHash: byte array
            EventCandidate: byte array
            ApprovalSequence: int64
            ApprovalEpoch: int64
            ApprovalHash: byte array
            ApprovalCandidate: byte array
            ApprovalCutoff: int64
            ApprovalCutoffHash: byte array
        }

    let private query =
        "SELECT c.copy_id,c.source_case_id,c.state,c.revision,c.retain_until,"
        + "c.deletion_proof_sha256,r.witness_sequence,r.witness_epoch,"
        + "e.event_id,e.event_kind,e.revision,e.witness_sequence,e.witness_epoch,"
        + "e.witness_entry_hash,e.candidate_sha256,e.recorded_at,"
        + "u.approval_id,u.used_at,a.deletion_event_id,a.copy_id,"
        + "a.expected_copy_revision,a.inspection_report_sha256,a.expires_at,"
        + "a.witness_cutoff_sequence,a.witness_cutoff_hash,a.witness_sequence,"
        + "a.witness_epoch,a.witness_entry_hash,a.candidate_sha256 "
        + "FROM claimcore.managed_copies c "
        + "LEFT JOIN claimcore.managed_copy_events r "
        + "ON r.copy_id=c.copy_id AND r.revision=1 "
        + "LEFT JOIN claimcore.managed_copy_events e "
        + "ON e.copy_id=c.copy_id AND e.revision=c.revision "
        + "LEFT JOIN claimcore.managed_copy_deletion_approval_uses u "
        + "ON u.deletion_event_id=e.event_id "
        + "LEFT JOIN claimcore.managed_copy_deletion_approvals a "
        + "ON a.approval_id=u.approval_id "
        + "WHERE c.copy_id>@after ORDER BY c.copy_id LIMIT 50"

    let private bytes (reader: NpgsqlDataReader) index = reader.GetFieldValue<byte array>(index)

    let private exact (reader: NpgsqlDataReader) (witness: WitnessProtocol) reportCutoff tip now =
        let required = [ 5..28 ]

        if required |> List.exists reader.IsDBNull then
            false
        else
            let copyId = reader.GetGuid(0)
            let revision = reader.GetInt64(3)
            let retained = reader.GetFieldValue<DateTimeOffset>(4)
            let deletionEvent = reader.GetGuid(8)
            let approvalId = reader.GetGuid(16)
            let used = reader.GetFieldValue<DateTimeOffset>(17)

            reader.GetString(2) = "VERIFIED_DELETED"
            && revision > 1L
            && retained <= now
            && reader.GetInt64(6) <= reportCutoff
            && reader.GetInt64(7) = witness.Identity.Epoch
            && reader.GetString(9) = "VERIFIED_DELETED"
            && reader.GetInt64(10) = revision
            && reader.GetInt64(11) <= tip
            && reader.GetInt64(11) <= reportCutoff
            && reader.GetInt64(12) = witness.Identity.Epoch
            && reader.GetFieldValue<DateTimeOffset>(15) <= now
            && used >= retained
            && used <= now
            && reader.GetGuid(18) = deletionEvent
            && reader.GetGuid(19) = copyId
            && reader.GetInt64(20) = revision - 1L
            && bytes reader 21 = bytes reader 5
            && reader.GetFieldValue<DateTimeOffset>(22) >= used
            && reader.GetInt64(23) < reader.GetInt64(25)
            && reader.GetInt64(25) < reader.GetInt64(11)
            && reader.GetInt64(26) = witness.Identity.Epoch
            && reader.GetGuid(16) = approvalId

    let private readProof (reader: NpgsqlDataReader) =
        {
            CopyId = reader.GetGuid(0)
            EventId = reader.GetGuid(8)
            ApprovalId = reader.GetGuid(16)
            SourceCaseId = if reader.IsDBNull(1) then None else Some(reader.GetGuid(1))
            EventSequence = reader.GetInt64(11)
            EventEpoch = reader.GetInt64(12)
            EventHash = bytes reader 13
            EventCandidate = bytes reader 14
            ApprovalSequence = reader.GetInt64(25)
            ApprovalEpoch = reader.GetInt64(26)
            ApprovalHash = bytes reader 27
            ApprovalCandidate = bytes reader 28
            ApprovalCutoff = reader.GetInt64(23)
            ApprovalCutoffHash = bytes reader 24
        }

    let private witnessed
        connection
        transaction
        (witness: WitnessProtocol)
        tip
        (row: DeletionProof)
        (ct: CancellationToken)
        =
        task {
            let proof operation sequence epoch entryHash candidate =
                match row.SourceCaseId with
                | Some caseId ->
                    CaseWitnessAuditEvidence.verify
                        connection
                        transaction
                        witness
                        tip
                        caseId
                        operation
                        sequence
                        epoch
                        entryHash
                        candidate
                        SettledAuthority
                        ct
                | None ->
                    task {
                        do!
                            witness.VerifyAuthorityEvidenceForInstallation(
                                operation,
                                sequence,
                                epoch,
                                entryHash,
                                candidate,
                                ct
                            )
                    }

            do! witness.VerifyHistoricalTip(row.ApprovalCutoff, row.ApprovalCutoffHash, ct)

            do! proof row.EventId row.EventSequence row.EventEpoch row.EventHash row.EventCandidate

            do!
                proof
                    row.ApprovalId
                    row.ApprovalSequence
                    row.ApprovalEpoch
                    row.ApprovalHash
                    row.ApprovalCandidate
        }

    let verify
        connection
        transaction
        (witness: WitnessProtocol)
        reportCutoff
        tip
        now
        (ct: CancellationToken)
        =
        task {
            use digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            digest.AppendData(Encoding.ASCII.GetBytes("claimcore:terminal-copy-deletions:v1\000"))
            let mutable after = Guid.Empty
            let mutable count = 0L
            let mutable more = true

            while more do
                use command = new NpgsqlCommand(query, connection, transaction)
                Sql.uuid command "after" after
                use! reader = command.ExecuteReaderAsync(ct)
                let rows = ResizeArray<DeletionProof>()

                while reader.Read() do
                    if
                        reader.GetGuid(0) <= after
                        || not (exact reader witness reportCutoff tip now)
                    then
                        invalidOp "Terminal copy deletion row is unavailable."

                    let row = readProof reader
                    rows.Add row
                    after <- row.CopyId

                reader.Close()

                for row in rows do
                    do! witnessed connection transaction witness tip row ct
                    digest.AppendData(Encoding.ASCII.GetBytes(row.CopyId.ToString("D")))
                    digest.AppendData(Encoding.ASCII.GetBytes(row.EventId.ToString("D")))
                    digest.AppendData(Encoding.ASCII.GetBytes(row.ApprovalId.ToString("D")))
                    count <- count + 1L

                more <- rows.Count = 50

            return count, digest.GetHashAndReset()
        }
