namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Application.VerificationChecks
open Npgsql

/// Deterministic precedence: ledger order with link/hash/commitment/format checks first, then
/// history and permanent authority rules; erasure, projections, head and checkpoint follow.
module internal Verification =
    let private scanEntry installationId expected (state: LedgerVerification.Scan) row =
        result {
            let! sequence =
                VerificationRows.capture None VerificationFinding.PayloadFormat (fun () ->
                    DatabaseSession.value<int64> 0 row |> DatabaseSession.admitted Sequence.create)

            let at = Some sequence

            let! previous =
                VerificationRows.capture at VerificationFinding.PayloadFormat (fun () ->
                    DatabaseSession.value<byte array> 1 row)

            do! LedgerVerification.link state sequence previous

            let! raw =
                VerificationRows.capture at VerificationFinding.PayloadFormat (fun () ->
                    StoredEntries.decode installationId row)

            return! LedgerVerification.append installationId expected state raw.Entry raw.Metadata
        }

    /// Run inside the scan's repeatable-read snapshot; only the fold decides precedence.
    let private earliestDuplicate connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT min(sequence) FROM (SELECT sequence,row_number() OVER (PARTITION BY operation_id ORDER BY sequence) AS occurrence FROM claimcore.ledger) AS occurrences WHERE occurrence=2"
                    [||]
                    token

            return
                match List.exactlyOne rows |> Array.exactlyOne with
                | :? DBNull -> None
                | value -> Some(DatabaseSession.admitted Sequence.create (unbox<int64> value))
        }

    let private scan
        connection
        installationId
        (expected: LedgerPosition option)
        (token: CancellationToken)
        =
        task {
            let! duplicate = earliestDuplicate connection token
            let mutable state = Ok(LedgerVerification.initial expected duplicate)

            use statement =
                DatabaseSession.statement
                    connection
                    ($"SELECT {StoredEntries.columns} FROM claimcore.ledger ORDER BY sequence")
                    [||]

            use! reader = statement.ExecuteReaderAsync(token)
            let mutable reading = true

            while reading && Result.isOk state do
                let! found = reader.ReadAsync(token)

                if not found then
                    reading <- false
                else
                    let row = Array.zeroCreate<obj> reader.FieldCount
                    reader.GetValues row |> ignore

                    state <-
                        state
                        |> Result.bind (fun state -> scanEntry installationId expected state row)

            return state
        }

    let private head connection (state: LedgerVerification.Scan) token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT sequence,entry_hash FROM claimcore.ledger_head"
                    [||]
                    token

            return
                VerificationRows.capture None VerificationFinding.Head (fun () ->
                    match rows with
                    | [ row ] ->
                        Some(
                            StoredEntries.position
                                (DatabaseSession.value 0 row)
                                (DatabaseSession.value 1 row)
                        )
                    | _ -> None)
                |> Result.bind (LedgerVerification.head state)
        }

    let private projections connection installationId (state: LedgerVerification.Scan) token =
        task {
            let! cases = VerificationRows.cases connection installationId state.Cases token

            match cases with
            | Error error -> return Error error
            | Ok() -> return! VerificationRows.authority connection state.Actors token
        }

    let private finish
        connection
        installationId
        (expected: LedgerPosition option)
        (state: LedgerVerification.Scan)
        token
        =
        task {
            match LedgerVerification.erasure state with
            | Error error -> return VerificationResult.Diverged error
            | Ok() ->
                let! projections = projections connection installationId state token

                match projections with
                | Error error -> return VerificationResult.Diverged error
                | Ok() ->
                    let! checkedHead = head connection state token

                    return
                        match checkedHead with
                        | Error error -> VerificationResult.Diverged error
                        | Ok() ->
                            LedgerVerification.complete expected state
                            |> Result.map VerificationResult.Verified
                            |> Result.defaultWith VerificationResult.Diverged
        }

    let verify
        (source: NpgsqlDataSource)
        (expected: LedgerPosition option)
        (token: CancellationToken)
        =
        DatabaseSession.read source token (fun connection ->
            task {
                ServerSupport.require connection.PostgreSqlVersion
                let! installationId, _ = Baseline.require connection token
                let! result = scan connection installationId expected token

                match result with
                | Error error -> return VerificationResult.Diverged error
                | Ok state -> return! finish connection installationId expected state token
            })
