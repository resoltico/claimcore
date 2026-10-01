module ClaimCore.Tests.CoreStore

open System
open System.Collections.Generic
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport

/// Test-only port double: not a storage adapter and not a claim of PostgreSQL correctness.
type internal Store
    (?onOperation: unit -> unit, ?onAccepted: unit -> unit, ?acceptedFailure: CoreFailure) =
    let pageSize = SemanticContract.current.MaximumPageSize
    let mutable cases: Map<string, Claim> = Map.empty
    let mutable receipts: Map<Guid, string * Receipt> = Map.empty
    let mutable transactions = 0
    let continuations = Dictionary<string, string>()
    let gate = obj ()
    member _.TransactionCalls = transactions

    interface ITestCommandExecutor with
        member _.Execute(operation, capture, decide) =
            Task.FromResult(
                lock gate (fun () ->
                    transactions <- transactions + 1
                    let request = Operation.request operation
                    let fingerprint = Operation.fingerprint operation

                    match Map.tryFind request.OperationId receipts with
                    | Some(original, receipt) when original = fingerprint ->
                        Ok { receipt with Replayed = true }
                    | Some _ -> Error CoreFailure.IdempotencyConflict
                    | None ->
                        match
                            decide
                                (capture ()).EffectiveBusinessDate
                                (Map.tryFind request.CaseReference cases)
                        with
                        | Error error -> Error(CoreFailure.Domain error)
                        | Ok claim ->
                            let receipt: Receipt =
                                {
                                    OperationId = request.OperationId
                                    Case = claim
                                    RecordedAt = DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)
                                    RecordedBy = "test-operator"
                                    Replayed = false
                                    CommandName = Commands.name request.Command
                                }

                            cases <- Map.add request.CaseReference claim cases
                            receipts <- Map.add request.OperationId (fingerprint, receipt) receipts
                            Ok receipt)
            )

    interface IClaimStore with
        member _.Get reference =
            Task.FromResult(lock gate (fun () -> Ok(Map.tryFind reference cases)))

        member _.List request =
            Task.FromResult(
                lock gate (fun () ->
                    let after =
                        request.AfterCursor
                        |> Option.map (fun token ->
                            match continuations.TryGetValue token with
                            | true, reference -> Ok(Some reference)
                            | _ -> Error CoreFailure.InvalidCaseListCursor)
                        |> Option.defaultValue (Ok None)

                    match after with
                    | Error failure -> Error failure
                    | Ok position ->
                        let items =
                            cases
                            |> Map.toList
                            |> List.filter (fun (reference, _) ->
                                position
                                |> Option.forall (fun cursor ->
                                    String.CompareOrdinal(reference, cursor) > 0))
                            |> List.map snd
                            |> List.truncate (request.Limit + 1)

                        let page = items |> List.truncate request.Limit

                        let next =
                            if items.Length > request.Limit then
                                page
                                |> List.tryLast
                                |> Option.map (fun claim ->
                                    let token = Guid.NewGuid().ToString("N")

                                    continuations[token] <-
                                        (Claim.view claim).Fields.CaseReference

                                    token)
                            else
                                None

                        Ok { Items = page; NextCursor = next })
            )

        member _.History(reference, afterVersion) =
            Task.FromResult(
                lock gate (fun () ->
                    let items =
                        receipts
                        |> Map.toList
                        |> List.map (snd >> snd)
                        |> List.filter (fun receipt ->
                            let view = Claim.view receipt.Case in
                            view.Fields.CaseReference = reference && view.Version > afterVersion)
                        |> List.sortBy (fun receipt -> (Claim.view receipt.Case).Version)
                        |> List.truncate (pageSize + 1)

                    let page = items |> List.truncate pageSize

                    let next =
                        if items.Length > pageSize then
                            page
                            |> List.tryLast
                            |> Option.map (fun receipt -> (Claim.view receipt.Case).Version)
                        else
                            None

                    Ok
                        {
                            Items = page
                            NextAfterVersion = next
                        })
            )

        member _.Operation operationId =
            let result =
                lock gate (fun () ->
                    Ok(
                        Map.tryFind operationId receipts
                        |> Option.map (fun (_, receipt) -> { receipt with Replayed = true })
                    ))

            onOperation |> Option.iter (fun callback -> callback ())
            Task.FromResult result

        member _.Accepted(operationId, requestSha256) =
            let result =
                lock gate (fun () ->
                    match acceptedFailure with
                    | Some failure -> Error failure
                    | None ->
                        match Map.tryFind operationId receipts with
                        | None -> Ok None
                        | Some(original, receipt) when original = requestSha256 ->
                            Ok(Some { receipt with Replayed = true })
                        | Some _ -> Error CoreFailure.IdempotencyConflict)

            onAccepted |> Option.iter (fun callback -> callback ())
            Task.FromResult result
