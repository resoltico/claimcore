namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open DataAuditCommon

module internal CaseTombstonePruneReceiptAudit =
    let private proposal (receipt: StoredWitnessPruneReceipt) =
        {
            EventId = receipt.EventId
            CaseId = receipt.CaseId
            PurgeEventId = receipt.PurgeEventId
            PurgeWitnessSequence = receipt.PurgeWitnessSequence
            PurgeWitnessEpoch = receipt.PurgeWitnessEpoch
            PurgeWitnessHash = Convert.ToHexStringLower receipt.PurgeWitnessHash
            CutoffSequence = receipt.CutoffSequence
            CutoffHash = Convert.ToHexStringLower receipt.CutoffHash
            TargetCount = receipt.TargetCount
            TargetDigest = Convert.ToHexStringLower receipt.TargetDigest
            ExpectedAuthorityRevision = receipt.AuthorityRevision
            ExpectedAuthorityHash = Convert.ToHexStringLower receipt.AuthorityHash
            ValidUntil = receipt.ValidUntil
        }

    let private canonical
        connection
        transaction
        (witness: WitnessProtocol)
        (receipt: StoredWitnessPruneReceipt)
        =
        task {
            let value = proposal receipt

            if
                not (CaseTombstonePruneOwnerChecks.validProposal value)
                || receipt.CopyInventoryDigest.Length <> 32
                || receipt.IntentEpoch <> witness.Identity.Epoch
                || receipt.IntentSequence <= receipt.CutoffSequence
            then
                corrupt ()

            let! approvals =
                CaseTombstonePruneOwnerApprovals.read
                    connection
                    transaction
                    witness
                    0L
                    value
                    false
                    receipt.ValidUntil

            let bytes =
                CaseTombstonePruneExecutionCandidate.encode
                    value
                    receipt.CopyInventoryDigest
                    approvals

            try
                if
                    bytes <> receipt.Canonical || receipt.CandidateHash <> SHA256.HashData(bytes)
                then
                    corrupt ()

                witnessProof (fun () ->
                    witness.VerifyAuthorityEvidenceForCase(
                        receipt.EventId,
                        receipt.IntentSequence,
                        receipt.IntentEpoch,
                        receipt.IntentHash,
                        receipt.CandidateHash,
                        receipt.CaseId
                    ))

                CaseTombstonePrunePostcutoffAudit.verify witness receipt approvals
                return ()
            finally
                CryptographicOperations.ZeroMemory(bytes)
        }

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        caseId
        =
        task {
            let! found = CaseTombstonePrunePrimaryRead.find connection transaction caseId

            match found with
            | None -> return None
            | Some receipt ->
                if receipt.IntentSequence > cutoff then
                    corrupt ()

                do! canonical connection transaction witness receipt

                let! count =
                    CaseTombstonePruneTargetAudit.verify connection transaction witness receipt

                if count <> receipt.TargetCount then
                    corrupt ()

                return Some receipt.CutoffSequence
        }
