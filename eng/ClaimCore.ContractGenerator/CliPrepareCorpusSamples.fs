namespace ClaimCore.ContractGeneration

open ClaimCore.Application
open ClaimCore.Contracts

[<RequireQualifiedAccess>]
module internal CliPrepareCorpusSamples =
    let private sample = CliCorpusValues.sample
    let private encode = CliWireCodec.prepare "command.prepare"

    let private definite =
        [
            sample
                "command-prepare-prepared"
                "command.prepare"
                (encode (Prepared(CliCorpusValues.preparationDetails, CliCorpusValues.review)))
            sample
                "command-prepare-observed-accepted"
                "command.prepare"
                (encode (PrepareOutcome.ObservedAccepted CliCorpusValues.observedReceipt))
            sample
                "command-prepare-retained-for-recovery"
                "command.prepare"
                (encode (
                    PrepareOutcome.RetainedForRecovery(
                        CliCorpusValues.preparationDetails,
                        CliCorpusValues.rejection
                    )
                ))
            sample
                "command-prepare-rejected"
                "command.prepare"
                (encode (PrepareRejected(CliCorpusValues.operationId, CliCorpusValues.rejection)))
            sample
                "command-prepare-failed"
                "command.prepare"
                (encode (PrepareFailed(CliCorpusValues.operationId, CliCorpusValues.fault)))
        ]

    let private uncertain =
        [
            sample
                "command-prepare-cancelled"
                "command.prepare"
                (encode (PrepareOutcome.CancelledBeforeAdmission CliCorpusValues.operationId))
            sample
                "command-prepare-state-unknown"
                "command.prepare"
                (encode (
                    PrepareOutcome.PreparationStateUnknown(
                        CliCorpusValues.operationId,
                        CliCorpusValues.digest,
                        CliCorpusValues.fault
                    )
                ))
        ]

    let all = definite @ uncertain
