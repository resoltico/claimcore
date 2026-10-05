namespace ClaimCore.Database

open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness

module internal DatabaseRestoreFencedTailSignatures =
    let holder
        historical
        (owner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (report: RestoreReportClaims)
        (index: RestoreEvidenceIndex)
        (tail: FencedTailClaims)
        (evidence: SignedFencedTailEvidence)
        =
        task {
            let signer id purpose =
                if historical then
                    DatabaseRestoreSignedEvidence.historicalSigner
                        owner
                        transaction
                        witness
                        id
                        purpose
                        tail.Epoch
                        tail.W1Sequence
                else
                    System.Threading.Tasks.Task.FromResult(
                        DatabaseRestoreSignedEvidence.signer owner transaction id purpose
                    )

            let! reportKey, reportHolder =
                signer report.SignerKeyId CopySignerPurpose.RestoreReport

            try
                let! checkpointKey, checkpointHolder =
                    signer index.CheckpointSignerKeyId CopySignerPurpose.Checkpoint

                try
                    if
                        reportHolder = checkpointHolder
                        || CryptographicOperations.FixedTimeEquals(reportKey, checkpointKey)
                        || not (
                            ManagedCopySignature.verify
                                reportKey
                                evidence.Report
                                evidence.ReportSignature
                        )
                        || not (
                            ManagedCopySignature.verify
                                checkpointKey
                                evidence.Fence
                                evidence.FenceSignature
                        )
                        || not (
                            ManagedCopySignature.verify
                                checkpointKey
                                evidence.Supplement
                                evidence.SupplementSignature
                        )
                    then
                        invalidOp "Fenced recovery tail lacks an independent registered signer."

                    return checkpointHolder
                finally
                    CryptographicOperations.ZeroMemory(checkpointKey)
            finally
                CryptographicOperations.ZeroMemory(reportKey)
        }
