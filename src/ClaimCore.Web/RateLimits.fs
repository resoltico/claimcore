namespace ClaimCore.Web

open ClaimCore.Contract

open ClaimCore.Contract.Protocol

open System
open System.Threading
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Web.Composition

/// One process-wide budget, including cookie authentication and OIDC callbacks.
type internal DynamicIngress() =
    let limiter =
        new ConcurrencyLimiter(ConcurrencyLimiterOptions(PermitLimit = 24, QueueLimit = 0))

    member _.Acquire() = limiter.AttemptAcquire(1)

    interface IDisposable with
        member _.Dispose() = limiter.Dispose()

module internal RateLimits =
    let private busy context =
        task {
            HttpHeaders.noStore context

            do!
                (RouteSupport.hostFailure context HostFailureReason.RateLimited)
                    .ExecuteAsync(context)
        }

    let ingress (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            let limiter = context.RequestServices.GetRequiredService<DynamicIngress>()
            use lease = limiter.Acquire()

            if lease.IsAcquired then
                do! next.Invoke context
            else
                do! busy context
        }

    let configure (limits: WebAdmissionLimits) (services: IServiceCollection) =
        services.AddSingleton<DynamicIngress>() |> ignore

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
