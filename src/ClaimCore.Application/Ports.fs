namespace ClaimCore.Application

open System
open System.Threading.Tasks
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type internal CoreFailure =
    | Domain of error: DomainError
    | ResourceUnavailable
    | InvalidCaseListCursor
    | IdempotencyConflict
    | StoreUnavailable
    | CommitOutcomeUnknown of operationId: Guid
    | StoreCorrupt
    | SchemaMismatch

/// A receipt describes one historical accepted operation, not necessarily the case's current version.
[<NoEquality; NoComparison>]
type internal Receipt =
    {
        OperationId: Guid
        Case: Claim
        RecordedAt: DateTimeOffset
        RecordedBy: string
        Replayed: bool
        CommandName: string
    }

[<NoEquality; NoComparison>]
type internal CasePage =
    {
        Items: Claim list
        NextCursor: string option
    }

[<NoEquality; NoComparison>]
type internal HistoryPage =
    {
        Items: Receipt list
        NextAfterVersion: int64 option
    }

/// One capture is an observed instant and the installation calendar derived from it. Keeping these
/// together prevents a preview from pairing a decision date with a separately observed host zone.
[<NoEquality; NoComparison>]
type internal BusinessContext =
    {
        ObservedUtcInstant: DateTimeOffset
        EffectiveBusinessDate: DateOnly
        TimeZoneId: string
    }

/// Read and exact-receipt observations. IRecoveryStore alone owns command mutation and its locks.
type internal IClaimStore =
    abstract Get: reference: string -> Task<Result<Claim option, CoreFailure>>
    abstract List: request: CaseListRequest -> Task<Result<CasePage, CoreFailure>>

    abstract History:
        reference: string * afterVersion: int64 -> Task<Result<HistoryPage, CoreFailure>>

    abstract Operation: operationId: Guid -> Task<Result<Receipt option, CoreFailure>>

    /// Verify the candidate's content identity before disclosing an accepted receipt.
    /// Accepted history outlives optional technical preparation retention.
    abstract Accepted:
        operationId: Guid * requestSha256: string -> Task<Result<Receipt option, CoreFailure>>

/// Runtime-owned authenticated encryption. Application defines what the token means; the runtime
/// owns nonce generation and an ephemeral key that is discarded on restart.
type internal ICaseListCursorProtection =
    abstract Seal: payload: byte array -> string
    abstract Open: token: string -> byte array option

type internal IBusinessTime =
    abstract Capture: unit -> BusinessContext
