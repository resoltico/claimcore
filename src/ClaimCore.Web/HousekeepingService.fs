namespace ClaimCore.Web

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting

/// One bounded batch per jittered ten-minute interval; cancellation drains through the runtime lease.
type internal HousekeepingService(work: CancellationToken -> Task<unit>, clock: TimeProvider) =
    inherit BackgroundService()

    override _.ExecuteAsync token =
        task {
            try
                while not token.IsCancellationRequested do
                    let delay =
                        TimeSpan.FromMinutes(10.)
                        + TimeSpan.FromSeconds(float (Random.Shared.Next(-60, 61)))

                    do! Task.Delay(delay, clock, token)

                    try
                        do! work token
                    with
                    | :? OperationCanceledException when token.IsCancellationRequested -> ()
                    | _ ->
                        Console.Error.WriteLine(
                            "ClaimCore housekeeping failed; the next scheduled batch will retry."
                        )
            with :? OperationCanceledException when token.IsCancellationRequested ->
                ()
        }
