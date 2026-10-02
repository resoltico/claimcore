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
        member _.Get(reference) =
            ActorReadStore.get dataSource witness actorContext reference

        member _.List(request) =
            ActorReadStore.list dataSource witness actorContext cursorProtection request

        member _.History(reference, afterVersion) =
            ActorReadStore.history dataSource witness actorContext reference afterVersion

        member _.Operation(operationId) =
            ActorOperationReadStore.operation dataSource witness actorContext operationId

        member _.Accepted(operationId, requestSha256) =
            ActorOperationReadStore.accepted
                dataSource
                witness
                actorContext
                operationId
                requestSha256

    interface IDisposable with
        member _.Dispose() = ()
