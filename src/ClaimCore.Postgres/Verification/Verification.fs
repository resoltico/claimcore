namespace ClaimCore.Postgres

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
                    DatabaseSession.value<int64> 0 row)

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

    let private scan
        connection
        installationId
        (expected: ExpectedPosition option)
        (token: CancellationToken)
        =
        task {
            let mutable state = Ok(LedgerVerification.initial expected)

            use command =
                DatabaseSession.command
                    connection
                    ($"SELECT {StoredEntries.columns} FROM claimcore.ledger ORDER BY sequence")
                    [||]

            use! reader = command.ExecuteReaderAsync(token)
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
                            {
                                Sequence = DatabaseSession.value 0 row
                                EntryHash = DatabaseSession.value 1 row
                            }
                            : LedgerHead
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
        (expected: ExpectedPosition option)
        (state: LedgerVerification.Scan)
        token
        =
        task {
            match CaseFold.erasure state.Cases with
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
        (expected: ExpectedPosition option)
        (token: CancellationToken)
        =
        if expected |> Option.exists (ExpectedPositions.isValid >> not) then
            invalidArg (nameof expected) "Expected position is outside the supported format."

        DatabaseSession.read source token (fun connection ->
            task {
                ServerSupport.require connection.PostgreSqlVersion
                let! count = Migrations.history connection Migrations.scripts token

                if count <> Migrations.scripts.Length then
                    invalidOp "Unsupported schema version."

                let! installationId, _ = Migrations.installation connection token
                let! result = scan connection installationId expected token

                match result with
                | Error error -> return VerificationResult.Diverged error
                | Ok state -> return! finish connection installationId expected state token
            })
