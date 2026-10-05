namespace ClaimCore.Database

open System
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// Rechecks a signed pre-W1 report against a later complete audit. Only the two exact
/// owner approvals, and then the matching W1 PREPARE intent, may follow its cutoff.
module internal DatabaseRestoreHandoffRecheck =
    let private auditAtTip
        publication
        report
        index
        files
        proposal
        witnessOwner
        binarySha256
        expectedSequence
        expectedHash
        pending
        barrier
        transaction
        witness
        summary
        tip
        =
        task {
            let! facts =
                if pending then
                    DatabaseRestoreLive.inspectForHandoff
                        barrier
                        transaction
                        witnessOwner
                        witness
                        summary
                        tip
                else
                    DatabaseRestoreLive.inspect barrier transaction witnessOwner witness summary tip

            if
                tip.TipSequence <> expectedSequence
                || Convert.ToHexStringLower(tip.TipHash) <> expectedHash
                || (not pending && facts.PendingIntents <> 0L)
            then
                invalidOp "Current W1 authority tip differs from reviewed chain."

            do!
                DatabaseRestoreHandoffChecks.auditedPair
                    publication
                    report
                    index
                    files
                    proposal
                    binarySha256
                    facts
                    barrier
                    transaction
                    witness

            return facts
        }

    let private audit
        publication
        scope
        owner
        witnessAudit
        witnessOwner
        custody
        suppression
        (files: RestoreReportFiles)
        (proposal: WriterHandoffPreparation)
        binarySha256
        expectedSequence
        expectedHash
        pending
        now
        =
        task {
            let report, index = DatabaseRestoreHandoffChecks.parsed publication scope files now

            if
                DatabaseRestoreHandoffChecks.digest files.Report
                <> Convert.ToHexStringLower(proposal.RestoreReportSha256)
            then
                invalidOp "Historical report digest differs from W1 proposal."

            let! _, _, facts =
                DatabaseVerifyData.auditedRestoredWith
                    owner
                    witnessAudit
                    custody
                    suppression
                    (fun barrier transaction witness summary tip ->
                        auditAtTip
                            publication
                            report
                            index
                            files
                            proposal
                            witnessOwner
                            binarySha256
                            expectedSequence
                            expectedHash
                            pending
                            barrier
                            transaction
                            witness
                            summary
                            tip)

            if facts.WitnessCutoff <> expectedSequence then
                invalidOp "Audited W1 cutoff changed during recheck."

            return DatabaseRestoreHandoffChecks.result publication report index files
        }

    let preparation
        publication
        scope
        owner
        witnessAudit
        witnessOwner
        custody
        suppression
        files
        proposal
        binarySha256
        now
        =
        task {
            if scope = "synthetic-only" then
                DatabaseRestoreIsolation.requireIsolated owner witnessAudit

            return!
                audit
                    publication
                    scope
                    owner
                    witnessAudit
                    witnessOwner
                    custody
                    suppression
                    files
                    proposal
                    binarySha256
                    proposal.ExpectedTipSequence
                    (Convert.ToHexStringLower(proposal.ExpectedTipHash))
                    false
                    now
        }

    let settlement
        publication
        scope
        owner
        witnessAudit
        witnessOwner
        custody
        suppression
        files
        (prepared: PrimaryWriterPreparation)
        binarySha256
        now
        =
        task {
            if scope = "synthetic-only" then
                DatabaseRestoreIsolation.requireIsolated owner witnessAudit

            return!
                audit
                    publication
                    scope
                    owner
                    witnessAudit
                    witnessOwner
                    custody
                    suppression
                    files
                    prepared.Value
                    binarySha256
                    prepared.Intent.Sequence
                    (Convert.ToHexStringLower(prepared.Intent.EntryHash))
                    true
                    now
        }

    let afterSettlement
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
        DatabaseRestoreHandoffSettlement.recheck
            publication
            scope
            owner
            witnessAudit
            witnessOwner
            custody
            suppression
            files
            handoffId
            binarySha256
            now
