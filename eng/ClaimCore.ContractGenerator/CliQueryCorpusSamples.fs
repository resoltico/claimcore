namespace ClaimCore.ContractGeneration

open ClaimCore.Application
open ClaimCore.Contracts

[<RequireQualifiedAccess>]
module internal CliQueryCorpusSamples =
    let private sample = CliCorpusValues.sample

    let caseGet =
        let encode = CliWireCodec.caseGet "case.get"

        [
            sample
                "case-get-found"
                "case.get"
                (encode (Succeeded(Found CliCorpusValues.currentCase)))
            sample
                "case-get-found-four-byte-boundary"
                "case.get"
                (encode (Succeeded(Found CliCorpusValues.fourByteCurrentCase)))
            sample
                "case-get-found-scalar-boundaries"
                "case.get"
                (encode (Succeeded(Found CliCorpusValues.scalarBoundaryCurrentCase)))
            sample
                "case-get-not-found"
                "case.get"
                (encode (Succeeded(NotFound CliCorpusValues.fields.CaseReference)))
            sample "case-get-rejected" "case.get" (encode (Rejected CliCorpusValues.rejection))
            sample "case-get-failed" "case.get" (encode (Failed CliCorpusValues.fault))
            sample "case-get-cancelled" "case.get" (encode Cancelled)
        ]

    let caseList =
        let encode = CliWireCodec.caseList "case.list"

        [
            sample
                "case-list-succeeded"
                "case.list"
                (encode (
                    Succeeded
                        {
                            Items = [ CliCorpusValues.caseSummary ]
                            NextAfterReference = Some CliCorpusValues.fields.CaseReference
                        }
                ))
            sample "case-list-rejected" "case.list" (encode (Rejected CliCorpusValues.rejection))
            sample "case-list-failed" "case.list" (encode (Failed CliCorpusValues.fault))
            sample "case-list-cancelled" "case.list" (encode Cancelled)
        ]

    let caseHistory =
        let encode = CliWireCodec.caseHistory "case.history"

        [
            sample
                "case-history-found"
                "case.history"
                (encode (
                    Succeeded(
                        Found
                            {
                                Entries =
                                    [
                                        HistoryEntry.SummaryEntry CliCorpusValues.change
                                        HistoryEntry.FullEntry CliCorpusValues.receipt
                                    ]
                                NextCursor = Some "synthetic-history-cursor"
                            }
                    )
                ))
            sample
                "case-history-not-found"
                "case.history"
                (encode (Succeeded(NotFound CliCorpusValues.fields.CaseReference)))
            sample
                "case-history-rejected"
                "case.history"
                (encode (Rejected CliCorpusValues.rejection))
            sample "case-history-failed" "case.history" (encode (Failed CliCorpusValues.fault))
            sample "case-history-cancelled" "case.history" (encode Cancelled)
        ]

    let operation =
        let encode = CliWireCodec.operationObserve "operation.observe"

        [
            sample
                "operation-observe-found"
                "operation.observe"
                (encode (Succeeded(Found CliCorpusValues.receipt)))
            sample
                "operation-observe-not-found"
                "operation.observe"
                (encode (Succeeded(NotFound CliCorpusValues.operationId)))
            sample
                "operation-observe-rejected"
                "operation.observe"
                (encode (Rejected CliCorpusValues.rejection))
            sample
                "operation-observe-failed"
                "operation.observe"
                (encode (Failed CliCorpusValues.fault))
            sample "operation-observe-cancelled" "operation.observe" (encode Cancelled)
        ]

    let recoveryList =
        let encode = CliWireCodec.recoveryList "recovery.list"

        [
            sample
                "recovery-list-succeeded"
                "recovery.list"
                (encode (
                    RecoverySucceeded
                        {
                            View = RecoveryListView.Pending
                            Items = [ RetainedRecoveryItem CliCorpusValues.preparationSummary ]
                            NextCursor = Some "synthetic-recovery-cursor"
                            PendingPreparationCount = 1
                            PendingCanonicalRequestBytes = 128L
                            MaximumPendingPreparations = 1024
                            MaximumPendingCanonicalRequestBytes = 67108864L
                            NearCapacity = false
                        }
                ))
            sample
                "recovery-list-terminal-revocation"
                "recovery.list"
                (encode (
                    RecoverySucceeded
                        {
                            View = RecoveryListView.Terminal
                            Items = [ RevokedRecoveryItem CliCorpusValues.revokedOperation ]
                            NextCursor = None
                            PendingPreparationCount = 0
                            PendingCanonicalRequestBytes = 0L
                            MaximumPendingPreparations = 1024
                            MaximumPendingCanonicalRequestBytes = 67108864L
                            NearCapacity = false
                        }
                ))
            sample
                "recovery-list-rejected"
                "recovery.list"
                (encode (RecoveryRejected CliCorpusValues.recoveryRejection))
            sample
                "recovery-list-failed"
                "recovery.list"
                (encode (RecoveryFailed CliCorpusValues.fault))
            sample "recovery-list-cancelled" "recovery.list" (encode RecoveryCancelled)
        ]

    let recoveryInspect =
        let encode = CliWireCodec.recoveryInspect "recovery.inspect"

        let details observation =
            {
                Preparation = CliCorpusValues.preparationDetails
                Observation = observation
            }

        [
            sample
                "recovery-inspect-found-observed"
                "recovery.inspect"
                (encode (
                    RecoverySucceeded(
                        Found(RetainedInspection(details (Found CliCorpusValues.receipt)))
                    )
                ))
            sample
                "recovery-inspect-found-unobserved"
                "recovery.inspect"
                (encode (
                    RecoverySucceeded(
                        Found(RetainedInspection(details (NotFound CliCorpusValues.operationId)))
                    )
                ))
            sample
                "recovery-inspect-found-revoked"
                "recovery.inspect"
                (encode (
                    RecoverySucceeded(Found(RevokedInspection CliCorpusValues.revokedOperation))
                ))
            sample
                "recovery-inspect-not-found"
                "recovery.inspect"
                (encode (RecoverySucceeded(NotFound CliCorpusValues.operationId)))
            sample
                "recovery-inspect-rejected"
                "recovery.inspect"
                (encode (RecoveryRejected CliCorpusValues.recoveryRejection))
            sample
                "recovery-inspect-failed"
                "recovery.inspect"
                (encode (RecoveryFailed CliCorpusValues.fault))
            sample "recovery-inspect-cancelled" "recovery.inspect" (encode RecoveryCancelled)
        ]

    let all =
        caseGet @ caseList @ caseHistory @ operation @ recoveryList @ recoveryInspect
