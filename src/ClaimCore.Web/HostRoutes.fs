namespace ClaimCore.Web

open ClaimCore.Contract
open ClaimCore.Json

open ClaimCore.Contract.Protocol

open System
open System.IO
open System.Security.Claims
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.AspNetCore.StaticFiles
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Web.Composition

module HostRoutes =
    let internal loginReturnProperties () =
        let properties = AuthenticationProperties()
        properties.RedirectUri <- "/"
        properties

    let private session configuration (context: HttpContext) =
        task {
            if not (ApiRoutes.handshake context) then
                return RouteSupport.hostFailure context HostFailureReason.ContractMismatch
            else
                let! authenticated =
                    task {
                        match configuration.Oidc, Admission.credentialMode context with
                        | Some oidc, CredentialMode.BrowserCookie ->
                            let! principal = Admission.actorGet oidc configuration.Binding context in
                            return Result.isOk principal
                        | _ -> return false
                    }

                let antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>()
                let token = antiforgery.GetAndStoreTokens(context).RequestToken |> Option.ofObj

                return
                    Api.session.Output.Write(
                        ReadReply.Succeeded
                            {
                                Authenticated = authenticated
                                ContractIdentity = ContractIdentity.value
                                AntiforgeryToken = token
                            }
                    )
                    |> fun reply -> RouteSupport.reply reply context
        }

    let private logout configuration context =
        task {
            if not (ApiRoutes.handshake context) then
                return RouteSupport.hostFailure context HostFailureReason.ContractMismatch
            else
                match configuration.Oidc, Admission.credentialMode context with
                | Some oidc, CredentialMode.BrowserCookie ->
                    let antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>()

                    match!
                        Admission.actorPost oidc configuration.Binding 1024 antiforgery context
                    with
                    | Error reason -> return RouteSupport.admissionFailure context reason
                    | Ok _ ->
                        match! HttpBody.readRequest 1024 context with
                        | Error problem -> return RouteSupport.inputFailure context problem
                        | Ok bytes ->
                            match Codec.decode 1024 Api.logout.Input bytes with
                            | Error _ ->
                                return
                                    RouteSupport.hostFailure context HostFailureReason.InputInvalid
                            | Ok _ ->
                                do!
                                    context.SignOutAsync(
                                        CookieAuthenticationDefaults.AuthenticationScheme
                                    )

                                context.User <- ClaimsPrincipal(ClaimsIdentity())

                                return
                                    Api.logout.Output.Write(ReadReply.Succeeded())
                                    |> fun reply -> RouteSupport.reply reply context
                | _ -> return RouteSupport.hostFailure context HostFailureReason.SessionRejected
        }

    let health
        (readiness: System.Threading.CancellationToken -> Task<bool>)
        (context: HttpContext)
        (next: RequestDelegate)
        : Task =
        task {
            match context.Request.Method, context.Request.Path.Value with
            | "GET", "/health/live" -> context.Response.StatusCode <- 200
            | "GET", "/health/ready" ->
                let! ready = readiness context.RequestAborted
                context.Response.StatusCode <- if ready then 200 else 503
            | _, ("/health/live" | "/health/ready") ->
                do!
                    (RouteSupport.hostFailure context HostFailureReason.MethodRejected).ExecuteAsync
                        context
            | _ -> do! next.Invoke context
        }

    let private fallback assets (context: HttpContext) : Task =
        task {
            HttpHeaders.noStore context

            if context.Request.Path.StartsWithSegments(PathString "/api") then
                let known =
                    Api.all
                    |> List.exists (fun endpoint ->
                        Some(Api.path endpoint) = Option.ofObj context.Request.Path.Value)

                let reason =
                    if known then
                        HostFailureReason.MethodRejected
                    else
                        HostFailureReason.EndpointMissing

                do! (RouteSupport.hostFailure context reason).ExecuteAsync context
            else
                do!
                    Results
                        .File(Path.Combine(assets, "index.html"), "text/html; charset=utf-8")
                        .ExecuteAsync
                        context
        }

    let map assets configuration withActor (application: WebApplication) =
        Dispatch.check ()
        let files = StaticFileOptions()

        files.OnPrepareResponse <-
            Action<StaticFileResponseContext>(fun response -> HttpHeaders.noStore response.Context)

        application.UseDefaultFiles() |> ignore
        application.UseStaticFiles files |> ignore

        for endpoint in Api.all do
            let handler =
                match (Api.id endpoint) with
                | "session" -> session configuration
                | "session.logout" -> logout configuration
                | _ -> ApiRoutes.handle endpoint configuration withActor

            let exact (context: HttpContext) =
                if Option.ofObj context.Request.Path.Value <> Some(Api.path endpoint) then
                    Task.FromResult(
                        RouteSupport.hostFailure context HostFailureReason.EndpointMissing
                    )
                elif context.Request.Method <> ((Api.method endpoint).Method) then
                    Task.FromResult(
                        RouteSupport.hostFailure context HostFailureReason.MethodRejected
                    )
                else
                    handler context

            let route =
                application.MapMethods(
                    (Api.path endpoint),
                    [| ((Api.method endpoint).Method) |],
                    Func<HttpContext, Task<IResult>>(exact)
                )

            if (Api.id endpoint) <> "session" then
                route.RequireRateLimiting "core" |> ignore

        application
            .MapGet(
                "/auth/login",
                Func<HttpContext, Task>(fun context ->
                    HttpHeaders.noStore context
                    context.ChallengeAsync("OpenIdConnect", loginReturnProperties ()))
            )
            .RequireRateLimiting("login")
        |> ignore

        application.MapFallback(RequestDelegate(fallback assets)) |> ignore
