namespace ClaimCore.Application

open System
open System.Threading.Tasks
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type internal CoreFailure =
    | Domain of error: DomainError
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
        NextAfter: string option
    }

[<NoEquality; NoComparison>]
type internal HistoryPage =
    {
        Items: Receipt list
        NextAfterVersion: int64 option
    }

/// Storage owns locks/commit, not business decisions. It invokes decide while holding the case lock.
type internal IClaimStore =
    abstract Transact:
        operation: PreparedOperation * decide: (Claim option -> Result<Claim, DomainError>) ->
            Task<Result<Receipt, CoreFailure>>

    abstract Get: reference: string -> Task<Result<Claim option, CoreFailure>>
    abstract List: after: string option -> Task<Result<CasePage, CoreFailure>>

    abstract History:
        reference: string * afterVersion: int64 -> Task<Result<HistoryPage, CoreFailure>>

    abstract Operation: operationId: Guid -> Task<Result<Receipt option, CoreFailure>>

    /// Verify the candidate's content identity before disclosing an accepted receipt.
    /// Accepted history outlives optional technical preparation retention.
    abstract Accepted:
        operationId: Guid * requestSha256: string -> Task<Result<Receipt option, CoreFailure>>

/// One capture is an observed instant and the installation calendar derived from it. Keeping these
/// together prevents a preview from pairing a decision date with a separately observed host zone.
[<NoEquality; NoComparison>]
type internal BusinessContext =
    {
        ObservedUtcInstant: DateTimeOffset
        EffectiveBusinessDate: DateOnly
        TimeZoneId: string
    }

type internal IBusinessTime =
    abstract Capture: unit -> BusinessContext
