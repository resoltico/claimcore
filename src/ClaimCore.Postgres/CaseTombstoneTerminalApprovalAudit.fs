namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open DataAuditCommon

/// Historical steward drafts are audited even when a later owner decision supersedes them.
/// They do not, by themselves, prove absence of a copy or advance a privacy phase.
module internal CaseTombstoneTerminalApprovalAudit =
    let private verifySlots connection transaction (row: TerminalApprovalAuditRow) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT count(*) FROM claimcore.case_erasure_terminal_approvals "
                    + "WHERE terminal_event_id=@event",
                    connection,
                    transaction
                )

            Sql.uuid command "event" (TombstoneTerminalProposal.eventId row.Proposal)
            let! value = command.ExecuteScalarAsync()

            if value :?> int64 > 2L then
                corrupt ()
        }

    let private verifyWitness (witness: WitnessProtocol) cutoff (row: TerminalApprovalAuditRow) =
        let copy = TombstoneTerminalProposal.copy row.Proposal

        let prune =
            witness.EvidenceStore.TryReadMetadataOperation(copy.PruneEventId, SettledAuthority)
            |> Option.defaultWith corrupt

        if
            prune.Ticket.ScopeKind <> Case
            || prune.Ticket.SubjectCaseId <> Some copy.CaseId
            || row.WitnessSequence <= prune.Ticket.Sequence
            || row.WitnessSequence > cutoff
            || row.WitnessEpoch <> witness.Identity.Epoch
        then
            corrupt ()

        witnessProof (fun () ->
            witness.VerifyAuthorityEvidenceForCase(
                row.ApprovalId,
                row.WitnessSequence,
                row.WitnessEpoch,
                row.WitnessHash,
                row.CandidateHash,
                copy.CaseId
            ))

    let private verifyRow
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        caseId
        (row: TerminalApprovalAuditRow)
        =
        task {
            let copy = TombstoneTerminalProposal.copy row.Proposal

            if
                copy.CaseId <> caseId
                || row.ActorId = Guid.Empty
                || row.GrantRevision < 1L
                || not (
                    CaseTombstoneTerminalPolicy.valid
                        row.Proposal
                        row.ApprovalId
                        row.ExpiresAt
                        row.ApprovedAt
                )
            then
                corrupt ()

            let canonical =
                CaseTombstoneTerminalCandidate.approval
                    row.Proposal
                    row.ApprovalId
                    row.ActorId
                    row.GrantRevision
                    row.ApprovedAt
                    row.ExpiresAt

            try
                if
                    canonical <> row.Canonical || row.CandidateHash <> SHA256.HashData(canonical)
                then
                    corrupt ()

                do!
                    DataAuditTerminalStewardRole.verify
                        connection
                        transaction
                        row.ActorId
                        caseId
                        row.GrantRevision
                        Threading.CancellationToken.None

                verifyWitness witness cutoff row
                do! verifySlots connection transaction row
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }

    let verifyCase
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        caseId
        =
        task {
            let mutable after = Guid.Empty
            let mutable more = true
            let mutable count = 0L

            while more do
                let! rows =
                    CaseTombstoneTerminalApprovalAuditRows.page connection transaction caseId after

                for row in rows do
                    do! verifyRow connection transaction witness cutoff caseId row
                    count <- count + 1L

                match List.tryLast rows with
                | None -> more <- false
                | Some last -> after <- last.ApprovalId

            return count
        }
