namespace ClaimCore.Postgres

open System
open System.Threading
open System.IO
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open WitnessProtocolReconciliation

[<NoEquality; NoComparison>]
type internal StoredErasurePurge =
    {
        CaseId: Guid
        Phase: string
        RequestDenialCount: int64
        RequestDenialDigest: byte array
        EventId: Guid option
        ProposalCommitment: byte array option
        CanonicalAction: byte array option
        CandidateSha256: byte array option
        WitnessSequence: int64 option
        WitnessEpoch: int64 option
        WitnessHash: byte array option
    }

module internal CaseErasurePurgeRead =
    let private optional (reader: Data.Common.DbDataReader) index getter =
        if reader.IsDBNull(index) then None else Some(getter index)

    let find connection transaction caseId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT case_id,phase,denial_count,denial_set_sha256,"
                    + "purge_event_id,purge_proposal_commitment,"
                    + "purge_canonical_action,purge_candidate_sha256,purge_witness_sequence,"
                    + "purge_witness_epoch,purge_witness_entry_hash "
                    + "FROM claimcore.case_erasure_tombstones WHERE case_id=@case FOR UPDATE",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            use! reader = command.ExecuteReaderAsync()
            let! found = reader.ReadAsync()

            if not found then
                return None
            else
                let value =
                    {
                        CaseId = reader.GetGuid(0)
                        Phase = reader.GetString(1)
                        RequestDenialCount = reader.GetInt64(2)
                        RequestDenialDigest = reader.GetFieldValue<byte array>(3)
                        EventId = optional reader 4 reader.GetGuid
                        ProposalCommitment = optional reader 5 reader.GetFieldValue<byte array>
                        CanonicalAction = optional reader 6 reader.GetFieldValue<byte array>
                        CandidateSha256 = optional reader 7 reader.GetFieldValue<byte array>
                        WitnessSequence = optional reader 8 reader.GetInt64
                        WitnessEpoch = optional reader 9 reader.GetInt64
                        WitnessHash = optional reader 10 reader.GetFieldValue<byte array>
                    }

                if reader.Read() then
                    raise (InvalidDataException("Erasure tombstone is duplicated."))

                return Some value
        }

    let reconcile
        connection
        transaction
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (stored: StoredErasurePurge)
        eventId
        (canonicalDraft: byte array)
        ct
        =
        task {
            let expected = commitments.PurgeProposal canonicalDraft

            match
                stored.EventId,
                stored.ProposalCommitment,
                stored.CanonicalAction,
                stored.CandidateSha256,
                stored.WitnessSequence,
                stored.WitnessEpoch,
                stored.WitnessHash
            with
            | Some id,
              Some proposal,
              Some canonical,
              Some digest,
              Some sequence,
              Some epoch,
              Some hash when
                id = eventId
                && stored.Phase = "ERASURE_PENDING"
                && proposal = expected
                && digest = SHA256.HashData(canonical)
                ->
                do! CaseErasurePurgeDelete.verifyAbsent connection transaction stored.CaseId

                do! witness.ReconcileAuthority(id, sequence, epoch, hash, canonical, ct)

                do!
                    witness.VerifyAuthorityEvidenceForCase(
                        id,
                        sequence,
                        epoch,
                        hash,
                        digest,
                        stored.CaseId,
                        CancellationToken.None
                    )

                return true
            | _ -> return false
        }
