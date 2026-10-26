module internal ClaimCore.IntegrationTests.SubmissionStorage

open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Contract

let private interceptWrites (store: ILedgerStore) intercept =
    { new ILedgerStore with
        member _.ResolveOperation(principal, id, decide, token) =
            store.ResolveOperation(principal, id, decide, token)

        member _.WriteCase(principal, operation, reference, decide, token) =
            task {
                do! intercept operation
                return! store.WriteCase(principal, operation, reference, decide, token)
            }

        member _.WriteAuthority(principal, operation, target, reference, decide, token) =
            task {
                do! intercept operation
                return! store.WriteAuthority(principal, operation, target, reference, decide, token)
            }

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

/// Pauses before the writer starts its real head-locked transaction.
type Barrier(store: ILedgerStore, operationId) =
    let staged =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let released =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let intercept id =
        task {
            if id = operationId then
                staged.TrySetResult() |> ignore
                do! released.Task
        }

    member _.Staged = staged.Task
    member _.Release() = released.TrySetResult() |> ignore
    member _.Store = interceptWrites store intercept
