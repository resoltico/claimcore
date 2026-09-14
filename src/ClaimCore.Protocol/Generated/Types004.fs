// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

[<RequireQualifiedAccess; NoComparison>]
type RecoveryImportEnvelopeRetainResponseOutcome =
    | Retained of RecoveryImportEnvelopeRetainResponseOutcomeRetained
    | Existing of RecoveryImportEnvelopeRetainResponseOutcomeExisting
    | Rejected of RecoveryListResponseOutcomeRejected
    | Failed of CaseGetResponseOutcomeFailed
    | CancelledBeforeAdmission of
        RecoveryImportEnvelopeRetainResponseOutcomeCancelledBeforeAdmission
    | RetainStateUnknown of RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknown

[<NoComparison>]
type RecoveryImportEnvelopeRetainResponse =
    {
        Endpoint: string
        Outcome: RecoveryImportEnvelopeRetainResponseOutcome
    }

[<NoComparison>]
type RecoveryImportEnvelopeRetainHeaders = { XClaimCoreSourceSha256: string }

[<NoComparison>]
type RecoveryImportRecordPreviewResponse =
    {
        Endpoint: string
        Outcome: RecoveryImportEnvelopePreviewResponseOutcome
    }

[<NoComparison>]
type RecoveryImportRecordRetainResponse =
    {
        Endpoint: string
        Outcome: RecoveryImportEnvelopeRetainResponseOutcome
    }
