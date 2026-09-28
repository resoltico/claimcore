namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open DataAuditCommon

/// Read-only audit of live-purged cases. It proves the witnessed purge/approval/denial seal and
/// exact absence of primary claimant rows, while deliberately leaving copy completion pending.
module internal DataAuditPurgedErasure =
    let private ticketDenial
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (commitments: ISuppressionCommitments)
        caseId
        eventId
        sequence
        epoch
        (hash: byte array)
        =
        use command =
            new NpgsqlCommand(
                "SELECT witness_intent_sequence,witness_intent_epoch,witness_intent_entry_hash "
                + "FROM claimcore.case_erasure_operation_denials "
                + "WHERE case_id=@case AND operation_commitment=@digest",
                connection,
                transaction
            )

        Sql.uuid command "case" caseId

        Sql.add command "digest" NpgsqlDbType.Bytea (box (commitments.Operation eventId))

        use reader = command.ExecuteReader()

        if
            not (reader.Read())
            || reader.IsDBNull(0)
            || reader.GetInt64(0) <> sequence
            || reader.GetInt64(1) <> epoch
            || reader.GetFieldValue<byte array>(2) <> hash
            || reader.Read()
        then
            corrupt ()

    let private receipts connection transaction commitments (value: PurgedErasureAuditRow) =
        task {
            let! approvals =
                DataAuditPurgedApprovals.read
                    connection
                    transaction
                    value.CaseId
                    value.PurgeEventId
                    value.CutoffSequence
                    value.LivePurgedAt

            for approval in approvals do
                ticketDenial
                    connection
                    transaction
                    commitments
                    value.CaseId
                    approval.ApprovalId
                    approval.WitnessSequence
                    approval.WitnessEpoch
                    approval.WitnessHash

            ticketDenial
                connection
                transaction
                commitments
                value.CaseId
                value.RequestEventId
                value.RequestWitnessSequence
                value.RequestWitnessEpoch
                value.RequestWitnessHash

            return approvals
        }

    let private witnessed
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        commitments
        (value: PurgedErasureAuditRow)
        approvals
        =
        task {
            DataAuditPurgedErasureCandidate.verify value approvals

            do!
                CaseWitnessAuditEvidence.verify
                    connection
                    transaction
                    witness
                    cutoff
                    value.CaseId
                    value.PurgeEventId
                    value.PurgeWitnessSequence
                    value.PurgeWitnessEpoch
                    value.PurgeWitnessHash
                    value.CandidateHash
                    ClaimCore.Witness.SettledAuthority

            try
                CaseErasurePurgedDenialsAudit.verify
                    connection
                    transaction
                    witness
                    commitments
                    value.CaseId
                    value.CutoffSequence
                    value.CutoffHash
                    value.SubjectIntentCount
                    value.SubjectIntentDigest
                    value.DenialCount
                    value.DenialDigest
                |> ignore
            with _ ->
                corrupt ()
        }

    let private verifySubjectAuthority
        connection
        transaction
        witness
        cutoff
        (value: PurgedErasureAuditRow)
        =
        task {
            let! prunedCutoff =
                CaseTombstonePruneReceiptAudit.verify
                    connection
                    transaction
                    witness
                    cutoff
                    value.CaseId

            let! _ =
                CaseTombstoneHoldAudit.verify
                    connection
                    transaction
                    witness
                    cutoff
                    prunedCutoff
                    value.CaseId

            let! _ =
                CaseTombstonePruneApprovalAudit.verify
                    connection
                    transaction
                    witness
                    cutoff
                    prunedCutoff
                    value.CaseId
                    value.PurgeEventId

            let! _ =
                CaseTombstoneTerminalApprovalAudit.verifyCase
                    connection
                    transaction
                    witness
                    cutoff
                    value.CaseId

            return ()
        }

    let private verifyRow
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (commitments: ISuppressionCommitments)
        (value: PurgedErasureAuditRow)
        =
        task {
            commitments.Admit()

            if
                value.SuppressionKeyId <> commitments.KeyId
                || value.PurgeWitnessSequence > cutoff
                || value.PurgeWitnessSequence <= value.CutoffSequence
                || value.RequestWitnessSequence > value.CutoffSequence
                || value.RequestWitnessEpoch <> witness.Identity.Epoch
                || value.PurgeWitnessEpoch <> witness.Identity.Epoch
            then
                corrupt ()

            do! CaseErasurePurgeDelete.verifyAbsent connection transaction value.CaseId
            do! verifySubjectAuthority connection transaction witness cutoff value

            let! approvals = receipts connection transaction commitments value
            do! witnessed connection transaction witness cutoff commitments value approvals
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
            let mutable after = Guid.Empty
            let mutable more = true
            let mutable count = 0L

            while more do
                let! rows = DataAuditPurgedErasureRows.page connection transaction after ct

                for row in rows do
                    let port = commitments |> Option.defaultWith corrupt
                    do! verifyRow connection transaction witness cutoff port row
                    count <- count + 1L

                match List.tryLast rows with
                | None -> more <- false
                | Some last -> after <- last.CaseId

            return count
        }
