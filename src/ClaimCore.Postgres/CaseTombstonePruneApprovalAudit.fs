namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Text.Json
open Npgsql
open ClaimCore.Application
open DataAuditCommon

/// Historical proposals may be superseded; each approval is still exact, witnessed authority.
/// Only an executed prune can consume two approvals and therefore needs target-set recomputation.
module internal CaseTombstonePruneApprovalAudit =
    let private fields =
        [|
            "version"
            "kind"
            "approvalId"
            "caseId"
            "pruneEventId"
            "proposal"
            "approverActorId"
            "approverGrantRevision"
            "approvedAt"
            "expiresAt"
        |]

    let private proposal (row: TombstonePruneApprovalAuditRow) =
        try
            use document = JsonDocument.Parse(ReadOnlyMemory<byte>(row.Canonical))
            let root = document.RootElement
            let names = root.EnumerateObject() |> Seq.map _.Name |> Seq.toArray

            if
                names <> fields
                || root.GetProperty("version").GetInt32() <> 1
                || root.GetProperty("kind").GetString() <> "CASE_WITNESS_PRUNE_APPROVAL"
                || root.GetProperty("approvalId").GetGuid() <> row.ApprovalId
                || root.GetProperty("caseId").GetGuid() <> row.CaseId
                || root.GetProperty("pruneEventId").GetGuid() <> row.PruneEventId
            then
                corrupt ()

            let embedded = root.GetProperty("proposal").GetBytesFromBase64()

            try
                CaseTombstoneProposalCodec.decode embedded |> Option.defaultWith corrupt
            finally
                CryptographicOperations.ZeroMemory(embedded)
        with _ ->
            corrupt ()

    let private matches (row: TombstonePruneApprovalAuditRow) (value: TombstonePruneProposal) =
        value.EventId = row.PruneEventId
        && value.CaseId = row.CaseId
        && value.PurgeEventId = row.PurgeEventId
        && value.CutoffSequence = row.CutoffSequence
        && value.CutoffHash = Convert.ToHexStringLower row.CutoffHash
        && value.TargetCount = row.TargetCount
        && value.TargetDigest = Convert.ToHexStringLower row.TargetDigest
        && value.ExpectedAuthorityRevision = row.AuthorityRevision
        && value.ExpectedAuthorityHash = Convert.ToHexStringLower row.AuthorityHash
        && value.ValidUntil = row.ValidUntil
        && row.WitnessSequence > value.CutoffSequence
        && row.ActorId <> Guid.Empty
        && row.GrantRevision > 0L
        && row.ExpiresAt > row.ApprovedAt
        && row.ExpiresAt <= value.ValidUntil
        && value.ValidUntil <= row.ApprovedAt.AddHours(24.0)

    let private verifyRow
        (witness: WitnessProtocol)
        cutoff
        prunedCutoff
        purgeEventId
        (row: TombstonePruneApprovalAuditRow)
        ct
        =
        task {
            if
                row.PurgeEventId <> purgeEventId
                || row.WitnessSequence > cutoff
                || row.WitnessEpoch <> witness.Identity.Epoch
            then
                corrupt ()

            let value = proposal row

            if not (matches row value) then
                corrupt ()

            let canonical =
                CaseTombstoneCandidate.approval
                    value
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

                if
                    not (
                        prunedCutoff
                        |> Option.exists (fun sealedAt -> row.WitnessSequence <= sealedAt)
                    )
                then
                    do!
                        witness.VerifyAuthorityEvidenceForCase(
                            row.ApprovalId,
                            row.WitnessSequence,
                            row.WitnessEpoch,
                            row.WitnessHash,
                            row.CandidateHash,
                            row.CaseId,
                            ct
                        )
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }

    let private verifySlots connection transaction (row: TombstonePruneApprovalAuditRow) =
        use command =
            new NpgsqlCommand(
                "SELECT count(*) FROM claimcore.case_erasure_prune_approvals "
                + "WHERE case_id=@case AND prune_event_id=@event",
                connection,
                transaction
            )

        Sql.uuid command "case" row.CaseId
        Sql.uuid command "event" row.PruneEventId
        let count = command.ExecuteScalar() :?> int64

        if count < 1L || count > 2L then
            corrupt ()

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        prunedCutoff
        caseId
        purgeEventId
        ct
        =
        task {
            let mutable after = Guid.Empty
            let mutable count = 0L
            let mutable more = true

            while more do
                let! rows =
                    CaseTombstonePruneApprovalAuditRows.page connection transaction caseId after

                for row in rows do
                    do! verifyRow witness cutoff prunedCutoff purgeEventId row ct
                    verifySlots connection transaction row
                    count <- count + 1L

                match List.tryLast rows with
                | None -> more <- false
                | Some last -> after <- last.ApprovalId

            return count
        }
