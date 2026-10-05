namespace ClaimCore.Database

open System
open System.IO
open System.Security.Cryptography
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// Historical pre-W1 authority and current W1/tail facts are checked in separate,
/// non-nested stable audits. This local proof alone never activates real-data case work.
module internal DatabaseFencedTailQualification =
    let private binaryDigest () =
        let path = typeof<DatabaseCommand>.Assembly.Location

        if String.IsNullOrWhiteSpace path then
            invalidOp "Published owner verifier binary is unavailable."

        use binary = File.OpenRead(path)
        SHA256.HashData(binary) |> Convert.ToHexStringLower

    let private currentAudit
        (owner: string)
        (witnessAudit: string)
        (custody: IKeyCustody)
        (suppression: SuppressionKeyFile)
        (loaded: LoadedFencedTail)
        (historical: HandoffReportEvidence)
        (ticket: Ticket)
        (expectedProbeSha: string)
        (probeEvidenceSha: string)
        (configuredArchiveRoot: string)
        (now: DateTimeOffset)
        =
        task {
            let! _, _, proof =
                DatabaseVerifyData.auditedRestoredWith
                    owner
                    witnessAudit
                    custody
                    suppression
                    (fun barrier transaction witness summary tip ->
                        if
                            summary.PendingIntents <> 0L
                            || tip.TipSequence <> ticket.Sequence
                            || tip.TipHash <> ticket.EntryHash
                        then
                            invalidOp "Final WAL tail current authority differs from W1."

                        DatabaseRestoreFencedTailVerification.verify
                            barrier
                            transaction
                            witness
                            historical.Report
                            historical.Index
                            expectedProbeSha
                            probeEvidenceSha
                            configuredArchiveRoot
                            loaded.Evidence
                            now)

            return proof
        }

    let private requireSettlement
        (settlement: WriterHandoffSettlement)
        (ticket: Ticket)
        (tail: FencedTailClaims)
        =
        if
            settlement.HandoffId <> tail.HandoffId
            || ticket.Sequence <> tail.W1Sequence
            || Convert.ToHexStringLower(ticket.EntryHash) <> tail.W1Hash
        then
            invalidOp "Signed final WAL tail differs from historical W1."


    let verify
        (publication: TrustedRestorePublication)
        (scope: string)
        (owner: string)
        (witnessAudit: string)
        (witnessOwner: string)
        (custody: IKeyCustody)
        (suppression: SuppressionKeyFile)
        (loaded: LoadedFencedTail)
        (expectedProbeSha: string)
        (probeEvidenceSha: string)
        (configuredArchiveRoot: string)
        (now: DateTimeOffset)
        =
        task {
            let tail =
                DatabaseRestoreFencedTailClaims.parse loaded.Evidence.Supplement now
                |> Option.defaultWith (fun () -> invalidOp "Signed final WAL tail is invalid.")

            let! historical, settlement, ticket =
                DatabaseRestoreHandoffRecheck.afterSettlement
                    publication
                    scope
                    owner
                    witnessAudit
                    witnessOwner
                    custody
                    suppression
                    loaded.ReportFiles
                    tail.HandoffId
                    (binaryDigest ())
                    now

            requireSettlement settlement ticket tail

            return!
                currentAudit
                    owner
                    witnessAudit
                    custody
                    suppression
                    loaded
                    historical
                    ticket
                    expectedProbeSha
                    probeEvidenceSha
                    configuredArchiveRoot
                    now
        }
