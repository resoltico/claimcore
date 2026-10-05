namespace ClaimCore.Web

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
open ClaimCore.Application
open ClaimCore.Contracts

module HostRoutes =
    let internal loginReturnProperties () =
        let properties = AuthenticationProperties()
        properties.RedirectUri <- "/"
        properties

    let private useStaticAssets (application: WebApplication) =
        let files = StaticFileOptions()

        files.OnPrepareResponse <-
            Action<StaticFileResponseContext>(fun response -> HttpHeaders.noStore response.Context)

        application.UseDefaultFiles() |> ignore
        application.UseStaticFiles(files) |> ignore

    let private sessionSnapshot endpoint (context: HttpContext) authenticated =
        let antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>()
        let token = antiforgery.GetAndStoreTokens(context).RequestToken
        WebWire.session endpoint authenticated token

    let private currentBrowser configuration (context: HttpContext) =
        task {
            match configuration.Oidc, Admission.credentialMode context with
            | Some oidc, CredentialMode.BrowserCookie ->
                match! Admission.actorGet oidc configuration.Origin context with
                | Ok _ -> return true
                | Error _ -> return false
            | _ -> return false
        }

    let private sessionRead configuration (context: HttpContext) =
        task {
            HttpHeaders.noStore context
            let! authenticated = currentBrowser configuration context
            return sessionSnapshot "session" context authenticated
        }

    let private sessionLogout configuration (context: HttpContext) =
        task {
            HttpHeaders.noStore context

            match configuration.Oidc, Admission.credentialMode context with
            | Some oidc, CredentialMode.BrowserCookie ->
                let antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>()

                match!
                    Admission.actorPost
                        oidc
                        configuration.Origin
                        RequestBody.Json
                        1024
                        antiforgery
                        context
                with
                | Error failure -> return RouteSupport.admissionFailure context failure
                | Ok _ ->
                    match! HttpBody.readBounded 1024 context.Request.Body with
                    | Error problem -> return RouteSupport.inputFailure context problem
                    | Ok bytes ->
                        match HttpInput.logout bytes with
                        | Error problem -> return RouteSupport.inputFailure context problem
                        | Ok() ->
                            do!
                                context.SignOutAsync(
                                    CookieAuthenticationDefaults.AuthenticationScheme
                                )

                            context.User <- ClaimsPrincipal(ClaimsIdentity())
                            return sessionSnapshot "session.logout" context false
            | _ -> return RouteSupport.admissionFailure context AdmissionFailure.SessionRejected
        }

    let private definition
        configuration
        (forActor: ClaimCore.Application.PrincipalKey -> IActorClaimsCore)
        (context: HttpContext)
        =
        task {
            HttpHeaders.noStore context

            match configuration.Oidc with
            | None -> return RouteSupport.hostFailure context WebHostFailure.SessionRejected
            | Some oidc ->
                match! Admission.actorGet oidc configuration.Origin context with
                | Error failure -> return RouteSupport.admissionFailure context failure
                | Ok principal ->
                    match! (forActor principal).Definition(context.RequestAborted) with
                    | QueryOutcome.Succeeded value -> return WebWire.description value
                    | _ -> return RouteSupport.hostFailure context WebHostFailure.SessionForbidden
        }

    let private mapSession configuration (application: WebApplication) =
        application.MapGet(
            WebContract.path "session",
            Func<HttpContext, Task<IResult>>(sessionRead configuration)
        )
        |> ignore

        application.MapPost(
            WebContract.jsonPath "session.logout",
            Func<HttpContext, Task<IResult>>(sessionLogout configuration)
        )
        |> ignore

        application
            .MapGet(
                "/auth/login",
                Func<HttpContext, Task>(fun context ->
                    HttpHeaders.noStore context
                    context.ChallengeAsync("OpenIdConnect", loginReturnProperties ()))
            )
            .RequireRateLimiting("login")
        |> ignore

    let private mapFallback assets (application: WebApplication) =
        application.MapFallback(
            RequestDelegate(fun context ->
                task {
                    HttpHeaders.noStore context

                    if context.Request.Path.StartsWithSegments(PathString("/api")) then
                        do!
                            (RouteSupport.hostFailure context WebHostFailure.EndpointMissing)
                                .ExecuteAsync(context)
                    else
                        do!
                            Results
                                .File(
                                    Path.Combine(assets, "index.html"),
                                    "text/html; charset=utf-8"
                                )
                                .ExecuteAsync(context)
                }
                :> Task)
        )
        |> ignore

    let map
        assets
        configuration
        (forActor: ClaimCore.Application.PrincipalKey -> IActorClaimsCore)
        (readiness: System.Threading.CancellationToken -> Task<string * string * bool>)
        (application: WebApplication)
        =
        useStaticAssets application
        mapSession configuration application

        application
            .MapGet(
                WebContract.path "definition",
                Func<HttpContext, Task<IResult>>(definition configuration forActor)
            )
            .RequireRateLimiting("core")
        |> ignore

        ApiRoutes.map configuration forActor application

        application.MapGet(
            "/health/live",
            Func<HttpContext, IResult>(fun context ->
                HttpHeaders.noStore context
                WebWire.liveness)
        )
        |> ignore

        application.MapGet(
            "/health/ready",
            Func<HttpContext, Task<IResult>>(fun context ->
                task {
                    HttpHeaders.noStore context
                    let! scope, phase, ready = readiness context.RequestAborted
                    context.Response.Headers["X-ClaimCore-Data-Use-Scope"] <- scope
                    context.Response.Headers["X-ClaimCore-Data-Use-Phase"] <- phase

                    context.Response.Headers["X-ClaimCore-Real-Data-Ready"] <-
                        if ready then "true" else "false"

                    return Results.StatusCode(if ready then 200 else 503)
                })
        )
        |> ignore

        mapFallback assets application
