namespace ClaimCore.ContractGeneration

open ClaimCore.Application
open ClaimCore.Contracts

[<RequireQualifiedAccess>]
module internal CliMutationCorpusSamples =
    let private sample = CliCorpusValues.sample

    let private submissionCompleted identifier execution settlement =
        CliCorpusValues.sample
            identifier
            "command.execute"
            (CliWireCodec.submission
                "command.execute"
                (SubmissionOutcome.Completed(
                    CliCorpusValues.preparationSummary,
                    CliCorpusValues.attemptId,
                    execution,
                    settlement
                )))

    let private submissionCompletions =
        let encode = CliWireCodec.submission "command.execute"

        [
            sample
                "command-execute-observed"
                "command.execute"
                (encode (SubmissionOutcome.ObservedAccepted CliCorpusValues.receipt))
            submissionCompleted
                "command-execute-completed-accepted"
                (DefiniteExecution.Accepted CliCorpusValues.receipt)
                SettlementConfirmation.Confirmed
            submissionCompleted
                "command-execute-completed-rejected"
                (DefiniteExecution.ExecutionRejected(
                    CliCorpusValues.operationId,
                    CliCorpusValues.rejection
                ))
                SettlementConfirmation.Unconfirmed
            submissionCompleted
                "command-execute-completed-revoked-before-execution"
                (DefiniteExecution.ExecutionRevokedBeforeExecution CliCorpusValues.operationId)
                SettlementConfirmation.Confirmed
            submissionCompleted
                "command-execute-completed-failed"
                (DefiniteExecution.FailedBeforeCommit(
                    CliCorpusValues.operationId,
                    CliCorpusValues.fault
                ))
                SettlementConfirmation.Confirmed
        ]

    let private submissionBeforeAttempt =
        let encode = CliWireCodec.submission "command.execute"

        [
            sample
                "command-execute-rejected-with-preparation"
                "command.execute"
                (encode (
                    SubmissionOutcome.RejectedBeforeAttempt(
                        Some CliCorpusValues.preparationSummary,
                        CliCorpusValues.rejection
                    )
                ))
            sample
                "command-execute-rejected-without-preparation"
                "command.execute"
                (encode (SubmissionOutcome.RejectedBeforeAttempt(None, CliCorpusValues.rejection)))
            sample
                "command-execute-failed-before-attempt"
                "command.execute"
                (encode (SubmissionOutcome.FailedBeforeAttempt(None, CliCorpusValues.fault)))
            sample
                "command-execute-preparation-state-unknown"
                "command.execute"
                (encode (
                    SubmissionOutcome.PreparationStateUnknown(
                        CliCorpusValues.operationId,
                        CliCorpusValues.digest,
                        CliCorpusValues.fault
                    )
                ))
        ]

    let private submissionAfterAttempt =
        let encode = CliWireCodec.submission "command.execute"

        [
            sample
                "command-execute-cancelled-before-admission"
                "command.execute"
                (encode (SubmissionOutcome.CancelledBeforeAdmission CliCorpusValues.operationId))
            sample
                "command-execute-cancelled-before-attempt"
                "command.execute"
                (encode (
                    SubmissionOutcome.CancelledBeforeAttempt CliCorpusValues.preparationSummary
                ))
            sample
                "command-execute-admission-unknown"
                "command.execute"
                (encode (
                    SubmissionOutcome.AttemptAdmissionUnknown(
                        CliCorpusValues.preparationSummary,
                        CliCorpusValues.fault
                    )
                ))
            sample
                "command-execute-unresolved"
                "command.execute"
                (encode (
                    SubmissionOutcome.AttemptUnresolved(
                        CliCorpusValues.preparationSummary,
                        CliCorpusValues.attemptId,
                        CliCorpusValues.fault
                    )
                ))
        ]

    let submission =
        submissionCompletions @ submissionBeforeAttempt @ submissionAfterAttempt

    let private resolveCompleted identifier execution settlement =
        CliCorpusValues.sample
            identifier
            "recovery.resolve"
            (CliWireCodec.resolve
                "recovery.resolve"
                (ResolveOutcome.ResolveCompleted(
                    CliCorpusValues.preparationSummary,
                    CliCorpusValues.attemptId,
                    execution,
                    settlement
                )))

    let private resolveCompletions =
        let encode = CliWireCodec.resolve "recovery.resolve"

        [
            sample
                "recovery-resolve-observed"
                "recovery.resolve"
                (encode (ResolveOutcome.ResolveObservedAccepted CliCorpusValues.receipt))
            resolveCompleted
                "recovery-resolve-completed-accepted"
                (DefiniteExecution.Accepted CliCorpusValues.receipt)
                SettlementConfirmation.Confirmed
            resolveCompleted
                "recovery-resolve-completed-rejected"
                (DefiniteExecution.ExecutionRejected(
                    CliCorpusValues.operationId,
                    CliCorpusValues.rejection
                ))
                SettlementConfirmation.Unconfirmed
            resolveCompleted
                "recovery-resolve-completed-revoked-before-execution"
                (DefiniteExecution.ExecutionRevokedBeforeExecution CliCorpusValues.operationId)
                SettlementConfirmation.Confirmed
            resolveCompleted
                "recovery-resolve-completed-failed"
                (DefiniteExecution.FailedBeforeCommit(
                    CliCorpusValues.operationId,
                    CliCorpusValues.fault
                ))
                SettlementConfirmation.Confirmed
        ]

    let private resolveBoundaries =
        let encode = CliWireCodec.resolve "recovery.resolve"

        [
            sample
                "recovery-resolve-refused"
                "recovery.resolve"
                (encode (
                    ResolveOutcome.RefusedBeforeAttempt(None, CliCorpusValues.recoveryRejection)
                ))
            sample
                "recovery-resolve-failed-before-attempt"
                "recovery.resolve"
                (encode (
                    ResolveOutcome.ResolveFailedBeforeAttempt(
                        Some CliCorpusValues.preparationSummary,
                        CliCorpusValues.fault
                    )
                ))
            sample
                "recovery-resolve-cancelled-before-admission"
                "recovery.resolve"
                (encode (ResolveOutcome.ResolveCancelledBeforeAdmission CliCorpusValues.operationId))
            sample
                "recovery-resolve-cancelled"
                "recovery.resolve"
                (encode (
                    ResolveOutcome.ResolveCancelledBeforeAttempt CliCorpusValues.preparationSummary
                ))
            sample
                "recovery-resolve-admission-unknown"
                "recovery.resolve"
                (encode (
                    ResolveOutcome.ResolveAttemptAdmissionUnknown(
                        CliCorpusValues.preparationSummary,
                        CliCorpusValues.fault
                    )
                ))
            sample
                "recovery-resolve-unresolved"
                "recovery.resolve"
                (encode (
                    ResolveOutcome.ResolveAttemptUnresolved(
                        CliCorpusValues.preparationSummary,
                        CliCorpusValues.attemptId,
                        CliCorpusValues.fault
                    )
                ))
        ]

    let resolve = resolveCompletions @ resolveBoundaries

    let private dismissTerminal encode =
        [
            sample
                "recovery-dismiss-dismissed"
                "recovery.dismiss"
                (encode (DismissedPreparation CliCorpusValues.preparationDetails))
            sample
                "recovery-dismiss-already-dismissed"
                "recovery.dismiss"
                (encode (AlreadyDismissedPreparation CliCorpusValues.preparationDetails))
            sample
                "recovery-dismiss-already-revoked"
                "recovery.dismiss"
                (encode (
                    AlreadyRevoked
                        {
                            OperationId = CliCorpusValues.operationId
                            RevokedAt = CliCorpusValues.timestamp
                            Reason = "Synthetic operator revocation."
                        }
                ))
            sample
                "recovery-dismiss-not-found"
                "recovery.dismiss"
                (encode (DismissNotFound CliCorpusValues.operationId))
        ]

    let private dismissNonterminal encode =
        [
            sample
                "recovery-dismiss-refused-with-details"
                "recovery.dismiss"
                (encode (
                    DismissRefused(
                        Some CliCorpusValues.preparationDetails,
                        CliCorpusValues.recoveryRejection
                    )
                ))
            sample
                "recovery-dismiss-refused-without-details"
                "recovery.dismiss"
                (encode (DismissRefused(None, CliCorpusValues.recoveryRejection)))
            sample
                "recovery-dismiss-failed"
                "recovery.dismiss"
                (encode (DismissFailed CliCorpusValues.fault))
            sample
                "recovery-dismiss-cancelled"
                "recovery.dismiss"
                (encode (DismissCancelledBeforeAdmission CliCorpusValues.operationId))
            sample
                "recovery-dismiss-state-unknown"
                "recovery.dismiss"
                (encode (
                    DismissStateUnknown(
                        CliCorpusValues.operationId,
                        CliCorpusValues.digest,
                        CliCorpusValues.fault
                    )
                ))
        ]

    let dismiss =
        let encode = CliWireCodec.dismiss "recovery.dismiss"
        dismissTerminal encode @ dismissNonterminal encode

    let all = submission @ resolve @ dismiss
