// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

[<NoComparison>]
type OperationObserveResponseOutcomeSucceeded =
    {
        Tag: string
        Data: OperationObserveResponseOutcomeSucceededData
    }

[<RequireQualifiedAccess; NoComparison>]
type OperationObserveResponseOutcome =
    | Succeeded of OperationObserveResponseOutcomeSucceeded
    | Rejected of CaseGetResponseOutcomeRejected
    | Failed of CaseGetResponseOutcomeFailed
    | Cancelled of CaseGetResponseOutcomeCancelled

[<NoComparison>]
type OperationObserveResponse =
    {
        Endpoint: string
        Outcome: OperationObserveResponseOutcome
    }

[<NoComparison>]
type OperationObserveRequest = { OperationId: string }

[<NoComparison>]
type CommandPrepareResponseOutcomePreparedData =
    {
        Details: PreparationDetails
        Review: AdvisoryReview
    }

[<NoComparison>]
type CommandPrepareResponseOutcomePrepared =
    {
        Tag: string
        Data: CommandPrepareResponseOutcomePreparedData
    }

[<NoComparison>]
type CommandPrepareResponseOutcomeObservedAcceptedData =
    {
        Details: PreparationDetails
        Receipt: Receipt
    }

[<NoComparison>]
type CommandPrepareResponseOutcomeObservedAccepted =
    {
        Tag: string
        Data: CommandPrepareResponseOutcomeObservedAcceptedData
    }

[<NoComparison>]
type CommandPrepareResponseOutcomeRetainedForRecoveryData =
    {
        Details: PreparationDetails
        Rejection: Rejection
    }

[<NoComparison>]
type CommandPrepareResponseOutcomeRetainedForRecovery =
    {
        Tag: string
        Data: CommandPrepareResponseOutcomeRetainedForRecoveryData
    }

[<NoComparison>]
type CommandPrepareResponseOutcomeRejectedData =
    {
        OperationId: string
        Rejection: Rejection
    }

[<NoComparison>]
type CommandPrepareResponseOutcomeRejected =
    {
        Tag: string
        Data: CommandPrepareResponseOutcomeRejectedData
    }

[<NoComparison>]
type CommandPrepareResponseOutcomeFailedData = { OperationId: string; Fault: Fault }

[<NoComparison>]
type CommandPrepareResponseOutcomeFailed =
    {
        Tag: string
        Data: CommandPrepareResponseOutcomeFailedData
    }

[<NoComparison>]
type CommandPrepareResponseOutcomeCancelledBeforeAdmission =
    {
        Tag: string
        Data: OperationObserveRequest
    }

[<NoComparison>]
type CommandPrepareResponseOutcomePreparationStateUnknownData =
    {
        OperationId: string
        RequestSha256: string
        Fault: Fault
    }

[<NoComparison>]
type CommandPrepareResponseOutcomePreparationStateUnknown =
    {
        Tag: string
        Data: CommandPrepareResponseOutcomePreparationStateUnknownData
    }

[<RequireQualifiedAccess; NoComparison>]
type CommandPrepareResponseOutcome =
    | Prepared of CommandPrepareResponseOutcomePrepared
    | ObservedAccepted of CommandPrepareResponseOutcomeObservedAccepted
    | RetainedForRecovery of CommandPrepareResponseOutcomeRetainedForRecovery
    | Rejected of CommandPrepareResponseOutcomeRejected
    | Failed of CommandPrepareResponseOutcomeFailed
    | CancelledBeforeAdmission of CommandPrepareResponseOutcomeCancelledBeforeAdmission
    | PreparationStateUnknown of CommandPrepareResponseOutcomePreparationStateUnknown

[<NoComparison>]
type CommandPrepareResponse =
    {
        Endpoint: string
        Outcome: CommandPrepareResponseOutcome
    }

