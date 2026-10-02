namespace ClaimCore.Web

open System
open System.Threading
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Contracts

module internal RateLimits =
    let private busy context =
        task {
            HttpHeaders.noStore context
            do! (WebWire.hostFailure WebHostFailure.Busy).ExecuteAsync(context)
        }

    let configure (limits: WebAdmissionLimits) (services: IServiceCollection) =
        services.AddRateLimiter(fun options ->
            options.RejectionStatusCode <- StatusCodes.Status429TooManyRequests

            options.OnRejected <-
                Func<OnRejectedContext, CancellationToken, ValueTask>(fun rejected _ ->
                    ValueTask(busy rejected.HttpContext))

            options.AddConcurrencyLimiter(
                "core",
                fun limiter ->
                    limiter.PermitLimit <- limits.CorePermitLimit
                    limiter.QueueProcessingOrder <- QueueProcessingOrder.OldestFirst
                    limiter.QueueLimit <- limits.CoreQueueLimit
            )
            |> ignore

            options.AddFixedWindowLimiter(
                "login",
                fun limiter ->
                    limiter.PermitLimit <- limits.LoginPermitLimit
                    limiter.Window <- TimeSpan.FromMinutes(1.)
                    limiter.QueueLimit <- 0
            )
            |> ignore)
        |> ignore
