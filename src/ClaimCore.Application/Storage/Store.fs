namespace ClaimCore.Application.Storage

open ClaimCore.Application.Reads

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<NoEquality; NoComparison>]
type internal ReadOperation<'result> =
    {
        Work: ReadTx -> Task<'result>
        CancellationToken: CancellationToken
    }

[<NoEquality; NoComparison>]
type internal WriteOperation<'result> =
    {
        Work: WriteTx -> Task<Verdict<'result>>
        CancellationToken: CancellationToken
    }

/// Context cache writes are non-ledger facts. Implementations serialize the principal before
/// taking the access snapshot and refresh a request only when that snapshot has no actor.
[<NoComparison>]
type internal ContextQuery =
    {
        Principal: PrincipalKey
        Claims: RequesterClaims
        CancellationToken: CancellationToken
    }

type internal ContextFacts =
    {
        Facts: GlobalReadFacts
        ObservedAt: DateTimeOffset
    }

type internal IStore =
    abstract Context: ContextQuery -> Task<Result<ContextFacts, StoreFailure>>
    abstract Read: ReadOperation<'result> -> Task<Result<'result, StoreFailure>>

    abstract RecordDisclosure:
        DisclosureEvent * CancellationToken -> Task<Result<unit, StoreFailure>>

    abstract Write: WriteOperation<'result> -> Task<Result<Verdict<'result>, StoreFailure>>
