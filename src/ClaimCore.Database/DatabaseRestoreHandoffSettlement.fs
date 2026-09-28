namespace ClaimCore.Database

open ClaimCore.Postgres
open ClaimCore.Witness

/// Re-audits a settled W1 pair without pretending the historical pre-W1 report is current.
module internal DatabaseRestoreHandoffSettlement =
    let private readSettled barrier transaction (witness: WitnessProtocol) handoffId =
        let prepared =
            WriterHandoffOwnerRead.preparation barrier transaction witness handoffId
            |> Option.defaultWith (fun () -> invalidOp "Historical W1 PREPARE is unavailable.")

        let canonical, signature, _, _ =
            WriterHandoffOwnerRead.completed barrier transaction handoffId
            |> Option.defaultWith (fun () -> invalidOp "Historical W1 SETTLE is unavailable.")

        let value =
            WriterHandoffSettlement.parse canonical
            |> Option.defaultWith (fun () -> invalidOp "Historical W1 SETTLE bytes are invalid.")

        let ticket =
            WriterHandoffOwnerReconcile.trySettled
                barrier
                transaction
                witness
                prepared
                value
                canonical
                signature
            |> Option.defaultWith (fun () -> invalidOp "Independent W1 settlement is unavailable.")

        prepared, value, ticket

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
        let facts =
            DatabaseRestoreLive.inspect barrier transaction witnessOwner witness summary tip

        let prepared, value, ticket = readSettled barrier transaction witness handoffId

        DatabaseRestoreHandoffChecks.settled
            summary
            tip
            prepared
            value
            ticket
            handoffId
            (DatabaseRestoreHandoffChecks.digest files.Report)

        WriterHandoffCutoff.verify witness prepared.Value

        DatabaseRestoreHandoffChecks.historical
            publication
            report
            index
            facts
            prepared.Value
            prepared.Value.NewGeneration
            binarySha256

        DatabaseRestoreReportRecheck.signedEvidence
            barrier
            transaction
            witness
            files
            report
            index
            facts

        value, ticket

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
        if scope = "synthetic-only" then
            DatabaseRestoreProduceTarget.requireIsolated owner witnessAudit

        let report, index = DatabaseRestoreHandoffChecks.parsed publication scope files now

        let _, _, (value, ticket) =
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

        DatabaseRestoreHandoffChecks.result publication report index files, value, ticket
