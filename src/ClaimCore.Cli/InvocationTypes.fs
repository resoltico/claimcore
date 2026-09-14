namespace ClaimCore.Cli

open System
open ClaimCore.Application

/// Typed CLI endpoint input after strict framing. Command input remains unbound until the adapter
/// calls the single Application binder at its narrow core boundary.
[<NoEquality; NoComparison; RequireQualifiedAccess>]
type EndpointInput =
    | Draft of CommandDraft
    | CaseReference of string
    | CaseList of afterReference: string option * limit: int
    | History of caseReference: string * cursor: string option * limit: int * detail: HistoryDetail
    | Operation of Guid
    | RecoveryPage of view: RecoveryListView * cursor: string option * limit: int
    | RecoveryInspect of operationId: Guid * attemptCursor: string option * attemptLimit: int
    | RecoveryResolve of operationId: Guid * requestSha256: string
    | RecoveryDismiss of operationId: Guid * requestSha256: string
    | RecoveryExport of operationId: Guid * requestSha256: string * destination: string
    | RecoveryImportPreview of source: string
    | RecoveryImportRetain of source: string * sourceSha256: string
