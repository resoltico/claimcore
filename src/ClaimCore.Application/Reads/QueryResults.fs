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
    let pageWithBasis
        (tx: ReadTx)
        purpose
        requester
        (businessDate: System.DateOnly option)
        decode
        encode
        (readWitness: unit -> System.Threading.Tasks.Task<string>)
        readPage
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
                if token.Length > Paging.MaximumCaseCursorCharacters then
                    Error Refusal.InvalidCursor
                else
                    match
                        tx.Cipher.OpenCursor(purpose, requester, token)
                        |> Option.map (fun bytes -> Text.Encoding.ASCII.GetString(bytes).Split(':'))
                    with
                    | Some [| recordedDate; witness; encoded |] when recordedDate = date ->
                        decode encoded |> Result.map (fun position -> witness, position)
                    | Some [| _; _; _ |] -> Error Refusal.TraversalChanged
                    | _ -> Error Refusal.InvalidCursor

            match QueryResults.cursor admit after with
            | Error refusal -> return Error refusal
            | Ok continuation ->
                let! witness =
                    match continuation with
                    | Some _ -> task { let! current = readWitness () in return Some current }
                    | None -> System.Threading.Tasks.Task.FromResult None

                if Option.map fst continuation <> witness then
                    return Error Refusal.TraversalChanged
                else
                    let! page = readPage (Option.map snd continuation)

                    let! continuation =
                        task {
                            match next page with
                            | None -> return None
                            | Some position ->
                                let! current =
                                    match witness with
                                    | Some value -> System.Threading.Tasks.Task.FromResult value
                                    | None -> readWitness ()

                                let plain =
                                    Text.Encoding.ASCII.GetBytes(
                                        String.concat ":" [ date; current; encode position ]
                                    )

                                return Some(tx.Cipher.SealCursor(purpose, requester, plain))
                        }

                    return Ok(page, continuation)
        }


    let page tx purpose requester date decode encode readWitness readPage next after =
        pageWithBasis tx purpose requester (Some date) decode encode readWitness readPage next after
