// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

[<NoComparison>]
type CommandExecuteResponseOutcomeFailedBeforeAttempt =
    {
        Tag: string
        Data: CommandExecuteResponseOutcomeFailedBeforeAttemptData
    }

[<NoComparison>]
type CommandExecuteResponseOutcomeCancelledBeforeAttemptData = { Preparation: PreparationSummary }

[<NoComparison>]
type CommandExecuteResponseOutcomeCancelledBeforeAttempt =
    {
        Tag: string
        Data: CommandExecuteResponseOutcomeCancelledBeforeAttemptData
    }

[<NoComparison>]
type CommandExecuteResponseOutcomeAttemptAdmissionUnknownData =
    {
        Preparation: PreparationSummary
        Fault: Fault
    }

[<NoComparison>]
type CommandExecuteResponseOutcomeAttemptAdmissionUnknown =
    {
        Tag: string
        Data: CommandExecuteResponseOutcomeAttemptAdmissionUnknownData
    }

[<NoComparison>]
type CommandExecuteResponseOutcomeAttemptUnresolvedData =
    {
        Preparation: PreparationSummary
        AttemptId: string
        Fault: Fault
    }

[<NoComparison>]
type CommandExecuteResponseOutcomeAttemptUnresolved =
    {
        Tag: string
        Data: CommandExecuteResponseOutcomeAttemptUnresolvedData
    }

[<RequireQualifiedAccess; NoComparison>]
type CommandExecuteResponseOutcome =
    | ObservedAccepted of CommandExecuteResponseOutcomeObservedAccepted
    | Completed of CommandExecuteResponseOutcomeCompleted
    | RefusedBeforeAttempt of CommandExecuteResponseOutcomeRefusedBeforeAttempt
    | FailedBeforeAttempt of CommandExecuteResponseOutcomeFailedBeforeAttempt
    | CancelledBeforeAdmission of CommandPrepareResponseOutcomeCancelledBeforeAdmission
    | CancelledBeforeAttempt of CommandExecuteResponseOutcomeCancelledBeforeAttempt
    | AttemptAdmissionUnknown of CommandExecuteResponseOutcomeAttemptAdmissionUnknown
    | AttemptUnresolved of CommandExecuteResponseOutcomeAttemptUnresolved

[<NoComparison>]
type CommandExecuteResponse =
    {
        Endpoint: string
        Outcome: CommandExecuteResponseOutcome
    }

[<NoComparison>]
type CommandExecuteRequest =
    {
        OperationId: string
        RequestSha256: string
    }

[<NoComparison>]
type RecoveryListResponseOutcomeSucceededData =
    {
        Items: (PreparationSummary) list
        NextCursor: (string) option
    }

[<NoComparison>]
type RecoveryListResponseOutcomeSucceeded =
    {
        Tag: string
        Data: RecoveryListResponseOutcomeSucceededData
    }

[<NoComparison>]
type RecoveryListResponseOutcomeRejected =
    { Tag: string; Data: RecoveryRejection }

[<RequireQualifiedAccess; NoComparison>]
type RecoveryListResponseOutcome =
    | Succeeded of RecoveryListResponseOutcomeSucceeded
    | Rejected of RecoveryListResponseOutcomeRejected
    | Failed of CaseGetResponseOutcomeFailed
    | Cancelled of CaseGetResponseOutcomeCancelled

[<NoComparison>]
type RecoveryListResponse =
    {
        Endpoint: string
        Outcome: RecoveryListResponseOutcome
    }

[<NoComparison>]
type RecoveryInspectResponseOutcomeSucceededDataFound = { Tag: string; Value: RecoveryDetails }

[<RequireQualifiedAccess; NoComparison>]
type RecoveryInspectResponseOutcomeSucceededData =
    | Found of RecoveryInspectResponseOutcomeSucceededDataFound
    | NotFound of RecoveryDetailsObservationNotFound

[<NoComparison>]
type RecoveryInspectResponseOutcomeSucceeded =
    {
        Tag: string
        Data: RecoveryInspectResponseOutcomeSucceededData
    }

[<RequireQualifiedAccess; NoComparison>]
type RecoveryInspectResponseOutcome =
    | Succeeded of RecoveryInspectResponseOutcomeSucceeded
    | Rejected of RecoveryListResponseOutcomeRejected
    | Failed of CaseGetResponseOutcomeFailed
    | Cancelled of CaseGetResponseOutcomeCancelled

