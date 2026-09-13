module ClaimCore.Tests.CoreStore

open System
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application

/// Test-only port double: not a storage adapter and not a claim of PostgreSQL correctness.
type internal Store(?onOperation: unit -> unit) =
    let pageSize = SemanticContract.current.MaximumPageSize
    let mutable cases: Map<string, Claim> = Map.empty
    let mutable receipts: Map<Guid, string * Receipt> = Map.empty
    let mutable transactions = 0
    let gate = obj ()
    member _.TransactionCalls = transactions

    interface IClaimStore with
        member _.Transact(operation, decide) =
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
                        match decide (Map.tryFind request.CaseReference cases) with
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

        member _.Get reference =
            Task.FromResult(lock gate (fun () -> Ok(Map.tryFind reference cases)))

        member _.List after =
            Task.FromResult(
                lock gate (fun () ->
                    let items =
                        cases
                        |> Map.toList
                        |> List.filter (fun (reference, _) ->
                            after
                            |> Option.forall (fun cursor ->
                                String.CompareOrdinal(reference, cursor) > 0))
                        |> List.map snd
                        |> List.truncate (pageSize + 1)

                    let page = items |> List.truncate pageSize

                    let next =
                        if items.Length > pageSize then
                            page
                            |> List.tryLast
                            |> Option.map (fun claim -> (Claim.view claim).Fields.CaseReference)
                        else
                            None

                    Ok { Items = page; NextAfter = next })
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
