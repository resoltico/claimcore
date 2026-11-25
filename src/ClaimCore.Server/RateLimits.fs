namespace ClaimCore.Server

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation


open System
open System.Threading
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Server.Composition

/// One process-wide budget, including cookie authentication and OIDC callbacks.
type internal DynamicIngress() =
    let limiter =
        new ConcurrencyLimiter(
            ConcurrencyLimiterOptions(PermitLimit = AdmissionPolicy.ingress, QueueLimit = 0)
        )

    member _.Acquire() = limiter.AttemptAcquire(1)

    interface IDisposable with
        member _.Dispose() = limiter.Dispose()

type internal AuthenticationAdmission(stopping: CancellationToken) =
    let cookie =
        AdmissionLease.limiter
            AdmissionPolicy.cookieAuthentication
            AdmissionPolicy.queuedCookieAuthentication

    let bearer = AdmissionLease.limiter AdmissionPolicy.bearerAuthentication 0
    let interactive = AdmissionLease.limiter AdmissionPolicy.interactiveAuthentication 0
    let closed = new CancellationTokenSource()
    let closure = closed.Token
    let mutable disposed = 0

    member _.Run(context: HttpContext, action: IDisposable -> Task<'value>) =
        task {
            let queued =
                not (AuthMiddleware.interactiveRequest context.Request.Path)
                && RequestGate.credentialMode context <> CredentialMode.Bearer

            let limiter =
                if AuthMiddleware.interactiveRequest context.Request.Path then
                    interactive
                elif queued then
                    cookie
                else
                    bearer

            let client = context.RequestAborted

            let clientCancelled () =
                client.IsCancellationRequested && not stopping.IsCancellationRequested

            let! acquired =
                task {
                    if Volatile.Read(&disposed) <> 0 then
                        return None
                    else
                        use deadline = new CancellationTokenSource()

                        use waiting =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                [| client; stopping; closure; deadline.Token |]
                            )

                        try
                            if queued then
                                deadline.CancelAfter AdmissionPolicy.authenticationQueueWait

                            let! lease =
                                if queued then
                                    limiter.AcquireAsync(1, waiting.Token).AsTask()
                                else
                                    Task.FromResult(limiter.AttemptAcquire 1)
                            // The waiting deadline must never become an active provider-work deadline.
                            deadline.CancelAfter Timeout.InfiniteTimeSpan

                            if waiting.IsCancellationRequested then
                                lease.Dispose()
                                return raise (OperationCanceledException(waiting.Token))
                            elif not lease.IsAcquired then
                                lease.Dispose()
                                return None
                            else
                                return Some lease
                        with
                        | :? OperationCanceledException as error when
                            error.CancellationToken = waiting.Token && clientCancelled ()
                            ->
                            return raise (OperationCanceledException(client))
                        | :? OperationCanceledException
                        | :? ObjectDisposedException -> return None
                }

            match acquired with
            | None -> return None
            | Some lease ->
                use owner = AdmissionLease.once lease.Dispose

                if clientCancelled () then
                    client.ThrowIfCancellationRequested()

                if
                    context.RequestAborted.IsCancellationRequested
                    || stopping.IsCancellationRequested
                    || Volatile.Read(&disposed) <> 0
                then
                    return None
                else
                    let! value = action owner
                    return Some value
        }

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                closed.Cancel()
                cookie.Dispose()
                bearer.Dispose()
                interactive.Dispose()
                closed.Dispose()
                // Active owners retain their leases and existing provider cleanup lifetime.


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

                    let! result =
                        budget.Run(
                            context,
                            fun owner ->
                                task {
                                    context.Items[capacityKey] <- owner

                                    try
                                        do! next.Invoke context
                                    finally
                                        context.Items.Remove capacityKey |> ignore
                                }
                        )

                    if result.IsNone then
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
        services.AddSingleton<AttachmentTransferAdmission>() |> ignore

        services.AddSingleton<AuthenticationAdmission>(fun provider ->
            new AuthenticationAdmission(
                provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping
            ))
        |> ignore

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
