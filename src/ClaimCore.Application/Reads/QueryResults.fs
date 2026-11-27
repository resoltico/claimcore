namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Every read authorizes and decodes its continuation inside one store snapshot.
module internal QueryResults =
    let fault =
        function
        | StoreFailure.Internal -> CoreFault.InternalError
        | StoreFailure.Unavailable -> CoreFault.StoreUnavailable
        | StoreFailure.SchemaMismatch -> CoreFault.SchemaMismatch
        | _ -> CoreFault.StoreIntegrityError

    let failure =
        function
        | StoreFailure.Cancelled -> Outcome.Failed CoreFault.StoreUnavailable
        | value -> Outcome.Failed(fault value)

    let unavailable<'Value> : Outcome<'Value> =
        Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)

    let validLimit limit =
        limit >= 1 && limit <= Paging.MaximumPageSize

    let invalidLimitRefusal =
        Refusal.invalidInput (InputTarget.Limit, Violation.PageLimitRange Paging.MaximumPageSize)

    let invalidLimit () = Outcome.Rejected invalidLimitRefusal

    let parseReference value =
        CaseReference.parse value
        |> Result.mapError (fun violation ->
            Refusal.invalidInput (InputTarget.Field BusinessField.CaseReference, violation))

    let currentActor principal actor =
        if Access.validActor principal actor then
            Ok actor
        else
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)

    let readResult result = result |> Result.defaultWith failure

    let succeeded result = Outcome.ofResult result

    let collect results =
        List.foldBack
            (fun item rest ->
                Result.bind (fun value -> Result.map (fun rest -> value :: rest) rest) item)
            results
            (Ok [])

    let cursor decode =
        function
        | None -> Ok None
        | Some value -> decode value |> Result.map Some


/// A continuation authenticates the requester, selection, date and authorized traversal witness.
module internal ScopedTraversal =
    type Basis =
        {
            Generation: string
            ValidUntil: DateTimeOffset
        }

    let private ledgerBasis (tx: ReadTx) () =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    "SELECT h.sequence,m.publication_generation FROM claimcore.ledger_head h CROSS JOIN claimcore.register_maintenance m"
                    [||]
                    tx.CancellationToken

            let row = List.exactlyOne rows

            return
                {
                    Generation =
                        FormattableString.Invariant(
                            $"{DatabaseSession.value<int64> 0 row}.{DatabaseSession.value<int64> 1 row}"
                        )
                    ValidUntil = DateTimeOffset.MaxValue
                }
        }

    let private ticks (value: string) =
        match
            Int64.TryParse(
                value,
                Globalization.NumberStyles.None,
                Globalization.CultureInfo.InvariantCulture
            )
        with
        | true, value when value >= 0L && value <= DateTimeOffset.MaxValue.UtcTicks -> Some value
        | _ -> None

    let pageWithBasis
        (tx: ReadTx)
        purpose
        maximumCharacters
        requester
        (businessDate: System.DateOnly option)
        decode
        encode
        (readBasis: unit -> System.Threading.Tasks.Task<Basis>)
        (readWitness: unit -> System.Threading.Tasks.Task<string>)
        readPage
        proveFirst
        next
        after
        =
        task {
            let date =
                businessDate
                |> Option.map (fun date ->
                    date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
                |> Option.defaultValue ""

            let requester = ActorId.value requester

            let readWitness =
                if tx.ExportSnapshot then
                    fun () -> System.Threading.Tasks.Task.FromResult "export-snapshot"
                else
                    readWitness

            let admit (token: string) =
                if token.Length > maximumCharacters then
                    Error Refusal.InvalidCursor
                else
                    match
                        tx.Cipher.OpenCursor(purpose, requester, token)
                        |> Option.map (fun bytes -> Text.Encoding.ASCII.GetString(bytes).Split(':'))
                    with
                    | Some [| recordedDate; generation; observed; validUntil; witness; encoded |] when
                        recordedDate = date
                        ->
                        match ticks observed, ticks validUntil with
                        | Some start, Some expires when start <= expires ->
                            decode encoded
                            |> Result.map (fun position ->
                                (generation, start, expires, witness), position)
                        | _ -> Error Refusal.InvalidCursor
                    | Some [| _; _; _; _; _; _ |] -> Error Refusal.TraversalChanged
                    | _ -> Error Refusal.InvalidCursor

            match QueryResults.cursor admit after with
            | Error refusal -> return Error refusal
            | Ok continuation ->
                let! basis =
                    if tx.ExportSnapshot then
                        System.Threading.Tasks.Task.FromResult
                            {
                                Generation = "export"
                                ValidUntil = DateTimeOffset.MaxValue
                            }
                    else
                        readBasis ()

                let! witness =
                    task {
                        match continuation with
                        | None when proveFirst ->
                            let! current = readWitness ()
                            return Some current
                        | None -> return None
                        | Some((generation, observed, validUntil, witness), _) ->
                            let now = tx.ObservedAt.UtcTicks

                            if
                                generation = basis.Generation && observed <= now && now < validUntil
                            then
                                return Some witness
                            else
                                let! current = readWitness ()
                                return Some current
                    }

                if
                    continuation
                    |> Option.exists (fun ((_, _, _, digest), _) -> Some digest <> witness)
                then
                    return Error Refusal.TraversalChanged
                else
                    let! page = readPage witness (Option.map snd continuation)

                    match next page with
                    | None -> return Ok(page, None)
                    | Some position ->
                        let! current =
                            match witness with
                            | Some value -> System.Threading.Tasks.Task.FromResult value
                            | None -> readWitness ()

                        let invariant (value: int64) =
                            value.ToString(Globalization.CultureInfo.InvariantCulture)

                        let plain =
                            [
                                date
                                basis.Generation
                                invariant tx.ObservedAt.UtcTicks
                                invariant basis.ValidUntil.UtcTicks
                                current
                                encode position
                            ]
                            |> String.concat ":"
                            |> Text.Encoding.ASCII.GetBytes

                        let token = tx.Cipher.SealCursor(purpose, requester, plain)

                        if token.Length > maximumCharacters then
                            return
                                raise (
                                    RecordCorruptException
                                        "A generated continuation exceeds its transport limit."
                                )
                        else
                            return Ok(page, Some token)
        }

    let page tx purpose requester date decode encode readWitness readPage next after =
        pageWithBasis
            tx
            purpose
            Paging.MaximumCaseCursorCharacters
            requester
            (Some date)
            decode
            encode
            (ledgerBasis tx)
            readWitness
            (fun _ -> readPage)
            false
            next
            after

    let report tx purpose requester date decode encode readWitness readPage next after =
        pageWithBasis
            tx
            purpose
            Paging.MaximumCaseCursorCharacters
            requester
            (Some date)
            decode
            encode
            (ledgerBasis tx)
            readWitness
            readPage
            true
            next
            after
