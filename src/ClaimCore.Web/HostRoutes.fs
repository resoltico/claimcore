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
    let private hostFailure context reason =
        task {
            HttpHeaders.noStore context
            do! (RouteSupport.hostFailure context reason).ExecuteAsync(context)
        }

    let private useStaticAssets (application: WebApplication) =
        let files = StaticFileOptions()

        files.OnPrepareResponse <-
            Action<StaticFileResponseContext>(fun context -> HttpHeaders.noStore context.Context)

        application.UseDefaultFiles() |> ignore
        application.UseStaticFiles(files) |> ignore

    let private mapFallback assets (application: WebApplication) =
        application.MapFallback(
            RequestDelegate(fun context ->
                task {
                    HttpHeaders.noStore context

                    if context.Request.Path.StartsWithSegments(PathString("/api")) then
                        do! hostFailure context WebHostFailure.EndpointMissing
                    else
                        let index = Path.Combine(assets, "index.html")
                        do! Results.File(index, "text/html; charset=utf-8").ExecuteAsync(context)
                }
                :> Task)
        )
        |> ignore

    let private sessionSnapshot endpoint (context: HttpContext) authenticated =
        let antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>()
        let token = antiforgery.GetAndStoreTokens(context).RequestToken
        WebWire.session endpoint authenticated token

    let private mapSessionRead sessions (application: WebApplication) =
        application.MapGet(
            (WebContract.path "session"),
            Func<HttpContext, IResult>(fun context ->
                HttpHeaders.noStore context
                sessionSnapshot "session" context (Admission.isCurrentSession sessions context))
        )
        |> ignore

    let internal invalidLoginBody context message =
        RouteSupport.inputFailure context message

    let private loginSucceeded (sessions: SessionRegistry) (context: HttpContext) =
        task {
            let id = sessions.Create(DateTimeOffset.UtcNow)
            let identity = ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme)
            identity.AddClaim(Claim("claimcore-session", id))
            let principal = ClaimsPrincipal(identity)
            do! context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal)
            context.User <- principal
            return sessionSnapshot "session.login" context true
        }

    let private loginRejected (context: HttpContext) =
        RouteSupport.hostFailure context WebHostFailure.LoginRejected

    let private suppliedAntiforgeryToken (context: HttpContext) =
        match context.Request.Headers.TryGetValue("X-ClaimCore-Antiforgery") with
        | true, values when values.Count = 1 -> Some values[0]
        | _ -> None

    let internal authenticateLogin bootstrap sessions context (input: LoginInput) =
        match suppliedAntiforgeryToken context with
        | Some header when String.Equals(header, input.AntiforgeryToken, StringComparison.Ordinal) ->
            if Security.isBootstrapCredential bootstrap input.Credential then
                loginSucceeded sessions context
            else
                Task.FromResult(loginRejected context)
        | _ -> Task.FromResult(loginRejected context)

    let private loginHandler configuration bootstrap sessions (context: HttpContext) =
        task {
            HttpHeaders.noStore context
            let antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>()

            match!
                Admission.validatePost
                    configuration.Origin
                    RequestBody.Json
                    4096
                    antiforgery
                    context
            with
            | Error failure -> return RouteSupport.admissionFailure context failure
            | Ok() ->
                match! HttpInput.readBounded 4096 context.Request.Body with
                | Error message -> return invalidLoginBody context message
                | Ok bytes ->
                    match HttpInput.login bytes with
                    | Error message -> return invalidLoginBody context message
                    | Ok input -> return! authenticateLogin bootstrap sessions context input
        }

    let private logoutHandler configuration sessions (context: HttpContext) =
        task {
            HttpHeaders.noStore context
            let antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>()

            match!
                Admission.admitAuthenticated
                    configuration.Origin
                    RequestBody.Json
                    1024
                    sessions
                    antiforgery
                    context
            with
            | Error failure -> return RouteSupport.admissionFailure context failure
            | Ok() ->
                match! HttpInput.readBounded 1024 context.Request.Body with
                | Error message -> return RouteSupport.inputFailure context message
                | Ok bytes ->
                    match HttpInput.logout bytes with
                    | Error message -> return RouteSupport.inputFailure context message
                    | Ok() ->
                        Security.sessionId context.User.Claims |> Option.iter sessions.Revoke
                        do! context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
                        context.User <- ClaimsPrincipal(ClaimsIdentity())
                        return sessionSnapshot "session.logout" context false
        }

    let private mapSessionRoutes configuration bootstrap sessions (application: WebApplication) =
        mapSessionRead sessions application

        (application.MapPost(
            (WebContract.jsonPath "session.login"),
            Func<HttpContext, Task<IResult>>(loginHandler configuration bootstrap sessions)
        ))
            .RequireRateLimiting("login")
        |> ignore

        application.MapPost(
            (WebContract.jsonPath "session.logout"),
            Func<HttpContext, Task<IResult>>(logoutHandler configuration sessions)
        )
        |> ignore

    let private sessionFailure context =
        RouteSupport.hostFailure context WebHostFailure.SessionRejected

    let private mapDefinition sessions (core: IClaimsCore) (application: WebApplication) =
        (application.MapGet(
            (WebContract.path "definition"),
            Func<HttpContext, IResult>(fun context ->
                HttpHeaders.noStore context

                if Admission.isCurrentSession sessions context then
                    core.Describe() |> WebWire.description
                else
                    sessionFailure context)
        ))
            .RequireRateLimiting("core")
        |> ignore

    let map
        assets
        configuration
        bootstrap
        sessions
        (core: IClaimsCore)
        (application: WebApplication)
        =
        useStaticAssets application
        mapSessionRoutes configuration bootstrap sessions application
        mapDefinition sessions core application
        ApiRoutes.map configuration sessions core application

        application.MapGet(
            "/health/live",
            Func<HttpContext, IResult>(fun context ->
                HttpHeaders.noStore context
                WebWire.liveness)
        )
        |> ignore

        mapFallback assets application
