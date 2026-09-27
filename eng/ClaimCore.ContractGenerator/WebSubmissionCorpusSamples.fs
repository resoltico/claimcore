namespace ClaimCore.ContractGeneration

open ClaimCore.Application
open ClaimCore.Contracts

module internal WebSubmissionCorpusSamples =
    let private sample suffix outcome =
        let endpoint = "command.execute"
        WebCorpusSamples.sample (endpoint + "-" + suffix) endpoint (WebWireCodec.submit outcome)

    let private completed execution settlement =
        SubmissionOutcome.Completed(
            CliCorpusValues.preparationSummary,
            CliCorpusValues.attemptId,
            execution,
            settlement
        )

    let private definite =
        [
            sample "observed" (SubmissionOutcome.ObservedAccepted WebCorpusSamples.replayedReceipt)
            sample
                "completed-accepted"
                (completed
                    (DefiniteExecution.Accepted CliCorpusValues.receipt)
                    SettlementConfirmation.Confirmed)
            sample
                "completed-rejected"
                (completed
                    (DefiniteExecution.ExecutionRejected(
                        CliCorpusValues.operationId,
                        WebCorpusSamples.rejectionWithField
                    ))
                    SettlementConfirmation.Unconfirmed)
            sample
                "completed-revoked-before-execution"
                (completed
                    (DefiniteExecution.ExecutionRevokedBeforeExecution CliCorpusValues.operationId)
                    SettlementConfirmation.Confirmed)
            sample
                "completed-failed"
                (completed
                    (DefiniteExecution.FailedBeforeCommit(
                        CliCorpusValues.operationId,
                        CliCorpusValues.fault
                    ))
                    SettlementConfirmation.Confirmed)
        ]

    let private refusals =
        [
            sample
                "refused-with-preparation"
                (SubmissionOutcome.RejectedBeforeAttempt(
                    Some CliCorpusValues.preparationSummary,
                    WebCorpusSamples.rejectionWithField
                ))
            sample
                "refused-without-preparation"
                (SubmissionOutcome.RejectedBeforeAttempt(None, Rejection.ResourceUnavailable))
            sample
                "failed-with-preparation"
                (SubmissionOutcome.FailedBeforeAttempt(
                    Some WebCorpusSamples.preparationWithoutDigest,
                    CliCorpusValues.fault
                ))
            sample
                "failed-without-preparation"
                (SubmissionOutcome.FailedBeforeAttempt(None, CliCorpusValues.fault))
        ]

    let private uncertain =
        [
            sample
                "cancelled-before-admission"
                (SubmissionOutcome.CancelledBeforeAdmission CliCorpusValues.operationId)
            sample
                "cancelled-before-attempt"
                (SubmissionOutcome.CancelledBeforeAttempt CliCorpusValues.preparationSummary)
            sample
                "admission-unknown"
                (SubmissionOutcome.AttemptAdmissionUnknown(
                    CliCorpusValues.preparationSummary,
                    CliCorpusValues.fault
                ))
            sample
                "unresolved"
                (SubmissionOutcome.AttemptUnresolved(
                    CliCorpusValues.preparationSummary,
                    CliCorpusValues.attemptId,
                    CliCorpusValues.fault
                ))
            sample
                "preparation-state-unknown"
                (SubmissionOutcome.PreparationStateUnknown(
                    CliCorpusValues.operationId,
                    CliCorpusValues.digest,
                    CliCorpusValues.fault
                ))
        ]

    let all = definite @ refusals @ uncertain
