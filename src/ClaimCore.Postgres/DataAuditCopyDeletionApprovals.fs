namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open DataAuditCommon
open WitnessProtocolReconciliation

/// Replays every metadata-only deletion approval and its optional one-use copy event.
module internal DataAuditCopyDeletionApprovals =
    [<NoEquality; NoComparison>]
    type private ApprovalWitnessProof =
        {
            ApprovalId: Guid
            SourceCaseId: Guid option
            Sequence: int64
            Epoch: int64
            EntryHash: byte array
            CandidateDigest: byte array
            CutoffSequence: int64
            CutoffHash: byte array
        }

    let private query =
        "SELECT a.approval_id,a.deletion_event_id,a.copy_id,a.verifier_signing_key_id,"
        + "a.expected_copy_revision,a.location_commitment,a.inspection_report_sha256,"
        + "a.witness_cutoff_sequence,a.witness_cutoff_hash,a.approver_actor_id,"
        + "a.approver_grant_revision,a.expires_at,a.canonical_action,a.candidate_sha256,"
        + "a.witness_sequence,a.witness_epoch,a.witness_entry_hash,c.source_case_id,"
        + "s.signer_purpose,s.holder_actor_id,actor.principal_kind,"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=1),"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=2),"
        + "u.deletion_event_id,e.copy_id,e.revision,e.event_kind,e.producer_kind,e.canonical_attestation "
        + "FROM claimcore.managed_copy_deletion_approvals a "
        + "JOIN claimcore.managed_copies c ON c.copy_id=a.copy_id "
        + "JOIN claimcore.managed_copy_signers s ON s.signing_key_id=a.verifier_signing_key_id "
        + "JOIN claimcore.actors actor ON actor.actor_id=a.approver_actor_id "
        + "LEFT JOIN claimcore.managed_copy_deletion_approval_uses u ON u.approval_id=a.approval_id "
        + "LEFT JOIN claimcore.managed_copy_events e ON e.event_id=u.deletion_event_id "
        + "WHERE a.approval_id>@after ORDER BY a.approval_id LIMIT 50"

    let private bytes (reader: NpgsqlDataReader) index = reader.GetFieldValue<byte array>(index)

    let private request (reader: NpgsqlDataReader) : CopyDeletionApprovalRequest =
        {
            ApprovalId = reader.GetGuid(0)
            DeletionEventId = reader.GetGuid(1)
            CopyId = reader.GetGuid(2)
            VerifierSigningKeyId = reader.GetGuid(3)
            ExpectedCopyRevision = reader.GetInt64(4)
            LocationCommitment = bytes reader 5
            InspectionReportSha256 = bytes reader 6
            WitnessCutoffSequence = reader.GetInt64(7)
            WitnessCutoffHash = bytes reader 8
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(11)
        }

    let private proofMatches
        (witness: WitnessProtocol)
        cutoff
        (reader: NpgsqlDataReader)
        (canonical: byte array)
        =
        reader.GetString(18) = "DELETION_VERIFIER"
        && reader.GetGuid(19) = reader.GetGuid(9)
        && reader.GetString(20) = "HUMAN"
        && reader.GetInt64(10) > 0L
        && reader.GetInt64(14) > reader.GetInt64(21)
        && (reader.IsDBNull(22) || reader.GetInt64(14) < reader.GetInt64(22))
        && reader.GetInt64(14) <= cutoff
        && reader.GetInt64(15) = witness.Identity.Epoch
        && bytes reader 13 = SHA256.HashData(canonical)
        && bytes reader 12 = canonical

    let private usedMatches (reader: NpgsqlDataReader) (approval: CopyDeletionApprovalRequest) =
        if reader.IsDBNull(23) then
            true
        else
            let expected =
                match reader.GetString(27) with
                | "OWNER_ATTESTED" ->
                    let transition =
                        ManagedCopyTransitionAttestation.parse (bytes reader 28)
                        |> Option.defaultWith corrupt

                    transition.Copy.EventId = approval.DeletionEventId
                    && transition.Copy.CopyId = approval.CopyId
                    && transition.DeletionApprovalId = Some approval.ApprovalId
                    && transition.DeletionProofSha256 = Some approval.InspectionReportSha256
                    && transition.ActionWitnessCutoffSequence = approval.WitnessCutoffSequence
                    && transition.ActionWitnessCutoffHash = approval.WitnessCutoffHash
                | "PRODUCT_EXPORT"
                | "ADOPTED_EXTERNAL" ->
                    let transition =
                        ManagedCopyAdoptedTransitionAttestation.parse (bytes reader 28)
                        |> Option.defaultWith corrupt

                    transition.EventId = approval.DeletionEventId
                    && transition.CopyId = approval.CopyId
                    && transition.DeletionApprovalId = Some approval.ApprovalId
                    && transition.DeletionProofSha256 = Some approval.InspectionReportSha256
                    && transition.ActionWitnessCutoffSequence = approval.WitnessCutoffSequence
                    && transition.ActionWitnessCutoffHash = approval.WitnessCutoffHash
                | _ -> false

            expected
            && reader.GetGuid(23) = approval.DeletionEventId
            && reader.GetGuid(24) = approval.CopyId
            && reader.GetInt64(25) = approval.ExpectedCopyRevision + 1L
            && reader.GetString(26) = "VERIFIED_DELETED"

    let private witnessProofForRow
        (reader: NpgsqlDataReader)
        (approval: CopyDeletionApprovalRequest)
        =
        {
            ApprovalId = approval.ApprovalId
            SourceCaseId =
                if reader.IsDBNull(17) then
                    None
                else
                    Some(reader.GetGuid(17))
            Sequence = reader.GetInt64(14)
            Epoch = reader.GetInt64(15)
            EntryHash = bytes reader 16
            CandidateDigest = bytes reader 13
            CutoffSequence = approval.WitnessCutoffSequence
            CutoffHash = approval.WitnessCutoffHash
        }

    let private verifyWitness connection transaction (witness: WitnessProtocol) cutoff proof ct =
        task {
            do!
                witnessProofAsync (fun () ->
                    witness.VerifyHistoricalTip(proof.CutoffSequence, proof.CutoffHash, ct))

            match proof.SourceCaseId with
            | Some caseId ->
                do!
                    CaseWitnessAuditEvidence.verify
                        connection
                        transaction
                        witness
                        cutoff
                        caseId
                        proof.ApprovalId
                        proof.Sequence
                        proof.Epoch
                        proof.EntryHash
                        proof.CandidateDigest
                        SettledAuthority
                        ct
            | None ->
                do!
                    witnessProofAsync (fun () ->
                        witness.VerifyAuthorityEvidenceForInstallation(
                            proof.ApprovalId,
                            proof.Sequence,
                            proof.Epoch,
                            proof.EntryHash,
                            proof.CandidateDigest,
                            ct
                        ))
        }

    let private verifyRow (witness: WitnessProtocol) cutoff (reader: NpgsqlDataReader) =
        let approval = request reader

        let canonical =
            ManagedCopyDeletionApprovalCandidate.canonical
                approval
                (reader.GetGuid(9))
                (reader.GetInt64(10))

        try
            if
                not (proofMatches witness cutoff reader canonical)
                || not (usedMatches reader approval)
            then
                corrupt ()

            witnessProofForRow reader approval
        finally
            CryptographicOperations.ZeroMemory(canonical)

    let verify connection transaction witness cutoff (ct: CancellationToken) =
        task {
            let mutable after = Guid.Empty
            let mutable more = true
            let mutable count = 0L

            while more do
                use command = new NpgsqlCommand(query, connection, transaction)
                Sql.uuid command "after" after

                let! proofs =
                    task {
                        use! reader = command.ExecuteReaderAsync(ct)
                        let page = ResizeArray<ApprovalWitnessProof>()

                        while reader.Read() do
                            let proof = verifyRow witness cutoff reader

                            if proof.ApprovalId <= after then
                                corrupt ()

                            after <- proof.ApprovalId
                            page.Add proof

                        return page.ToArray()
                    }

                for proof in proofs do
                    do! verifyWitness connection transaction witness cutoff proof ct

                count <- count + int64 proofs.Length
                more <- proofs.Length = 50

            return count
        }
