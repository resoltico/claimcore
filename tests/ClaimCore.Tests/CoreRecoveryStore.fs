module ClaimCore.Tests.CoreRecoveryStore

open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Tests

/// In-memory double for typed Application workflow tests. Its focused state/read/mutation modules
/// model authority and evidence without claiming PostgreSQL transaction or locking correctness.
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
    let state = RecoveryStoreState.create ()

    let settings: RecoveryStoreSettings =
        {
            RetainFailure = retainFailure
            StartFailure = startFailure
            DismissFailure = dismissFailure
            SettleFailure = settleFailure
            SettleThrows = settleThrows |> Option.defaultValue false
            OnGet = onGet
            GetFailure = getFailure
            TransformGet = transformGet
            OnStart = onStart
        }

    member _.StartCalls = state.StartCalls
    member _.SettlementCalls = state.SettlementCalls
    member _.GetCalls = state.GetCalls

    /// Composition setup for this synthetic double. Production Postgres owns this combined
    /// transaction; unit tests share the same in-memory claim port so public reads observe it.
    member _.AttachClaimStore(claimStore: IClaimStore) = state.ClaimStore <- Some claimStore

    /// Test setup only: emulate owner pruning while preserving independent durable revocation.
    member _.PruneRetainedForTest operationId =
        RecoveryStoreState.pruneRetained state operationId

    interface IRecoveryStore with
        member _.InstallationLineage _ =
            System.Threading.Tasks.Task.FromResult(Ok state.Lineage)

        member _.Retain(draft, _) =
            RecoveryStoreRead.retain state settings draft

        member _.Get(operationId, _) =
            RecoveryStoreRead.get state settings operationId

        member _.Inspect(operationId, after, limit, _) =
            RecoveryStoreRead.inspect state operationId after limit

        member _.List(view, after, limit, _) =
            RecoveryStoreRead.list state view after limit

        member _.Start(operationId, _) =
            RecoveryStoreMutation.start state settings operationId

        member _.Settle(attemptId, outcome, _) =
            RecoveryStoreMutation.settle state settings attemptId outcome

        member _.ExecuteAdmitted(operation, attemptId, today, decide, _) =
            RecoveryStoreMutation.executeAdmitted state settings operation attemptId today decide

        member _.Dismiss(operationId, requestSha256, _) =
            RecoveryStoreMutation.dismiss state settings operationId requestSha256
