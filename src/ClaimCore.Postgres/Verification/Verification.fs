namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Application.VerificationChecks
open Npgsql

/// Deterministic precedence: ledger order with link/hash/commitment/format checks first, then
/// history and retained access rules; erasure, projections, head and checkpoint follow.
module internal Verification =
    /// Root batches are bounded and interpreted inside one repeatable-read snapshot.
    let private scan
        connection
        installationId
        (expected: LedgerPosition option)
        (token: CancellationToken)
        =
        task {
            let mutable state = Ok(Replay.initial expected)
            let mutable after = 0L
            let mutable running = true

            while running && Result.isOk state do
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT sequence,previous_hash,operation_id FROM claimcore.ledger WHERE sequence>$1 ORDER BY sequence LIMIT 128"
                        [| box after |]
                        token

                if rows.IsEmpty then
                    running <- false
                else
                    for row in rows do
                        if Result.isOk state then
                            let sequence =
                                DatabaseSession.value<int64> 0 row
                                |> DatabaseSession.admitted Sequence.create

                            let previous = DatabaseSession.value<byte array> 1 row

                            let identity =
                                DatabaseSession.value<Guid> 2 row
                                |> DatabaseSession.admitted OperationId.create

                            match
                                state
                                |> Result.bind (fun state -> Replay.link state sequence previous)
                            with
                            | Error error -> state <- Error error
                            | Ok() ->
                                try
                                    let! found =
                                        OperationStorage.find
                                            connection
                                            installationId
                                            identity
                                            token

                                    state <-
                                        state
                                        |> Result.bind (fun state ->
                                            match found with
                                            | Some root ->
                                                Replay.append installationId expected state root
                                            | None ->
                                                Error(
                                                    divergence
                                                        (Some sequence)
                                                        VerificationFinding.PayloadFormat
                                                ))
                                with :? RecordCorruptException ->
                                    state <-
                                        Error(
                                            divergence
                                                (Some sequence)
                                                VerificationFinding.PayloadFormat
                                        )

                            after <- Sequence.value sequence

            return state
        }

    let private head connection (state: Replay.Scan) token =
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
                |> Result.bind (Replay.head state)
        }

    /// Authoritative scan for maintenance; projection divergence must not prevent repair.
    let authoritative connection installationId expected token =
        task {
            let! scanned = scan connection installationId expected token

            match scanned with
            | Error finding -> return Error finding
            | Ok state ->
                match Replay.erasure state with
                | Error finding -> return Error finding
                | Ok() ->
                    let! checkedHead = head connection state token

                    return
                        checkedHead
                        |> Result.bind (fun () -> Replay.complete expected state)
                        |> Result.map (fun _ -> state)
        }

    let private payments connection (state: Replay.Scan) token =
        task {
            let expected =
                state.Cases
                |> Map.values
                |> Seq.collect (fun history -> history.PaymentProjections |> Map.values)
                |> Seq.map (fun payment -> payment.PaymentId, payment)
                |> Map.ofSeq

            try
                let! rows = PaymentStorage.projections connection token

                let actual =
                    rows |> List.map (fun payment -> payment.PaymentId, payment) |> Map.ofList

                return
                    require
                        None
                        VerificationFinding.CasePaymentsProjection
                        (rows.Length = actual.Count && expected = actual)
            with :? RecordCorruptException ->
                return Error(divergence None VerificationFinding.CasePaymentsProjection)
        }

    let projections connection installationId (state: Replay.Scan) token =
        task {
            let! cases = VerificationRows.cases connection installationId state token

            match cases with
            | Error error -> return Error error
            | Ok workIndexes ->
                let! people = VerificationRows.people connection state token

                match people with
                | Error error -> return Error error
                | Ok() ->
                    let! work = VerificationRows.family connection state workIndexes token

                    match work with
                    | Error finding -> return Error finding
                    | Ok() -> return! payments connection state token
        }

    let private finish
        connection
        installationId
        (expected: LedgerPosition option)
        (state: Replay.Scan)
        token
        =
        task {
            match Replay.erasure state with
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
                            Replay.complete expected state
                            |> Result.map VerificationResult.Verified
                            |> Result.defaultWith VerificationResult.Diverged
        }

    let verify
        (source: NpgsqlDataSource)
        (expected: InstallationCheckpoint option)
        (token: CancellationToken)
        =
        DatabaseSession.read source token (fun connection ->
            task {
                ServerSupport.require connection.PostgreSqlVersion
                let! installationId, _ = Baseline.require connection token

                let identity =
                    expected
                    |> Option.map (CheckpointEvidence.installation installationId)
                    |> Option.defaultValue (Ok())

                match identity with
                | Error finding -> return VerificationResult.Diverged finding
                | Ok() ->
                    let position = expected |> Option.map _.Position
                    let! result = scan connection installationId position token

                    match result with
                    | Error error -> return VerificationResult.Diverged error
                    | Ok state ->
                        let! result = finish connection installationId position state token

                        match result with
                        | VerificationResult.Diverged _ -> return result
                        | VerificationResult.Verified _ ->
                            let! frontier = CheckpointEvidence.frontier connection expected token

                            return
                                frontier
                                |> Result.map (fun () -> result)
                                |> Result.defaultWith VerificationResult.Diverged
            })
