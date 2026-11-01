namespace ClaimCore.Web

open ClaimCore.Domain
open ClaimCore.Protocol


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
        new ConcurrencyLimiter(
            ConcurrencyLimiterOptions(PermitLimit = AdmissionPolicy.ingress, QueueLimit = 0)
        )

    member _.Acquire() = limiter.AttemptAcquire(1)

    interface IDisposable with
        member _.Dispose() = limiter.Dispose()

type internal AuthenticationAdmission() =
    let limiter = AdmissionLease.limiter AdmissionPolicy.authentication 0
    member _.Acquire() = limiter.AttemptAcquire 1

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

    /// Run authentication as its own bounded stage, releasing capacity before the actor workload.
    let authentication (application: WebApplication) =
        let passed = obj ()
        let stage = (application :> IApplicationBuilder).New()
        stage.UseAuthentication() |> ignore

        stage.Run(
            RequestDelegate(fun context ->
                context.Items[passed] <- box true
                Task.CompletedTask)
        )

        let authenticate = stage.Build()

        application.Use(
            Func<HttpContext, RequestDelegate, Task>(fun context next ->
                task {
                    let budget =
                        context.RequestServices.GetRequiredService<AuthenticationAdmission>()

                    use lease = budget.Acquire()

                    if lease.IsAcquired then
                        do! authenticate.Invoke context

                        if context.Items.Remove passed then
                            do! next.Invoke context
                    else
                        do! busy context
                })
        )
        |> ignore

    let configure (limits: RequestGateLimits) (services: IServiceCollection) =
        services.AddSingleton<DynamicIngress>() |> ignore
        services.AddSingleton<AuthenticationAdmission>() |> ignore

        services.AddSingleton<PrincipalAdmission>(fun _ ->
            new PrincipalAdmission(limits.CorePermitLimit, limits.CoreQueueLimit))
        |> ignore

        services.AddRateLimiter(fun options ->
            options.RejectionStatusCode <- StatusCodes.Status429TooManyRequests

            options.OnRejected <-
                Func<OnRejectedContext, CancellationToken, ValueTask>(fun rejected _ ->
                    ValueTask(busy rejected.HttpContext))

            options.AddFixedWindowLimiter(
                "login",
                fun limiter ->
                    limiter.PermitLimit <- limits.LoginPermitLimit
                    limiter.Window <- TimeSpan.FromMinutes(1.)
                    limiter.QueueLimit <- 0
            )
            |> ignore)
        |> ignore
