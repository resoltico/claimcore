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

    let private requireReceipt
        (witness: WitnessProtocol)
        (receipt: StoredWitnessPruneReceipt)
        value
        =
        if
            not (CaseTombstonePruneOwnerChecks.validProposal value)
            || receipt.CopyInventoryDigest.Length <> 32
            || receipt.IntentEpoch <> witness.Identity.Epoch
            || receipt.IntentSequence <= receipt.CutoffSequence
        then
            corrupt ()


    let private canonical
        connection
        transaction
        (witness: WitnessProtocol)
        (receipt: StoredWitnessPruneReceipt)
        ct
        =
        task {
            let value = proposal receipt

            requireReceipt witness receipt value

            let! approvals =
                CaseTombstonePruneOwnerApprovals.read
                    connection
                    transaction
                    witness
                    0L
                    value
                    false
                    receipt.ValidUntil
                    ct

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

                do!
                    witnessProofAsync (fun () ->
                        witness.VerifyAuthorityEvidenceForCase(
                            receipt.EventId,
                            receipt.IntentSequence,
                            receipt.IntentEpoch,
                            receipt.IntentHash,
                            receipt.CandidateHash,
                            receipt.CaseId,
                            ct
                        ))

                do! CaseTombstonePrunePostcutoffAudit.verify witness receipt approvals ct
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
        ct
        =
        task {
            let! found = CaseTombstonePrunePrimaryRead.find connection transaction caseId

            match found with
            | None -> return None
            | Some receipt ->
                if receipt.IntentSequence > cutoff then
                    corrupt ()

                do! canonical connection transaction witness receipt ct

                let! count =
                    CaseTombstonePruneTargetAudit.verify connection transaction witness receipt ct

                if count <> receipt.TargetCount then
                    corrupt ()

                return Some receipt.CutoffSequence
        }
