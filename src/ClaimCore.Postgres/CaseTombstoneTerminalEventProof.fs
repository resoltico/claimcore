namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open DataAuditCommon

[<NoEquality; NoComparison>]
type private TerminalUseAuditRow =
    {
        Slot: int
        ApprovalId: Guid
        CaseId: Guid
        TerminalEventId: Guid
        ActionName: string
        ActorId: Guid
        GrantRevision: int64
        ApprovedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        Canonical: byte array
        CandidateHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
    }

module internal CaseTombstoneTerminalEventProof =
    let private rows connection transaction eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT u.slot,u.approval_id,u.case_id,u.terminal_event_id,a.action_name,"
                    + "a.approver_actor_id,a.approver_grant_revision,a.approved_at,a.expires_at,"
                    + "a.canonical_action,a.candidate_sha256,a.witness_sequence,"
                    + "a.approval_witness_epoch,a.witness_entry_hash "
                    + "FROM claimcore.case_erasure_terminal_approval_uses u "
                    + "JOIN claimcore.case_erasure_terminal_approvals a "
                    + "ON a.approval_id=u.approval_id AND a.case_id=u.case_id "
                    + "WHERE u.terminal_event_id=@event ORDER BY u.slot LIMIT 3",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            use! reader = command.ExecuteReaderAsync()
            let values = ResizeArray<TerminalUseAuditRow>()

            while reader.Read() do
                values.Add
                    {
                        Slot = reader.GetInt32(0)
                        ApprovalId = reader.GetGuid(1)
                        CaseId = reader.GetGuid(2)
                        TerminalEventId = reader.GetGuid(3)
                        ActionName = reader.GetString(4)
                        ActorId = reader.GetGuid(5)
                        GrantRevision = reader.GetInt64(6)
                        ApprovedAt = reader.GetFieldValue<DateTimeOffset>(7)
                        ExpiresAt = reader.GetFieldValue<DateTimeOffset>(8)
                        Canonical = reader.GetFieldValue<byte array>(9)
                        CandidateHash = reader.GetFieldValue<byte array>(10)
                        WitnessSequence = reader.GetInt64(11)
                        WitnessEpoch = reader.GetInt64(12)
                        WitnessHash = reader.GetFieldValue<byte array>(13)
                    }

            return values |> Seq.toList
        }

    let private action =
        function
        | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ ->
            "CONFIRM_MANAGED_PAYLOAD_ABSENCE"
        | TombstoneTerminalProposal.CompleteSuppressionHorizon _ -> "COMPLETE_SUPPRESSION_HORIZON"

    let private requireUseShape
        (witness: WitnessProtocol)
        (event: StoredTerminalEvent)
        (decoded: TerminalEventDecoded)
        expectedSlot
        expectedApproval
        (row: TerminalUseAuditRow)
        =
        if
            row.Slot <> expectedSlot
            || row.ApprovalId <> expectedApproval
            || row.CaseId <> event.CaseId
            || row.TerminalEventId <> event.EventId
            || row.ActionName <> action decoded.Proposal
            || row.WitnessSequence >= event.WitnessSequence
            || row.WitnessEpoch <> witness.Identity.Epoch
            || row.ExpiresAt <= event.RecordedAt
        then
            corrupt ()

    let private verifyUse
        connection
        transaction
        (witness: WitnessProtocol)
        (event: StoredTerminalEvent)
        (decoded: TerminalEventDecoded)
        expectedSlot
        expectedApproval
        (row: TerminalUseAuditRow)
        =
        task {
            requireUseShape witness event decoded expectedSlot expectedApproval row

            let canonical =
                CaseTombstoneTerminalCandidate.approval
                    decoded.Proposal
                    row.ApprovalId
                    row.ActorId
                    row.GrantRevision
                    row.ApprovedAt
                    row.ExpiresAt

            try
                if
                    row.Canonical <> canonical || row.CandidateHash <> SHA256.HashData(canonical)
                then
                    corrupt ()

                do!
                    DataAuditTerminalStewardRole.verify
                        connection
                        transaction
                        row.ActorId
                        event.CaseId
                        event.ActorAuthorityRevision
                        Threading.CancellationToken.None

                witnessProof (fun () ->
                    witness.VerifyAuthorityEvidenceForCase(
                        row.ApprovalId,
                        row.WitnessSequence,
                        row.WitnessEpoch,
                        row.WitnessHash,
                        row.CandidateHash,
                        event.CaseId
                    ))
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }

    let verifyApprovals connection transaction witness (event: StoredTerminalEvent) decoded =
        task {
            let! uses = rows connection transaction event.EventId

            match uses with
            | [ first; second ] when
                first.ActorId <> second.ActorId && event.ApprovalOneId <> event.ApprovalTwoId
                ->
                do!
                    verifyUse
                        connection
                        transaction
                        witness
                        event
                        decoded
                        1
                        event.ApprovalOneId
                        first

                do!
                    verifyUse
                        connection
                        transaction
                        witness
                        event
                        decoded
                        2
                        event.ApprovalTwoId
                        second
            | _ -> corrupt ()
        }
