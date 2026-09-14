namespace ClaimCore.Tests

open System
open System.Threading.Tasks
open ClaimCore.Application

module internal RecoveryStoreRead =
    let retain
        (state: RecoveryStoreStateData)
        (settings: RecoveryStoreSettings)
        (draft: RecoveryPreparationDraft)
        =
        Task.FromResult(
            lock state.Gate (fun () ->
                match settings.RetainFailure with
                | Some failure -> Error failure
                | None ->
                    match Map.tryFind draft.OperationId state.Accepted with
                    | Some value -> Ok(RecoveryRetain.ObservedAccepted value)
                    | None ->
                        match Map.tryFind draft.OperationId state.Revocations with
                        | Some value -> Ok(RecoveryRetain.Revoked value)
                        | None ->
                            match Map.tryFind draft.OperationId state.Values with
                            | Some retained when RecoveryStoreState.same draft retained ->
                                Ok(RecoveryRetain.Existing retained)
                            | Some _ -> Error RecoveryStoreFailure.IdempotencyConflict
                            | None ->
                                RecoveryStoreState.materialize
                                    state
                                    draft
                                    PreparationLifecycle.Unsubmitted
                                |> RecoveryStoreState.update state
                                |> RecoveryRetain.Created
                                |> Ok)
        )

    let get (state: RecoveryStoreStateData) (settings: RecoveryStoreSettings) operationId =
        let result =
            lock state.Gate (fun () ->
                state.GetCalls <- state.GetCalls + 1

                match settings.GetFailure with
                | Some failure -> Error failure
                | None ->
                    match
                        Map.tryFind operationId state.Values,
                        Map.tryFind operationId state.Revocations
                    with
                    | Some value, _ ->
                        settings.TransformGet
                        |> Option.map (fun change -> change value)
                        |> Option.defaultValue value
                        |> fun retained ->
                            RecoveryStoredOperation.Retained(
                                retained,
                                RecoveryStoreState.authority retained.Lifecycle
                            )
                        |> Some
                        |> Ok
                    | None, Some revoked ->
                        Ok(Some(RecoveryStoredOperation.RevokedTombstone revoked))
                    | None, None -> Ok None)

        settings.OnGet |> Option.iter (fun callback -> callback ())
        Task.FromResult result

    let inspect (state: RecoveryStoreStateData) operationId after limit =
        Task.FromResult(
            lock state.Gate (fun () ->
                match
                    Map.tryFind operationId state.Values, Map.tryFind operationId state.Revocations
                with
                | Some retained, _ ->
                    Ok(
                        Some(
                            RecoveryStoreInspection.Retained(
                                retained,
                                RecoveryStoreState.attemptPage state operationId after limit,
                                RecoveryStoreState.authority retained.Lifecycle
                            )
                        )
                    )
                | None, Some revoked -> Ok(Some(RecoveryStoreInspection.RevokedTombstone revoked))
                | None, None -> Ok None)
        )

    let private hasPreceded after (occurredAt: DateTimeOffset) (operationId: Guid) =
        after
        |> Option.forall (fun cursor ->
            occurredAt < cursor.OccurredAt
            || (occurredAt = cursor.OccurredAt && operationId.CompareTo(cursor.OperationId) < 0))

    let private retainedListItems state view =
        state.Values
        |> Map.toList
        |> List.map snd
        |> List.choose (fun value ->
            match view, value.Lifecycle with
            | RecoveryListView.Pending, PreparationLifecycle.Unsubmitted
            | RecoveryListView.Pending, PreparationLifecycle.SubmissionStarted _
            | RecoveryListView.Terminal, PreparationLifecycle.Dismissed _ ->
                Some(
                    value.PreparedAt,
                    value.OperationId,
                    RecoveryStoreListItem.Retained(
                        value,
                        RecoveryStoreState.authority value.Lifecycle
                    )
                )
            | _ -> None)

    let private tombstoneListItems state view =
        match view with
        | RecoveryListView.Pending -> []
        | RecoveryListView.Terminal ->
            state.Revocations
            |> Map.toList
            |> List.choose (fun (operationId, revoked) ->
                if Map.containsKey operationId state.Values then
                    None
                else
                    Some(revoked.RevokedAt, operationId, RecoveryStoreListItem.Revoked revoked))

    let private pendingPreparations state =
        state.Values
        |> Map.toList
        |> List.map snd
        |> List.filter (fun value ->
            match value.Lifecycle with
            | PreparationLifecycle.Unsubmitted
            | PreparationLifecycle.SubmissionStarted _ -> true
            | PreparationLifecycle.Dismissed _ -> false)

    let private page
        (view: RecoveryListView)
        limit
        (pending: RetainedPreparation list)
        (ordered: (DateTimeOffset * Guid * RecoveryStoreListItem) list)
        : RecoveryStorePage =
        let values = ordered |> List.truncate limit

        {
            View = view
            Items = values |> List.map (fun (_, _, item) -> item)
            NextAfter =
                if ordered.Length > values.Length then
                    values
                    |> List.tryLast
                    |> Option.map (fun (occurredAt, operationId, _) ->
                        {
                            View = view
                            OccurredAt = occurredAt
                            OperationId = operationId
                        })
                else
                    None
            PendingPreparationCount = pending.Length
            PendingCanonicalRequestBytes =
                pending |> List.sumBy (fun value -> int64 value.CanonicalRequest.Length)
            MaximumPendingPreparations = 1024
            MaximumPendingCanonicalRequestBytes = 64L * 1024L * 1024L
        }

    let private listPage state view after limit =
        let ordered =
            retainedListItems state view @ tombstoneListItems state view
            |> List.sortByDescending (fun (occurredAt, operationId, _) -> occurredAt, operationId)
            |> List.filter (fun (occurredAt, operationId, _) ->
                hasPreceded after occurredAt operationId)

        page view limit (pendingPreparations state) ordered

    let list (state: RecoveryStoreStateData) view after limit =
        lock state.Gate (fun () -> listPage state view after limit)
        |> Ok
        |> Task.FromResult
