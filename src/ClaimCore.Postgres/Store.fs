namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Application

/// The relational case adapter is created only for one admitted actor call. Startup and schema
/// diagnostics use RuntimeDatabase directly; there is no unbound case-work read or write path.
type internal PostgresStore
    (
        dataSource: NpgsqlDataSource,
        witness: WitnessProtocol,
        actorContext: ActorCallContext,
        cursorProtection: ICaseListCursorProtection
    ) =
    interface IClaimStore with
        member _.Get(reference, ct) =
            ActorReadStore.get dataSource witness actorContext reference ct

        member _.List(request, ct) =
            ActorReadStore.list dataSource witness actorContext cursorProtection request ct

        member _.History(reference, afterVersion, ct) =
            ActorReadStore.history dataSource witness actorContext reference afterVersion ct

        member _.Operation(operationId, ct) =
            ActorOperationReadStore.operation dataSource witness actorContext operationId ct

        member _.Accepted(operationId, requestSha256, ct) =
            ActorOperationReadStore.accepted
                dataSource
                witness
                actorContext
                operationId
                requestSha256
                ct

    interface IDisposable with
        member _.Dispose() = ()