[<NoComparison>]
type CommandPrepareRequestCommandOpenValues =
    {
        IncidentDate: string
        IncidentNotificationDate: string
        IncidentCountry: string
        ClaimantName: string
        InsurerName: string
        ClaimedAmount: string
        ClaimedCurrency: string
    }

[<NoComparison>]
type CommandPrepareRequestCommandOpen =
    {
        Kind: string
        Values: CommandPrepareRequestCommandOpenValues
    }

[<NoComparison>]
type CommandPrepareRequestCommandAmendRegistration =
    {
        Kind: string
        Values: CommandPrepareRequestCommandOpenValues
    }

[<NoComparison>]
type CommandPrepareRequestCommandDecideValues =
    {
        PaymentDecisionDate: string
        PayableAmount: string
        PayableCurrency: string
    }

[<NoComparison>]
type CommandPrepareRequestCommandDecide =
    {
        Kind: string
        Values: CommandPrepareRequestCommandDecideValues
    }

[<NoComparison>]
type CommandPrepareRequestCommandWithdrawDecision =
    {
        Kind: string
        Values: SessionLogoutRequest
    }

[<NoComparison>]
type CommandPrepareRequestCommandRecordPaymentValues = { PaymentDate: string }

[<NoComparison>]
type CommandPrepareRequestCommandRecordPayment =
    {
        Kind: string
        Values: CommandPrepareRequestCommandRecordPaymentValues
    }

[<NoComparison>]
type CommandPrepareRequestCommandClearPayment =
    {
        Kind: string
        Values: SessionLogoutRequest
    }

[<NoComparison>]
type CommandPrepareRequestCommandClose =
    {
        Kind: string
        Values: SessionLogoutRequest
    }

[<NoComparison>]
type CommandPrepareRequestCommandReopen =
    {
        Kind: string
        Values: SessionLogoutRequest
    }

[<RequireQualifiedAccess; NoComparison>]
type CommandPrepareRequestCommand =
    | Open of CommandPrepareRequestCommandOpen
    | AmendRegistration of CommandPrepareRequestCommandAmendRegistration
    | Decide of CommandPrepareRequestCommandDecide
    | WithdrawDecision of CommandPrepareRequestCommandWithdrawDecision
    | RecordPayment of CommandPrepareRequestCommandRecordPayment
    | ClearPayment of CommandPrepareRequestCommandClearPayment
    | Close of CommandPrepareRequestCommandClose
    | Reopen of CommandPrepareRequestCommandReopen

[<NoComparison>]
type CommandPrepareRequest =
    {
        OperationId: string
        CaseReference: string
        ExpectedRevision: string
        Command: CommandPrepareRequestCommand
    }

[<NoComparison>]
type CommandExecuteResponseOutcomeObservedAcceptedData = { Receipt: Receipt }

[<NoComparison>]
type CommandExecuteResponseOutcomeObservedAccepted =
    {
        Tag: string
        Data: CommandExecuteResponseOutcomeObservedAcceptedData
    }

[<NoComparison>]
type CommandExecuteResponseOutcomeCompletedData =
    {
        Preparation: PreparationSummary
        AttemptId: string
        Execution: DefiniteExecution
        Settlement: string
    }

[<NoComparison>]
type CommandExecuteResponseOutcomeCompleted =
    {
        Tag: string
        Data: CommandExecuteResponseOutcomeCompletedData
    }

[<NoComparison>]
type CommandExecuteResponseOutcomeRefusedBeforeAttemptData =
    {
        Preparation: (PreparationSummary) option
        Rejection: RecoveryRejection
    }

[<NoComparison>]
type CommandExecuteResponseOutcomeRefusedBeforeAttempt =
    {
        Tag: string
        Data: CommandExecuteResponseOutcomeRefusedBeforeAttemptData
    }

[<NoComparison>]
type CommandExecuteResponseOutcomeFailedBeforeAttemptData =
    {
        Preparation: (PreparationSummary) option
        Fault: Fault
    }
