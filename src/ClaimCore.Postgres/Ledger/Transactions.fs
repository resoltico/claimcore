namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application

type internal SessionTimeouts =
    {
        LockMilliseconds: int
        StatementMilliseconds: int
        IdleMilliseconds: int
    }

module internal Transactions =
    let timeouts =
        {
            LockMilliseconds = 5000
            StatementMilliseconds = 30000
            IdleMilliseconds = 15000
        }

    let configure connection settings token =
        DatabaseSession.execute
            connection
            "SELECT set_config('lock_timeout',$1,true),set_config('statement_timeout',$2,true),set_config('idle_in_transaction_session_timeout',$3,true)"
            [|
                box $"{settings.LockMilliseconds}ms"
                box $"{settings.StatementMilliseconds}ms"
                box $"{settings.IdleMilliseconds}ms"
            |]
            token

    let head connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT sequence,entry_hash,observed_at FROM claimcore.lock_ledger_head()"
                    [||]
                    token

            match rows with
            | [ row ] ->
                return
                    ({
                        Sequence = DatabaseSession.value 0 row
                        EntryHash = DatabaseSession.value 1 row
                    }
                    : LedgerHead),
                    DatabaseSession.instant 2 row,
                    RandomNumberGenerator.GetBytes 32
            | _ -> return raise (RecordCorruptException "Ledger head is absent.")
        }

    let private providerFailure commitStarted (error: exn) =
        match error with
        | :? RecordCorruptException -> StoreFailure.Integrity
        | :? OperationCanceledException when not commitStarted -> StoreFailure.Cancelled
        | :? OperationCanceledException -> StoreFailure.CommitOutcomeUnknown
        | :? NpgsqlException
        | :? IOException
        | :? TimeoutException ->
            if commitStarted then
                StoreFailure.CommitOutcomeUnknown
            else
                StoreFailure.Unavailable
        | _ -> StoreFailure.Internal

    let failure commitStarted (error: exn) =
        match error with
        | :? PostgresException as error when commitStarted && DatabaseSession.commitUncertain error ->
            StoreFailure.CommitOutcomeUnknown
        | :? PostgresException as error when
            error.SqlState = "CC001"
            || (error.SqlState = "23505" && error.TableName = "ledger")
            ->
            StoreFailure.Integrity
        | :? PostgresException as error when
            error.SqlState.StartsWith("42", StringComparison.Ordinal)
            ->
            StoreFailure.SchemaMismatch
        | :? PostgresException -> StoreFailure.Unavailable
        | error -> providerFailure commitStarted error

    [<RequireQualifiedAccess>]
    type private CommitDisposition =
        | NoWrite
        | LedgerAppend
        | OperationalWrite

    /// The commit marker is set only after all projection work and the final pre-commit cancellation check.
    let private transact source settings (token: CancellationToken) action =
        task {
            let mutable commitStarted = false

            try
                return!
                    DatabaseSession.transaction
                        source
                        Data.IsolationLevel.ReadCommitted
                        token
                        (fun connection transaction ->
                            task {
                                let! _ = configure connection settings token
                                let! result, disposition = action connection

                                if disposition <> CommitDisposition.NoWrite then
                                    token.ThrowIfCancellationRequested()
                                    commitStarted <- true
                                    do! transaction.CommitAsync(token)

                                return Ok result
                            })
            with error ->
                return Error(failure commitStarted error)
        }

    /// Only the accepted-ledger path supplies append permission.
    let write source settings token action =
        transact source settings token (fun connection ->
            task {
                let! result, append = action connection

                return
                    result,
                    (if append then
                         CommitDisposition.LedgerAppend
                     else
                         CommitDisposition.NoWrite)
            })

    [<RequireQualifiedAccess>]
    type OperationalDecision<'Result> =
        | ReadOnly of 'Result
        | Persist of 'Result

    /// Operational confirmation is independent of ledger append and never signals business acceptance.
    let operational source settings token action =
        transact source settings token (fun connection ->
            task {
                let! decision = action connection

                return
                    match decision with
                    | OperationalDecision.ReadOnly result -> result, CommitDisposition.NoWrite
                    | OperationalDecision.Persist result ->
                        result, CommitDisposition.OperationalWrite
            })

    let read source token action =
        task {
            try
                let! value =
                    DatabaseSession.read source token (fun connection ->
                        task {
                            let! _ = configure connection timeouts token
                            return! action connection
                        })

                return Ok value
            with error ->
                return Error(failure false error)
        }
