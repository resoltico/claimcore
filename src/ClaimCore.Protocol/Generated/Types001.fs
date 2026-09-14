// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

[<NoComparison>]
type PreparationSummary =
    {
        OperationId: string
        CaseReference: string
        Command: string
        PreparedAt: string
        State: string
        RequestSha256: (string) option
        AvailableActions: (string) list
    }

[<NoComparison>]
type PreparationDetailsAuthoredValuesItem = { Name: string; Value: string }

[<NoComparison>]
type PreparationDetailsAttemptsItem =
    {
        AttemptId: string
        StartedAt: string
        Settlement: (string) option
        SettledAt: (string) option
    }

[<NoComparison>]
type PreparationDetails =
    {
        Summary: PreparationSummary
        ExpectedRevision: string
        AuthoredValues: (PreparationDetailsAuthoredValuesItem) list
        CanonicalCommandFormat: int64
        PreparingApplicationVersion: string
        PreparingContractFingerprint: string
        PreparingContractKind: string
        Attempts: (PreparationDetailsAttemptsItem) list
        LegacyUncertainty: bool
    }

[<NoComparison>]
type RecoveryDetailsObservationFound = { Tag: string; Value: Receipt }

[<NoComparison>]
type RecoveryDetailsObservationNotFound = { Tag: string; Identity: string }

[<RequireQualifiedAccess; NoComparison>]
type RecoveryDetailsObservation =
    | Found of RecoveryDetailsObservationFound
    | NotFound of RecoveryDetailsObservationNotFound

[<NoComparison>]
type RecoveryDetails =
    {
        Preparation: PreparationDetails
        Observation: RecoveryDetailsObservation
    }

[<NoComparison>]
type RecoveryImportPreviewDecodedEffect =
    {
        OperationId: string
        CaseReference: string
        Command: string
        ExpectedRevision: string
        AuthoredValues: (PreparationDetailsAuthoredValuesItem) list
        CanonicalCommandFormat: int64
        RequestSha256: string
    }

[<NoComparison>]
type RecoveryImportPreview =
    {
        ArtifactKind: string
        SourceSha256: string
        DecodedEffect: RecoveryImportPreviewDecodedEffect
        ExistingPreparation: (PreparationSummary) option
    }

[<NoComparison>]
type RecoveryRejection =
    {
        Code: string
        Message: string
        RecommendedAction: string
    }

[<NoComparison>]
type SessionSnapshot =
    {
        Authenticated: bool
        AntiforgeryToken: (string) option
    }

[<NoComparison>]
type SessionResponseOutcome = { Tag: string; Data: SessionSnapshot }

[<NoComparison>]
type SessionResponse =
    {
        Endpoint: string
        Outcome: SessionResponseOutcome
    }

[<NoComparison>]
type SessionLoginResponse =
    {
        Endpoint: string
        Outcome: SessionResponseOutcome
    }

[<NoComparison>]
type SessionLoginRequest =
    {
        Credential: string
        AntiforgeryToken: string
    }

[<NoComparison>]
type SessionLogoutResponse =
    {
        Endpoint: string
        Outcome: SessionResponseOutcome
    }

type SessionLogoutRequest = unit

[<NoComparison>]
type DefinitionResponseOutcome =
    { Tag: string; Data: DefinitionPayload }

[<NoComparison>]
type DefinitionResponse =
    {
        Endpoint: string
        Outcome: DefinitionResponseOutcome
    }

[<NoComparison>]
type CaseGetResponseOutcomeSucceededDataFound = { Tag: string; Current: CurrentCase }

[<NoComparison>]
type CaseGetResponseOutcomeSucceededDataNotFound = { Tag: string; CaseReference: string }

[<RequireQualifiedAccess; NoComparison>]
type CaseGetResponseOutcomeSucceededData =
    | Found of CaseGetResponseOutcomeSucceededDataFound
    | NotFound of CaseGetResponseOutcomeSucceededDataNotFound

[<NoComparison>]
type CaseGetResponseOutcomeSucceeded =
    {
        Tag: string
        Data: CaseGetResponseOutcomeSucceededData
    }

[<NoComparison>]
type CaseGetResponseOutcomeRejected = { Tag: string; Data: Rejection }

[<NoComparison>]
type CaseGetResponseOutcomeFailed = { Tag: string; Data: Fault }

[<NoComparison>]
type CaseGetResponseOutcomeCancelled = { Tag: string; Data: unit }

[<RequireQualifiedAccess; NoComparison>]
type CaseGetResponseOutcome =
    | Succeeded of CaseGetResponseOutcomeSucceeded
    | Rejected of CaseGetResponseOutcomeRejected
    | Failed of CaseGetResponseOutcomeFailed
    | Cancelled of CaseGetResponseOutcomeCancelled

[<NoComparison>]
type CaseGetResponse =
    {
        Endpoint: string
        Outcome: CaseGetResponseOutcome
    }

[<NoComparison>]
type CaseGetRequest = { CaseReference: string }

[<NoComparison>]
type CaseListResponseOutcomeSucceededData =
    {
        Items: (CaseSummary) list
        NextCursor: (string) option
    }

[<NoComparison>]
type CaseListResponseOutcomeSucceeded =
    {
        Tag: string
        Data: CaseListResponseOutcomeSucceededData
    }

[<RequireQualifiedAccess; NoComparison>]
type CaseListResponseOutcome =
    | Succeeded of CaseListResponseOutcomeSucceeded
    | Rejected of CaseGetResponseOutcomeRejected
    | Failed of CaseGetResponseOutcomeFailed
    | Cancelled of CaseGetResponseOutcomeCancelled

[<NoComparison>]
type CaseListResponse =
    {
        Endpoint: string
        Outcome: CaseListResponseOutcome
    }

[<NoComparison>]
type CaseListRequest =
    {
        Cursor: (string) option
        Limit: int64
    }

[<NoComparison>]
type CaseHistoryResponseOutcomeSucceededDataFound =
    {
        Tag: string
        Entries: (HistoryEntry) list
        NextCursor: (string) option
    }

[<RequireQualifiedAccess; NoComparison>]
type CaseHistoryResponseOutcomeSucceededData =
    | Found of CaseHistoryResponseOutcomeSucceededDataFound
    | NotFound of CaseGetResponseOutcomeSucceededDataNotFound

[<NoComparison>]
type CaseHistoryResponseOutcomeSucceeded =
    {
        Tag: string
        Data: CaseHistoryResponseOutcomeSucceededData
    }

[<RequireQualifiedAccess; NoComparison>]
type CaseHistoryResponseOutcome =
    | Succeeded of CaseHistoryResponseOutcomeSucceeded
    | Rejected of CaseGetResponseOutcomeRejected
    | Failed of CaseGetResponseOutcomeFailed
    | Cancelled of CaseGetResponseOutcomeCancelled

[<NoComparison>]
type CaseHistoryResponse =
    {
        Endpoint: string
        Outcome: CaseHistoryResponseOutcome
    }

[<NoComparison>]
type CaseHistoryRequest =
    {
        CaseReference: string
        Cursor: (string) option
        Limit: int64
        Detail: string
    }

[<NoComparison>]
type OperationObserveResponseOutcomeSucceededDataFound = { Tag: string; Receipt: Receipt }

[<NoComparison>]
type OperationObserveResponseOutcomeSucceededDataNotFound = { Tag: string; OperationId: string }

[<RequireQualifiedAccess; NoComparison>]
type OperationObserveResponseOutcomeSucceededData =
    | Found of OperationObserveResponseOutcomeSucceededDataFound
    | NotFound of OperationObserveResponseOutcomeSucceededDataNotFound
