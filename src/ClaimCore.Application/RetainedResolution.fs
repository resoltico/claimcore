namespace ClaimCore.Application

open System

/// Internal result of resolving one retained canonical operation through separate attempt and
/// claim-commit durability boundaries.
[<NoEquality; NoComparison>]
type internal RetainedResolution =
    | ObservedReceipt of OperationReceipt
    | Resolved of PreparationSummary * Guid * DefiniteExecution * SettlementConfirmation
    | MissingPreparation of Guid
    | DismissedPreparation of PreparationSummary
    | DigestConflict
    | ReceiptIdentityConflict
    | ResolutionCancelledBeforeAdmission of Guid
    | ResolutionFailedBeforeAttempt of PreparationSummary option * CoreFault
    | ResolutionCancelledBeforeAttempt of PreparationSummary
    | ResolutionAdmissionUnknown of PreparationSummary * CoreFault
    | ResolutionUnresolved of PreparationSummary * Guid * CoreFault
