namespace ClaimCore.Database

open System.Threading
open ClaimCore.Postgres
open ClaimCore.Witness

/// Re-audits a settled W1 pair without pretending the historical pre-W1 report is current.
module internal DatabaseRestoreHandoffSettlement =
    let private readSettled barrier transaction (witness: WitnessProtocol) handoffId =
        task {
            let! retained =
                WriterHandoffOwnerRead.preparation
                    barrier
                    transaction
                    witness
                    handoffId
                    CancellationToken.None

            let prepared =
                retained
                |> Option.defaultWith (fun () -> invalidOp "Historical W1 PREPARE is unavailable.")

            let! completed =
                WriterHandoffOwnerRead.completed
                    barrier
                    transaction
                    handoffId
                    CancellationToken.None

            let canonical, signature, _, _ =
                completed
                |> Option.defaultWith (fun () -> invalidOp "Historical W1 SETTLE is unavailable.")

            let value =
                WriterHandoffSettlement.parse canonical
                |> Option.defaultWith (fun () ->
                    invalidOp "Historical W1 SETTLE bytes are invalid.")

            let! settled =
                WriterHandoffOwnerReconcile.trySettled
                    barrier
                    transaction
                    witness
                    prepared
                    value
                    canonical
                    signature
                    CancellationToken.None

            let ticket =
                settled
                |> Option.defaultWith (fun () ->
                    invalidOp "Independent W1 settlement is unavailable.")

            return prepared, value, ticket
        }

    let private atTip
        (publication: TrustedRestorePublication)
        (report: RestoreReportClaims)
        (index: RestoreEvidenceIndex)
        (files: RestoreReportFiles)
        witnessOwner
        handoffId
        binarySha256
        barrier
        transaction
        witness
        summary
        tip
        =
        task {
            let! facts =
                DatabaseRestoreLive.inspect barrier transaction witnessOwner witness summary tip

            let! prepared, value, ticket = readSettled barrier transaction witness handoffId

            DatabaseRestoreHandoffChecks.settled
                summary
                tip
                prepared
                value
                ticket
                handoffId
                (DatabaseRestoreHandoffChecks.digest files.Report)

            do! WriterHandoffCutoff.verify witness prepared.Value CancellationToken.None

            DatabaseRestoreHandoffChecks.historical
                publication
                report
                index
                facts
                prepared.Value
                prepared.Value.NewGeneration
                binarySha256

            do!
                DatabaseRestoreReportRecheck.signedEvidence
                    barrier
                    transaction
                    witness
                    files
                    report
                    index
                    facts

            return value, ticket
        }

    let recheck
        publication
        scope
        owner
        witnessAudit
        witnessOwner
        custody
        suppression
        (files: RestoreReportFiles)
        handoffId
        binarySha256
        now
        =
        task {
            if scope = "synthetic-only" then
                DatabaseRestoreIsolation.requireIsolated owner witnessAudit

            let report, index = DatabaseRestoreHandoffChecks.parsed publication scope files now

            let! _, _, (value, ticket) =
                DatabaseVerifyData.auditedRestoredWith
                    owner
                    witnessAudit
                    custody
                    suppression
                    (fun barrier transaction witness summary tip ->
                        atTip
                            publication
                            report
                            index
                            files
                            witnessOwner
                            handoffId
                            binarySha256
                            barrier
                            transaction
                            witness
                            summary
                            tip)

            return DatabaseRestoreHandoffChecks.result publication report index files, value, ticket
        }
