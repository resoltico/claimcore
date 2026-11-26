namespace ClaimCore.Server

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Json


open System
open System.Security.Claims
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.AspNetCore.WebUtilities
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Server.Composition

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
                        match configuration.Oidc, RequestGate.credentialMode context with
                        | Some oidc, CredentialMode.BrowserCookie ->
                            let! principal = RequestGate.actorGet oidc configuration.Binding context in
                            return Result.isOk principal
                        | _ -> return false
                    }

                let antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>()
                let token = antiforgery.GetAndStoreTokens(context).RequestToken |> Option.ofObj

                return
                    Endpoint.reply
                        Api.session
                        (Outcome.Succeeded(
                            Presented.create
                                (fun reply ->
                                    ContextScreen.sessionView
                                        reply.Authenticated
                                        reply.ContractIdentity)
                                {
                                    Authenticated = authenticated
                                    ContractIdentity = ContractIdentity.value
                                    AntiforgeryToken = token
                                }
                        ))
                    |> fun reply -> RouteSupport.reply reply context
        }

    let private identityFailure =
        function
        | OidcDependencyFailure.Unavailable -> HostFailureReason.IdentityProviderUnavailable
        | OidcDependencyFailure.Untrusted -> HostFailureReason.IdentityProviderUntrusted

    let private endSession fallback (context: HttpContext) =
        task {
            match context.RequestServices.GetService<IssuerMetadata>() |> Option.ofObj with
            | Some issuer ->
                let! metadata = issuer.Get context.RequestAborted
                return metadata |> Result.map (fun value -> Uri value.EndSessionEndpoint)
            | None ->
                return
                    fallback
                    |> Option.map Ok
                    |> Option.defaultValue (Error OidcDependencyFailure.Unavailable)
        }

    let private logout configuration fallback (context: HttpContext) =
        task {
            if not (ApiRoutes.handshake context) then
                return RouteSupport.hostFailure context HostFailureReason.ContractMismatch
            else
                match configuration.Oidc, RequestGate.credentialMode context with
                | Some oidc, CredentialMode.BrowserCookie ->
                    let antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>()

                    match!
                        RequestGate.actorPost oidc configuration.Binding 1024 antiforgery context
                    with
                    | Error reason -> return RouteSupport.gateFailure context reason
                    | Ok _ ->
                        match! HttpBody.readRequest 1024 context with
                        | Error problem -> return RouteSupport.inputFailure context problem
                        | Ok bytes ->
                            match Codec.decode 1024 Api.logout.Input bytes with
                            | Error _ ->
                                return
                                    RouteSupport.hostFailure context HostFailureReason.InputInvalid
                            | Ok _ ->
                                let! ticket =
                                    context.AuthenticateAsync(
                                        CookieAuthenticationDefaults.AuthenticationScheme
                                    )

                                let hint =
                                    match ticket.Properties with
                                    | null -> None
                                    | properties ->
                                        match
                                            properties.Items.TryGetValue
                                                AuthMiddleware.idTokenProperty
                                        with
                                        | true, NonNull token -> Some token
                                        | _ -> None

                                do!
                                    context.SignOutAsync(
                                        CookieAuthenticationDefaults.AuthenticationScheme
                                    )

                                context.User <- ClaimsPrincipal(ClaimsIdentity())

                                let! handoff =
                                    task {
                                        match hint with
                                        | None ->
                                            return
                                                LogoutHandoff.LocalOnly
                                                    LocalSignOutReason.NoProviderSession
                                        | Some token ->
                                            match! endSession fallback context with
                                            | Error OidcDependencyFailure.Unavailable ->
                                                return
                                                    LogoutHandoff.LocalOnly
                                                        LocalSignOutReason
                                                            .IdentityProviderUnavailable
                                            | Error OidcDependencyFailure.Untrusted ->
                                                return
                                                    LogoutHandoff.LocalOnly
                                                        LocalSignOutReason.IdentityProviderUntrusted
                                            | Ok endpoint ->
                                                let parameters =
                                                    dict
                                                        [
                                                            "client_id", oidc.ClientId
                                                            "id_token_hint", token
                                                            "post_logout_redirect_uri",
                                                            Uri(configuration.Binding.Origin, "/")
                                                                .AbsoluteUri
                                                        ]

                                                return
                                                    LogoutHandoff.Provider(
                                                        QueryHelpers.AddQueryString(
                                                            endpoint.AbsoluteUri,
                                                            parameters
                                                        )
                                                    )
                                    }

                                return
                                    Endpoint.reply
                                        Api.logout
                                        (Outcome.Succeeded(
                                            Presented.create
                                                (fun _ -> ContextScreen.logoutView ())
                                                handoff
                                        ))
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

    let identityHealth (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            if context.Request.Path.Value <> "/health/identity" then
                do! next.Invoke context
            else
                HttpHeaders.noStore context

                if not (HttpMethods.IsGet context.Request.Method) then
                    do!
                        (RouteSupport.hostFailure context HostFailureReason.MethodRejected)
                            .ExecuteAsync
                            context
                else
                    let issuer = context.RequestServices.GetRequiredService<IssuerMetadata>()

                    match! issuer.Get context.RequestAborted with
                    | Ok _ -> context.Response.StatusCode <- StatusCodes.Status200OK
                    | Error failure ->
                        do!
                            (RouteSupport.hostFailure context (identityFailure failure))
                                .ExecuteAsync
                                context
        }

    let private fallback (assets: PublicAssets.Inventory) (context: HttpContext) : Task =
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
            elif
                [ "/assets"; "/locales"; "/auth"; "/health" ]
                |> List.exists (fun prefix ->
                    context.Request.Path.StartsWithSegments(PathString prefix))
            then
                do!
                    (RouteSupport.hostFailure context HostFailureReason.EndpointMissing)
                        .ExecuteAsync
                        context
            else
                do! assets.ServeIndex context
        }

    let private attachmentViewer
        (viewer: PublicAssets.Viewer)
        (context: HttpContext)
        (next: RequestDelegate)
        : Task =
        task {
            if context.Request.Path.Value <> AttachmentTransport.viewerPath then
                do! next.Invoke context
            elif
                HttpMethods.IsGet context.Request.Method
                || HttpMethods.IsHead context.Request.Method
            then
                HttpHeaders.viewer viewer.ScriptHash context
                context.Response.ContentType <- "text/html; charset=utf-8"
                context.Response.ContentLength <- Nullable(int64 viewer.Bytes.Length)

                if HttpMethods.IsGet context.Request.Method then
                    do!
                        context.Response.Body
                            .WriteAsync(viewer.Bytes, context.RequestAborted)
                            .AsTask()
            else
                context.Response.StatusCode <- StatusCodes.Status405MethodNotAllowed
                context.Response.Headers.Allow <- "GET, HEAD"
        }

    let internal publicAssets (publicFiles: PublicAssets.Inventory) (application: WebApplication) =

        application.Use(
            Func<HttpContext, RequestDelegate, Task>(attachmentViewer publicFiles.Viewer)
        )
        |> ignore

        application.Use(
            Func<HttpContext, RequestDelegate, Task>(fun context next ->
                publicFiles.Serve(context, next))
        )
        |> ignore

        publicFiles

    let internal map
        assets
        configuration
        endSessionEndpoint
        handlers
        withActor
        (application: WebApplication)
        =
        application.MapPost(
            ExportTransport.path,
            Func<HttpContext, Task<IResult>>(ExportRoutes.handle configuration withActor)
        )
        |> ignore

        for transfer in AttachmentTransport.all do
            application.MapPost(
                AttachmentTransport.path transfer,
                Func<HttpContext, Task>(AttachmentRoutes.handle transfer configuration withActor)
            )
            |> ignore

        for endpoint in Api.all do
            let handler =
                match Api.identity endpoint with
                | EndpointId.Host HostEndpoint.Session -> session configuration
                | EndpointId.Host HostEndpoint.Logout -> logout configuration endSessionEndpoint
                | EndpointId.Actor actor ->
                    ApiRoutes.handle (handlers actor) configuration withActor

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

            route |> ignore

        application
            .MapGet(
                "/auth/login",
                Func<HttpContext, Task>(fun context ->
                    task {
                        HttpHeaders.noStore context

                        let! available =
                            task {
                                match
                                    context.RequestServices.GetService<IssuerMetadata>()
                                    |> Option.ofObj
                                with
                                | None -> return Ok()
                                | Some issuer ->
                                    let! answer = issuer.Get context.RequestAborted
                                    return answer |> Result.map ignore
                            }

                        match available with
                        | Error failure ->
                            do!
                                (RouteSupport.hostFailure context (identityFailure failure))
                                    .ExecuteAsync
                                    context
                        | Ok() ->
                            let properties = loginReturnProperties ()
                            let language = context.Request.Query["language"]

                            if
                                language.Count = 1
                                && List.contains language[0] LocalizationPolicy.languages
                            then
                                properties.Items["ui_locales"] <- language[0]

                            do! context.ChallengeAsync("OpenIdConnect", properties)
                    })
            )
            .RequireRateLimiting("login")
        |> ignore

        for pattern in
            [
                "/cases/{**path}"
                "/operations/{**path}"
                "/people/{**path}"
                "/work/{**path}"
            ] do
            application.MapFallback(pattern, RequestDelegate(fallback assets)) |> ignore

        application.MapFallback(RequestDelegate(fallback assets)) |> ignore

        application.MapFallback(
            "/{**path}",
            RequestDelegate(fun context ->
                (RouteSupport.hostFailure context HostFailureReason.EndpointMissing).ExecuteAsync
                    context)
        )
        |> ignore
