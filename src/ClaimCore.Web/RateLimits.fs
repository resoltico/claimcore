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
    let cookie = AdmissionLease.limiter AdmissionPolicy.cookieAuthentication 0
    let bearer = AdmissionLease.limiter AdmissionPolicy.bearerAuthentication 0
    let interactive = AdmissionLease.limiter AdmissionPolicy.interactiveAuthentication 0

    member _.Acquire(context: HttpContext) =
        let limiter =
            if AuthMiddleware.interactiveRequest context.Request.Path then
                interactive
            elif RequestGate.credentialMode context = CredentialMode.Bearer then
                bearer
            else
                cookie

        limiter.AttemptAcquire 1

    interface IDisposable with
        member _.Dispose() =
            cookie.Dispose()
            bearer.Dispose()
            interactive.Dispose()

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

    /// Register on the root builder so WebApplication cannot insert authentication ahead of guards.
    /// Ordinary authentication releases before actor work; interactive handlers retain their owner.
    let authentication (application: WebApplication) =
        let capacityKey = obj ()

        application.Use(
            Func<HttpContext, RequestDelegate, Task>(fun context next ->
                task {
                    let budget =
                        context.RequestServices.GetRequiredService<AuthenticationAdmission>()

                    let lease = budget.Acquire context

                    if lease.IsAcquired then
                        use owner = AdmissionLease.once (fun () -> lease.Dispose())
                        context.Items[capacityKey] <- owner

                        try
                            do! next.Invoke context
                        finally
                            context.Items.Remove capacityKey |> ignore
                    else
                        lease.Dispose()
                        do! busy context
                })
        )
        |> ignore

        application.UseAuthentication() |> ignore

        application.Use(
            Func<HttpContext, RequestDelegate, Task>(fun context next ->
                if not (AuthMiddleware.interactiveRequest context.Request.Path) then
                    match context.Items.TryGetValue capacityKey with
                    | true, (:? IDisposable as owner) -> owner.Dispose()
                    | _ -> invalidOp "Authentication capacity has no request owner."

                next.Invoke context)
        )
        |> ignore

    let configure (limits: RequestGateLimits) (services: IServiceCollection) =
        services.AddSingleton<DynamicIngress>() |> ignore
        services.AddSingleton<AuthenticationAdmission>() |> ignore
        services.AddScoped<RequestCapacity>() |> ignore

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
