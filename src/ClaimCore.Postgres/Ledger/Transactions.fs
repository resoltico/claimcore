namespace ClaimCore.Postgres

open System
open System.IO
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

    let private timeoutExpressions =
        "set_config('lock_timeout',$1,true),set_config('statement_timeout',$2,true),set_config('idle_in_transaction_session_timeout',$3,true)"

    let private timeoutParameters settings =
        [|
            box $"{settings.LockMilliseconds}ms"
            box $"{settings.StatementMilliseconds}ms"
            box $"{settings.IdleMilliseconds}ms"
        |]

    let configure connection settings token =
        DatabaseSession.execute
            connection
            ("SELECT " + timeoutExpressions)
            (timeoutParameters settings)
            token

    let head connection token =
        task {
            FailureDiagnostics.phase FailurePhase.Head
            let started = System.Diagnostics.Stopwatch.GetTimestamp()

            let! rows =
                task {
                    try
                        return!
                            DatabaseSession.rows
                                connection
                                "SELECT sequence,entry_hash,observed_at FROM claimcore.lock_ledger_head()"
                                [||]
                                token
                    finally
                        Service.recordHeadWait (System.Diagnostics.Stopwatch.GetElapsedTime started)
                }

            match rows with
            | [ row ] ->
                return
                    StoredEntries.position
                        (DatabaseSession.value 0 row)
                        (DatabaseSession.value 1 row),
                    DatabaseSession.instant 2 row
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
            (error.SqlState = "CC001" || error.SqlState = "CC004")
            || (error.SqlState = "23505" && error.TableName = "ledger")
            ->
            StoreFailure.Integrity
        | :? PostgresException as error when
            error.SqlState.StartsWith("42", StringComparison.Ordinal)
            ->
            StoreFailure.SchemaMismatch
        | :? PostgresException -> StoreFailure.Unavailable
        | error -> providerFailure commitStarted error

    /// The commit marker is set only after all projection work and the final pre-commit cancellation check.
    let private transact source settings (token: CancellationToken) action =
        task {
            use _diagnostic = FailureDiagnostics.scope FailurePhase.Open
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
                                let! result, commit = action connection

                                if commit then
                                    token.ThrowIfCancellationRequested()
                                    FailureDiagnostics.phase FailurePhase.Commit
                                    commitStarted <- true
                                    do! transaction.CommitAsync(token)

                                return Ok result
                            })
            with error ->
                FailureDiagnostics.report error
                return Error(failure commitStarted error)
        }

    /// Application permits commit only for Proceed, including operational fences.
    let write source settings token action = transact source settings token action

    let read source token action =
        task {
            use _diagnostic = FailureDiagnostics.scope FailurePhase.Open

            try
                let! value =
                    DatabaseSession.read source token (fun connection ->
                        task {
                            FailureDiagnostics.phase FailurePhase.Read

                            let! rows =
                                DatabaseSession.rows
                                    connection
                                    ("SELECT " + timeoutExpressions + ",clock_timestamp()")
                                    (timeoutParameters timeouts)
                                    token

                            let observedAt = DatabaseSession.instant 3 rows.Head
                            return! action connection observedAt
                        })

                return Ok value
            with error ->
                FailureDiagnostics.report error
                return Error(failure false error)
        }