[<NoComparison>]
type RecoveryInspectResponse =
    {
        Endpoint: string
        Outcome: RecoveryInspectResponseOutcome
    }

[<NoComparison>]
type RecoveryResolveResponse =
    {
        Endpoint: string
        Outcome: CommandExecuteResponseOutcome
    }

[<NoComparison>]
type RecoveryDismissResponseOutcomeDismissed =
    {
        Tag: string
        Data: PreparationDetails
    }

[<NoComparison>]
type RecoveryDismissResponseOutcomeAlreadyDismissed =
    {
        Tag: string
        Data: PreparationDetails
    }

[<NoComparison>]
type RecoveryDismissResponseOutcomeNotFound =
    {
        Tag: string
        Data: OperationObserveRequest
    }

[<NoComparison>]
type RecoveryDismissResponseOutcomeRefusedData =
    {
        Details: (PreparationDetails) option
        Rejection: RecoveryRejection
    }

[<NoComparison>]
type RecoveryDismissResponseOutcomeRefused =
    {
        Tag: string
        Data: RecoveryDismissResponseOutcomeRefusedData
    }

[<NoComparison>]
type RecoveryDismissResponseOutcomeDismissStateUnknown =
    {
        Tag: string
        Data: CommandPrepareResponseOutcomePreparationStateUnknownData
    }

[<RequireQualifiedAccess; NoComparison>]
type RecoveryDismissResponseOutcome =
    | Dismissed of RecoveryDismissResponseOutcomeDismissed
    | AlreadyDismissed of RecoveryDismissResponseOutcomeAlreadyDismissed
    | NotFound of RecoveryDismissResponseOutcomeNotFound
    | Refused of RecoveryDismissResponseOutcomeRefused
    | Failed of CaseGetResponseOutcomeFailed
    | CancelledBeforeAdmission of CommandPrepareResponseOutcomeCancelledBeforeAdmission
    | DismissStateUnknown of RecoveryDismissResponseOutcomeDismissStateUnknown

[<NoComparison>]
type RecoveryDismissResponse =
    {
        Endpoint: string
        Outcome: RecoveryDismissResponseOutcome
    }

[<NoComparison>]
type RecoveryDismissRequest =
    {
        OperationId: string
        RequestSha256: string
        Confirmed: bool
    }

[<RequireQualifiedAccess; NoComparison>]
type RecoveryExportResponseOutcome =
    | NotFound of RecoveryDismissResponseOutcomeNotFound
    | Rejected of RecoveryListResponseOutcomeRejected
    | Failed of CaseGetResponseOutcomeFailed
    | Cancelled of CaseGetResponseOutcomeCancelled

[<NoComparison>]
type RecoveryExportResponse =
    {
        Endpoint: string
        Outcome: RecoveryExportResponseOutcome
    }

[<NoComparison>]
type RecoveryImportEnvelopePreviewResponseOutcomeSucceeded =
    {
        Tag: string
        Data: RecoveryImportPreview
    }

[<RequireQualifiedAccess; NoComparison>]
type RecoveryImportEnvelopePreviewResponseOutcome =
    | Succeeded of RecoveryImportEnvelopePreviewResponseOutcomeSucceeded
    | Rejected of RecoveryListResponseOutcomeRejected
    | Failed of CaseGetResponseOutcomeFailed
    | Cancelled of CaseGetResponseOutcomeCancelled

[<NoComparison>]
type RecoveryImportEnvelopePreviewResponse =
    {
        Endpoint: string
        Outcome: RecoveryImportEnvelopePreviewResponseOutcome
    }

[<NoComparison>]
type RecoveryImportEnvelopeRetainResponseOutcomeRetained =
    {
        Tag: string
        Data: PreparationDetails
    }

[<NoComparison>]
type RecoveryImportEnvelopeRetainResponseOutcomeExisting =
    {
        Tag: string
        Data: PreparationDetails
    }

[<NoComparison>]
type RecoveryImportEnvelopeRetainResponseOutcomeCancelledBeforeAdmission =
    { Tag: string; Data: unit }

[<NoComparison>]
type RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknownData =
    {
        ArtifactKind: string
        SourceSha256: string
        OperationId: (string) option
        Fault: Fault
    }

[<NoComparison>]
type RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknown =
    {
        Tag: string
        Data: RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknownData
    }
