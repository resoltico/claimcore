module ClaimCore.Tests.CoreRecoveryStore

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application

/// In-memory double for typed Application workflow tests. It models exact request identity and
/// technical lifecycle only; PostgreSQL qualification remains the authority for transaction rules.
type internal Store
    (
        ?retainFailure: RecoveryStoreFailure,
        ?startFailure: RecoveryStoreFailure,
        ?dismissFailure: RecoveryStoreFailure,
        ?settleFailure: RecoveryStoreFailure,
        ?settleThrows: bool,
        ?onGet: unit -> unit,
        ?getFailure: RecoveryStoreFailure,
        ?transformGet: RetainedPreparation -> RetainedPreparation,
        ?onStart: unit -> unit
    ) =
    let gate = obj ()
    let lineage = Guid.Parse("30000000-0000-4000-8000-000000000001")
    let mutable values: Map<Guid, RetainedPreparation> = Map.empty
    let mutable startCalls = 0
    let mutable settlementCalls = 0
    let mutable getCalls = 0

    let same (draft: RecoveryPreparationDraft) (retained: RetainedPreparation) =
        draft.CanonicalRequestFormat = retained.CanonicalRequestFormat
        && draft.RequestSha256 = retained.RequestSha256
        && draft.CanonicalRequest = retained.CanonicalRequest

    let materialize
        (draft: RecoveryPreparationDraft)
        (lifecycle: PreparationLifecycle)
        : RetainedPreparation =
        {
            OperationId = draft.OperationId
            CanonicalRequestFormat = draft.CanonicalRequestFormat
            RequestSha256 = draft.RequestSha256
            CanonicalRequest = Array.copy draft.CanonicalRequest
            PreparedAt = DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)
            PreparingApplicationVersion = draft.PreparingApplicationVersion
            PreparingContractFingerprint = draft.PreparingContractFingerprint
            PreparingContractKind = draft.PreparingContractKind
            Lifecycle = lifecycle
            Attempts = []
            LegacyUncertainty = false
        }

    let update (value: RetainedPreparation) =
        values <- Map.add value.OperationId value values
        value

    member _.StartCalls = startCalls
    member _.SettlementCalls = settlementCalls
    member _.GetCalls = getCalls

    interface IRecoveryStore with
        member _.InstallationLineage _ = Task.FromResult(Ok lineage)

        member _.Retain(draft, _) =
            Task.FromResult(
                lock gate (fun () ->
                    match retainFailure with
                    | Some failure -> Error failure
                    | None ->
                        match Map.tryFind draft.OperationId values with
                        | Some retained when same draft retained ->
                            Ok(RecoveryRetain.Existing retained)
                        | Some _ -> Error RecoveryStoreFailure.IdempotencyConflict
                        | None ->
                            Ok(
                                materialize draft PreparationLifecycle.Unsubmitted
                                |> update
                                |> RecoveryRetain.Created
                            ))
            )

        member _.Get(operationId, _) =
            let result =
                lock gate (fun () ->
                    getCalls <- getCalls + 1

                    match getFailure with
                    | Some failure -> Error failure
                    | None ->
                        Map.tryFind operationId values
                        |> Option.map (fun value ->
                            transformGet
                            |> Option.map (fun change -> change value)
                            |> Option.defaultValue value)
                        |> Ok)

            onGet |> Option.iter (fun callback -> callback ())
            Task.FromResult result

        member _.List(_, limit, _) =
            Task.FromResult(
                lock gate (fun () ->
                    let sorted =
                        values
                        |> Map.toList
                        |> List.map snd
                        |> List.sortByDescending (fun value -> value.PreparedAt, value.OperationId)

                    let page = sorted |> List.truncate limit

                    Ok
                        {
                            Items = page
                            NextAfter =
                                if sorted.Length > page.Length then
                                    page
                                    |> List.tryLast
                                    |> Option.map (fun value ->
                                        {
                                            PreparedAt = value.PreparedAt
                                            OperationId = value.OperationId
                                        })
                                else
                                    None
                        })
            )

        member _.Start(operationId, _) =
            let result =
                lock gate (fun () ->
                    startCalls <- startCalls + 1

                    match startFailure with
                    | Some failure -> Error failure
                    | None ->
                        match Map.tryFind operationId values with
                        | None -> Error RecoveryStoreFailure.NotFound
                        | Some value ->
                            match value.Lifecycle with
                            | PreparationLifecycle.Dismissed _ -> Ok(RecoveryStart.Dismissed value)
                            | PreparationLifecycle.Unsubmitted ->
                                let started =
                                    { value with
                                        Lifecycle =
                                            PreparationLifecycle.SubmissionStarted
                                                DateTimeOffset.UtcNow
                                    }
                                    |> update

                                Ok(RecoveryStart.Started(Guid.NewGuid(), started))
                            | PreparationLifecycle.SubmissionStarted _ ->
                                Ok(RecoveryStart.AlreadyStarted(Guid.NewGuid(), value)))

            onStart |> Option.iter (fun callback -> callback ())
            Task.FromResult result

        member _.Settle(_, _, _) =
            lock gate (fun () -> settlementCalls <- settlementCalls + 1)

            if settleThrows |> Option.defaultValue false then
                Task.FromException<Result<unit, RecoveryStoreFailure>>(InvalidOperationException())
            else
                Task.FromResult(settleFailure |> Option.map Error |> Option.defaultValue (Ok()))

        member _.Dismiss(operationId, _) =
            Task.FromResult(
                lock gate (fun () ->
                    match dismissFailure with
                    | Some failure -> Error failure
                    | None ->
                        match Map.tryFind operationId values with
                        | None -> Error RecoveryStoreFailure.NotFound
                        | Some value ->
                            match value.Lifecycle with
                            | PreparationLifecycle.Unsubmitted ->
                                let dismissed =
                                    { value with
                                        Lifecycle =
                                            PreparationLifecycle.Dismissed DateTimeOffset.UtcNow
                                    }
                                    |> update

                                Ok(RecoveryDismissal.Dismissed dismissed)
                            | PreparationLifecycle.Dismissed _ ->
                                Ok(RecoveryDismissal.AlreadyDismissed value)
                            | PreparationLifecycle.SubmissionStarted _ ->
                                Ok(RecoveryDismissal.SubmissionAlreadyStarted value))
            )
