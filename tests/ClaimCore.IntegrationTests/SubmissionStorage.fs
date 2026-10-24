module internal ClaimCore.IntegrationTests.SubmissionStorage

open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Contract

let private recovery (store: ILedgerStore) intercept =
    { new IRecoveryStore with
        member _.StageCase(principal, envelope, authorize, token) =
            intercept envelope (fun () ->
                store.Recovery.StageCase(principal, envelope, authorize, token))

        member _.StageAuthority(principal, request, envelope, authorize, token) =
            intercept envelope (fun () ->
                store.Recovery.StageAuthority(principal, request, envelope, authorize, token))

        member _.Pending(actor, after, limit, token) =
            store.Recovery.Pending(actor, after, limit, token)

        member _.Load(actor, request, token) =
            store.Recovery.Load(actor, request, token)

        member _.Forget(actor, key, token) =
            store.Recovery.Forget(actor, key, token)

        member _.Retained(actor, key, token) =
            store.Recovery.Retained(actor, key, token)
    }


let private withRecovery (store: ILedgerStore) recovery =
    { new ILedgerStore with
        member _.Recovery = recovery

        member _.WriteCase(principal, operation, reference, decide, token) =
            store.WriteCase(principal, operation, reference, decide, token)

        member _.WriteAuthority(principal, operation, target, reference, decide, token) =
            store.WriteAuthority(principal, operation, target, reference, decide, token)

        member _.ReadAuthorityTarget(principal, operation, target, reference, token) =
            store.ReadAuthorityTarget(principal, operation, target, reference, token)

        member _.ReadCase(principal, operation, reference, token) =
            store.ReadCase(principal, operation, reference, token)

        member _.ListCases(principal, roles, select, limit, token) =
            store.ListCases(principal, roles, select, limit, token)

        member _.ReadHistory(principal, reference, select, limit, token) =
            store.ReadHistory(principal, reference, select, limit, token)

        member _.ListActors(principal, select, limit, token) =
            store.ListActors(principal, select, limit, token)

        member _.ListGrants(principal, target, select, limit, token) =
            store.ListGrants(principal, target, select, limit, token)

        member _.ReadOperation(principal, operation, token) =
            store.ReadOperation(principal, operation, token)

        member _.ReadActor(principal, token) = store.ReadActor(principal, token)
    }


/// Pauses only after a real operational commit and before the core's independent acceptance.
type Barrier(store: ILedgerStore, operationId) =
    let staged =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let released =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let intercept (envelope: ExecutionEnvelope) run =
        task {
            let! result = run ()

            if OperationInput.operationId envelope.Input = operationId then
                staged.TrySetResult() |> ignore
                do! released.Task

            return result
        }

    member _.Staged = staged.Task
    member _.Release() = released.TrySetResult() |> ignore
    member _.Store = withRecovery store (recovery store intercept)

/// Measures the actual operational transaction separately from ledger acceptance.
type Timing(store: ILedgerStore) =
    let samples = System.Collections.Concurrent.ConcurrentBag<float>()

    let intercept _ run =
        task {
            let watch = System.Diagnostics.Stopwatch.StartNew()
            let! result = run ()
            samples.Add watch.Elapsed.TotalMilliseconds
            return result
        }

    member _.Store = withRecovery store (recovery store intercept)

    member _.Summary =
        let values = samples.ToArray() |> Array.sort

        if values.Length = 0 then
            invalidOp "No submission transaction was measured."

        {|
            count = values.Length
            p50Milliseconds = values[values.Length / 2]
            p95Milliseconds = values[int (float (values.Length - 1) * 0.95)]
            p99Milliseconds = values[int (float (values.Length - 1) * 0.99)]
        |}
