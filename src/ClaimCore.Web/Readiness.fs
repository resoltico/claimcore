namespace ClaimCore.Web

open System
open System.Threading
open System.Threading.Tasks

/// Each permit owns the probe through provider cleanup, even if cancellation is ignored.
[<Sealed>]
type Readiness(probe: CancellationToken -> Task<bool>, stopping: CancellationToken) =
    let permits = new SemaphoreSlim(2, 2)

    member _.Check(aborted: CancellationToken) =
        task {
            if aborted.IsCancellationRequested || stopping.IsCancellationRequested then
                return false
            elif not (permits.Wait 0) then
                return false
            else
                try
                    try
                        use linked =
                            CancellationTokenSource.CreateLinkedTokenSource(aborted, stopping)

                        linked.CancelAfter(TimeSpan.FromSeconds 2.)
                        linked.Token.ThrowIfCancellationRequested()
                        let! ready = probe linked.Token
                        return ready && not linked.IsCancellationRequested
                    with _ ->
                        return false
                finally
                    permits.Release() |> ignore
        }
