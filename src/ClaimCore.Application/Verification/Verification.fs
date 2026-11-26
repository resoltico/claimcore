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
        (installation: Installation)
        cipher
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
                    let identity = DatabaseSession.value<Guid> 2

                    let! roots =
                        OperationStorage.prepareMany
                            connection
                            cipher
                            (List.map identity rows)
                            token

                    for row in rows do
                        if Result.isOk state then
                            token.ThrowIfCancellationRequested()

                            let sequence =
                                DatabaseSession.value<int64> 0 row
                                |> DatabaseSession.admitted Sequence.create

                            let previous = DatabaseSession.value<byte array> 1 row

                            state <-
                                state
                                |> Result.bind (fun state ->
                                    result {
                                        do! Replay.link state sequence previous

                                        let! restore =
                                            Map.tryFind (identity row) roots
                                            |> known
                                                (Some sequence)
                                                VerificationFinding.MissingRoot

                                        let! root =
                                            VerificationRows.capture
                                                (Some sequence)
                                                VerificationFinding.PayloadFormat
                                                restore

                                        do!
                                            require
                                                (Some sequence)
                                                VerificationFinding.BootstrapPolicy
                                                (sequence <> Sequence.first
                                                 || Baseline.matchesBootstrap installation root)

                                        return!
                                            Replay.append
                                                installation.InstallationId
                                                installation.CaseReferencePolicy
                                                expected
                                                state
                                                root
                                    })

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

    let private attachments connection cipher state token =
        task {
            let! references =
                AttachmentStorage.checkReferences
                    connection
                    (Replay.attachmentReferences state)
                    token

            let! blobs =
                AttachmentStorage.checkBlobs
                    connection
                    cipher
                    (Replay.attachmentBlobs state)
                    (Replay.attachmentDescriptors state)
                    token

            return
                match references, blobs with
                | Ok(), result
                | result, Ok() -> result
                | Error link, Error blob ->
                    let order (finding: VerificationDivergence) =
                        finding.Sequence.IsNone,
                        finding.Sequence,
                        finding.Attachment |> Option.bind _.EffectIndex

                    Error(if order link <= order blob then link else blob)
        }

    let private erasureAndAttachments connection cipher state token =
        match Replay.erasure state with
        | Error error -> System.Threading.Tasks.Task.FromResult(Error error)
        | Ok() -> attachments connection cipher state token

    let history connection installation cipher expected token =
        task {
            let! scanned = scan connection installation cipher expected token

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

    /// Authoritative scan for maintenance; projection divergence must not prevent repair.
    let authoritative connection installation cipher expected token =
        task {
            let! verified = history connection installation cipher expected token

            match verified with
            | Error finding -> return Error finding
            | Ok state ->
                let! checkedAttachments = attachments connection cipher state token
                return checkedAttachments |> Result.map (fun () -> state)
        }

    let private payments connection families token =
        task {
            let expected = Replay.payments families

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

    let private paymentTotals
        connection
        (families: Result<Map<CaseId, FamilyProjectionDelta>, VerificationDivergence>)
        token
        =
        task {
            let expected =
                families
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

    let projections connection installationId families people state token =
        task {
            let! cases = VerificationRows.cases connection installationId families token

            match cases with
            | Error error -> return Error error
            | Ok workIndexes ->
                let! people = VerificationRows.people connection people token

                match people with
                | Error error -> return Error error
                | Ok() ->
                    let! work = VerificationRows.family connection families workIndexes token

                    match work with
                    | Error finding -> return Error finding
                    | Ok() ->
                        let! payments = payments connection families token

                        match payments with
                        | Error finding -> return Error finding
                        | Ok() ->
                            let! totals = paymentTotals connection families token

                            match totals with
                            | Error finding -> return Error finding
                            | Ok() -> return! attachmentProjection connection state token
        }

    let private finish
        connection
        installationId
        cipher
        (expected: LedgerPosition option)
        (state: Replay.Scan)
        token
        =
        task {
            let! evidence = erasureAndAttachments connection cipher state token

            match evidence with
            | Error error -> return VerificationResult.Diverged error
            | Ok() ->
                let! projections =
                    projections
                        connection
                        installationId
                        (Replay.familyRows state)
                        (Replay.peopleRows state)
                        state
                        token

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
        masterKey
        (expected: InstallationCheckpoint option)
        (token: CancellationToken)
        =
        DatabaseSession.read source token (fun connection ->
            task {
                do! OwnerAdmission.require connection token
                let! installation = Baseline.require connection token

                use! cipher =
                    Baseline.loadCipher connection installation.InstallationId masterKey token

                let identity =
                    expected
                    |> Option.map (CheckpointEvidence.installation installation.InstallationId)
                    |> Option.defaultValue (Ok())

                match identity with
                | Error finding -> return VerificationResult.Diverged finding
                | Ok() ->
                    let position = expected |> Option.map _.Position
                    let! result = scan connection installation cipher position token

                    match result with
                    | Error error -> return VerificationResult.Diverged error
                    | Ok state ->
                        let! result =
                            finish
                                connection
                                installation.InstallationId
                                cipher
                                position
                                state
                                token

                        match result with
                        | VerificationResult.Diverged _ -> return result
                        | VerificationResult.Verified _ ->
                            let! frontier = CheckpointEvidence.frontier connection expected token

                            return
                                frontier
                                |> Result.map (fun () -> result)
                                |> Result.defaultWith VerificationResult.Diverged
            })
