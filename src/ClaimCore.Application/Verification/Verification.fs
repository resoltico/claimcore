namespace ClaimCore.Application.Verification

open ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Application.Verification.VerificationChecks
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

    let private attachments connection state token =
        task {
            let! references =
                task {
                    try
                        return!
                            AttachmentStorage.checkReferences
                                connection
                                (Replay.attachmentReferences state)
                                token
                    with :? RecordCorruptException ->
                        return false
                }

            if not references then
                return Error(divergence None VerificationFinding.AttachmentReferences)
            else
                try
                    let! payloads =
                        AttachmentStorage.checkBlobs connection (Replay.attachmentBlobs state) token

                    return require None VerificationFinding.AttachmentPayloads payloads
                with :? RecordCorruptException ->
                    return Error(divergence None VerificationFinding.AttachmentPayloads)
        }

    let private erasureAndAttachments connection state token =
        match Replay.erasure state with
        | Error error -> System.Threading.Tasks.Task.FromResult(Error error)
        | Ok() -> attachments connection state token

    /// Authoritative scan for maintenance; projection divergence must not prevent repair.
    let authoritative connection installationId expected token =
        task {
            let! scanned = scan connection installationId expected token

            match scanned with
            | Error finding -> return Error finding
            | Ok state ->
                let! evidence = erasureAndAttachments connection state token

                match evidence with
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
            let expected = Replay.payments state

            try
                let! rows = PaymentStorage.projections connection token

                let actual =
                    rows |> List.map (fun payment -> payment.PaymentId, payment) |> Map.ofList

                return
                    require
                        None
                        VerificationFinding.CasePaymentsProjection
                        (rows.Length = actual.Count && expected = Ok actual)
            with :? RecordCorruptException ->
                return Error(divergence None VerificationFinding.CasePaymentsProjection)
        }

    let private paymentTotals connection state token =
        task {
            let expected =
                Replay.familyRows state
                |> Result.map (fun rows ->
                    rows
                    |> Map.toList
                    |> List.choose (fun (id, row) ->
                        CaseState.fields row.Snapshot.State
                        |> Option.map (fun fields -> fields.Finance.CurrencyTotals)
                        |> Option.filter (Map.isEmpty >> not)
                        |> Option.map (fun totals -> id, totals))
                    |> Map.ofList)

            try
                let! actual = PaymentStorage.totals connection None token

                return
                    require
                        None
                        VerificationFinding.CasePaymentTotalsProjection
                        (expected = Ok actual)
            with :? RecordCorruptException ->
                return Error(divergence None VerificationFinding.CasePaymentTotalsProjection)
        }

    let private attachmentProjection connection state token =
        task {
            try
                let! matches =
                    AttachmentStorage.checkProjection
                        connection
                        (Replay.attachmentProjection state)
                        token

                return require None VerificationFinding.CaseAttachmentsProjection matches
            with :? RecordCorruptException ->
                return Error(divergence None VerificationFinding.CaseAttachmentsProjection)
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
                    | Ok() ->
                        let! payments = payments connection state token

                        match payments with
                        | Error finding -> return Error finding
                        | Ok() ->
                            let! totals = paymentTotals connection state token

                            match totals with
                            | Error finding -> return Error finding
                            | Ok() -> return! attachmentProjection connection state token
        }

    let private finish
        connection
        installationId
        (expected: LedgerPosition option)
        (state: Replay.Scan)
        token
        =
        task {
            let! evidence = erasureAndAttachments connection state token

            match evidence with
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
