module internal ClaimCore.IntegrationTests.SubmissionStorage

open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Protocol

let private interceptWrites (store: IStore) intercept =
    { new IStore with
        member _.Read
            ({
                 ReadOperation.Work = work
                 CancellationToken = token
             })
            =
            store.Read(
                {
                    ReadOperation.Work = work
                    CancellationToken = token
                }
            )

        member _.Write
            ({
                 WriteOperation.Work = work
                 CancellationToken = token
             })
            =
            task {
                do! intercept ()

                return!
                    store.Write(
                        {
                            WriteOperation.Work = work
                            CancellationToken = token
                        }
                    )
            }
    }

/// Pauses before the writer starts its real head-locked transaction.
type Barrier(store: IStore) =
    let staged =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let released =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let intercept () =
        task {
            staged.TrySetResult() |> ignore
            do! released.Task
        }

    member _.Staged = staged.Task
    member _.Release() = released.TrySetResult() |> ignore
    member _.Store = interceptWrites store intercept
