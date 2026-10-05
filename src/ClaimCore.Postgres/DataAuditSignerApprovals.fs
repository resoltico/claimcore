namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

/// Full witnessed approval scan. Current grant revocation does not rewrite a past approval.
module internal DataAuditSignerApprovals =
    let private page connection transaction after (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT approval_id FROM claimcore.managed_copy_signer_approvals "
                    + "WHERE approval_id>@after ORDER BY approval_id LIMIT 50",
                    connection,
                    transaction
                )

            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let ids = ResizeArray<Guid>()

            while reader.Read() do
                ids.Add(reader.GetGuid(0))

            return ids |> Seq.toList
        }

    let private humanActor connection transaction actorId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.actors "
                    + "WHERE actor_id=@actor AND principal_kind='HUMAN')",
                    connection,
                    transaction
                )

            Sql.uuid command "actor" actorId
            let! result = command.ExecuteScalarAsync(ct)
            return unbox<bool> result
        }

    let verifyOne
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        approvalId
        (ct: CancellationToken)
        =
        task {
            let! found = ManagedCopySignerApprovalRead.load connection transaction approvalId
            let approval = found |> Option.defaultWith corrupt
            let! human = humanActor connection transaction approval.ActorId ct

            if
                not human
                || approval.WitnessSequence > cutoff
                || approval.WitnessEpoch <> witness.Identity.Epoch
                || approval.GrantRevision < 1L
                || not (ManagedCopySignerApprovalRead.canonicalMatches approval)
            then
                corrupt ()

            do!
                witnessProofAsync (fun () ->
                    witness.VerifyAuthorityEvidenceForInstallation(
                        approval.ApprovalId,
                        approval.WitnessSequence,
                        approval.WitnessEpoch,
                        approval.WitnessEntryHash,
                        approval.CandidateSha256,
                        ct
                    ))

            return approval
        }

    let verifyAll connection transaction witness cutoff (ct: CancellationToken) =
        task {
            let mutable after = Guid.Empty
            let mutable more = true
            let mutable count = 0L

            while more do
                let! ids = page connection transaction after ct

                for id in ids do
                    let! _ = verifyOne connection transaction witness cutoff id ct
                    count <- count + 1L

                match List.tryLast ids with
                | None -> more <- false
                | Some id -> after <- id

            return count
        }
