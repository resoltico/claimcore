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

type internal IBusinessDate =
    abstract Today: unit -> DateOnly
