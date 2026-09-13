namespace ClaimCore.ContractGeneration

open ClaimCore.Application
open ClaimCore.Contracts

[<RequireQualifiedAccess>]
module internal WebMutationCorpusSamples =
    let private sample = WebCorpusSamples.sample

    let private prepareDefinite =
        let endpoint = "command.prepare"
        let encode = WebWireCodec.prepare

        [
            sample
                "command-prepare-prepared"
                endpoint
                (encode (
                    PrepareOutcome.Prepared(
                        CliCorpusValues.preparationDetails,
                        CliCorpusValues.review
                    )
                ))
            sample
                "command-prepare-prepared-nullables"
                endpoint
                (encode (
                    PrepareOutcome.Prepared(
                        WebCorpusSamples.detailsWithNullableValues,
                        WebCorpusSamples.reviewWithNullableValues
                    )
                ))
            sample
                "command-prepare-rejected"
                endpoint
                (encode (
                    PrepareOutcome.PrepareRejected(
                        CliCorpusValues.operationId,
                        WebCorpusSamples.rejectionWithField
                    )
                ))
            sample
                "command-prepare-failed"
                endpoint
                (encode (
                    PrepareOutcome.PrepareFailed(CliCorpusValues.operationId, CliCorpusValues.fault)
                ))
        ]

    let private prepareExactRetries =
        let endpoint = "command.prepare"
        let encode = WebWireCodec.prepare

        [
            sample
                "command-prepare-observed-accepted"
                endpoint
                (encode (
                    PrepareOutcome.ObservedAccepted(
                        CliCorpusValues.preparationDetails,
                        CliCorpusValues.observedReceipt
                    )
                ))
            sample
                "command-prepare-retained-for-recovery"
                endpoint
                (encode (
                    PrepareOutcome.RetainedForRecovery(
                        CliCorpusValues.preparationDetails,
                        CliCorpusValues.rejection
                    )
                ))
        ]

    let private prepareUncertain =
        let endpoint = "command.prepare"
        let encode = WebWireCodec.prepare

        [
            sample
                "command-prepare-cancelled"
                endpoint
                (encode (PrepareOutcome.CancelledBeforeAdmission WebCorpusSamples.alphaOperationId))
            sample
                "command-prepare-state-unknown"
                endpoint
                (encode (
                    PrepareOutcome.PreparationStateUnknown(
                        CliCorpusValues.operationId,
                        CliCorpusValues.digest,
                        CliCorpusValues.fault
                    )
                ))
        ]

    let prepare = prepareDefinite @ prepareExactRetries @ prepareUncertain

    let private completed endpoint identifier execution settlement =
        sample
            (endpoint + "-" + identifier)
            endpoint
            (WebWireCodec.resolve
                endpoint
                (ResolveOutcome.ResolveCompleted(
                    CliCorpusValues.preparationSummary,
                    CliCorpusValues.attemptId,
                    execution,
                    settlement
                )))

    let private resolveCompletions endpoint =
        let encode = WebWireCodec.resolve endpoint

        [
            sample
                (endpoint + "-observed")
                endpoint
                (encode (ResolveOutcome.ResolveObservedAccepted WebCorpusSamples.replayedReceipt))
            completed
                endpoint
                "completed-accepted"
                (DefiniteExecution.Accepted CliCorpusValues.receipt)
                SettlementConfirmation.Confirmed
            completed
                endpoint
                "completed-rejected"
                (DefiniteExecution.ExecutionRejected(
                    CliCorpusValues.operationId,
                    WebCorpusSamples.rejectionWithField
                ))
                SettlementConfirmation.Unconfirmed
            completed
                endpoint
                "completed-failed"
                (DefiniteExecution.FailedBeforeCommit(
                    CliCorpusValues.operationId,
                    CliCorpusValues.fault
                ))
                SettlementConfirmation.Confirmed
        ]

    let private resolveRefusals endpoint =
        let encode = WebWireCodec.resolve endpoint

        [
            sample
                (endpoint + "-refused-with-preparation")
                endpoint
                (encode (
                    ResolveOutcome.RefusedBeforeAttempt(
                        Some CliCorpusValues.preparationSummary,
                        CliCorpusValues.recoveryRejection
                    )
                ))
            sample
                (endpoint + "-refused-without-preparation")
                endpoint
                (encode (
                    ResolveOutcome.RefusedBeforeAttempt(None, CliCorpusValues.recoveryRejection)
                ))
            sample
                (endpoint + "-failed-with-preparation")
                endpoint
                (encode (
                    ResolveOutcome.ResolveFailedBeforeAttempt(
                        Some WebCorpusSamples.preparationWithoutDigest,
                        CliCorpusValues.fault
                    )
                ))
            sample
                (endpoint + "-failed-without-preparation")
                endpoint
                (encode (ResolveOutcome.ResolveFailedBeforeAttempt(None, CliCorpusValues.fault)))
        ]

    let private resolveUnknown endpoint =
        let encode = WebWireCodec.resolve endpoint

        [
            sample
                (endpoint + "-cancelled-before-admission")
                endpoint
                (encode (ResolveOutcome.ResolveCancelledBeforeAdmission CliCorpusValues.operationId))
            sample
                (endpoint + "-cancelled-before-attempt")
                endpoint
                (encode (
                    ResolveOutcome.ResolveCancelledBeforeAttempt CliCorpusValues.preparationSummary
                ))
            sample
                (endpoint + "-admission-unknown")
                endpoint
                (encode (
                    ResolveOutcome.ResolveAttemptAdmissionUnknown(
                        CliCorpusValues.preparationSummary,
                        CliCorpusValues.fault
                    )
                ))
            sample
                (endpoint + "-unresolved")
                endpoint
                (encode (
                    ResolveOutcome.ResolveAttemptUnresolved(
                        CliCorpusValues.preparationSummary,
                        CliCorpusValues.attemptId,
                        CliCorpusValues.fault
                    )
                ))
        ]

    let private resolve endpoint =
        resolveCompletions endpoint @ resolveRefusals endpoint @ resolveUnknown endpoint

    let private dismissDefinite =
        let endpoint = "recovery.dismiss"
        let encode = WebWireCodec.recoveryDismiss

        [
            sample
                "recovery-dismiss-dismissed"
                endpoint
                (encode (
                    RecoveryDismissOutcome.DismissedPreparation CliCorpusValues.preparationDetails
                ))
            sample
                "recovery-dismiss-already-dismissed"
                endpoint
                (encode (
                    RecoveryDismissOutcome.AlreadyDismissedPreparation
                        WebCorpusSamples.detailsWithNullableValues
                ))
            sample
                "recovery-dismiss-not-found"
                endpoint
                (encode (RecoveryDismissOutcome.DismissNotFound CliCorpusValues.operationId))
            sample
                "recovery-dismiss-refused-with-details"
                endpoint
                (encode (
                    RecoveryDismissOutcome.DismissRefused(
                        Some CliCorpusValues.preparationDetails,
                        CliCorpusValues.recoveryRejection
                    )
                ))
            sample
                "recovery-dismiss-refused-without-details"
                endpoint
                (encode (
                    RecoveryDismissOutcome.DismissRefused(None, CliCorpusValues.recoveryRejection)
                ))
        ]

    let private dismissFailures =
        let endpoint = "recovery.dismiss"
        let encode = WebWireCodec.recoveryDismiss

        [
            sample
                "recovery-dismiss-failed"
                endpoint
                (encode (RecoveryDismissOutcome.DismissFailed CliCorpusValues.fault))
            sample
                "recovery-dismiss-cancelled"
                endpoint
                (encode (
                    RecoveryDismissOutcome.DismissCancelledBeforeAdmission
                        CliCorpusValues.operationId
                ))
            sample
                "recovery-dismiss-state-unknown"
                endpoint
                (encode (
                    RecoveryDismissOutcome.DismissStateUnknown(
                        CliCorpusValues.operationId,
                        CliCorpusValues.digest,
                        CliCorpusValues.fault
                    )
                ))
        ]

    let dismiss = dismissDefinite @ dismissFailures

    let all = prepare @ resolve "command.execute" @ resolve "recovery.resolve" @ dismiss
