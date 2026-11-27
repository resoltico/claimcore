namespace ClaimCore.Server

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Security.Claims
open System.Net.Http
open System.Net.Security
open System.Security.Cryptography.X509Certificates
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Authentication.OpenIdConnect
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Microsoft.IdentityModel.Tokens
open ClaimCore.HostSecurity

module AuthMiddleware =
    [<Literal>]
    let BearerScheme = "ClaimCore.Bearer"

    let private callbackPath = PathString "/signin-oidc"
    let private signedOutCallbackPath = PathString "/signout-callback-oidc"
    let private remoteSignOutPath = PathString "/signout-oidc"

    let internal interactiveRequest (path: PathString) =
        [
            callbackPath
            signedOutCallbackPath
            remoteSignOutPath
            PathString "/auth/login"
        ]
        |> List.exists (fun candidate ->
            String.Equals(candidate.Value, path.Value, StringComparison.OrdinalIgnoreCase))

    let private acceptedAlgorithms =
        [| SecurityAlgorithms.RsaSha256; SecurityAlgorithms.RsaSsaPssSha256 |]

    let internal syntheticTrustHandler (root: X509Certificate2) =
        let handler = new HttpClientHandler(AllowAutoRedirect = false)

        handler.ServerCertificateCustomValidationCallback <-
            Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool>
                (fun _ certificate _ errors ->
                    if
                        (errors &&& SslPolicyErrors.RemoteCertificateNotAvailable)
                        <> SslPolicyErrors.None
                        || (errors &&& SslPolicyErrors.RemoteCertificateNameMismatch)
                           <> SslPolicyErrors.None
                    then
                        false
                    else
                        TlsCertificatePurpose.customRootServerAuthentication root certificate)

        handler

    let internal productionTrustHandler () =
        new HttpClientHandler(AllowAutoRedirect = false, CheckCertificateRevocationList = true)

    let internal idTokenProperty = BrowserTicket.idTokenProperty

    let private requireHuman (configuration: OidcConfiguration) (context: TokenValidatedContext) =
        match context.Principal |> Option.ofObj with
        | Some principal ->
            match PrincipalIdentity.fromBrowserSession configuration.Issuer principal with
            | Error _ ->
                context.Fail(AuthenticationFailure.token AuthenticationFailure.SubjectInvalid)
            | Ok _ ->
                let cookies =
                    context.HttpContext.RequestServices
                        .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                        .Get(CookieAuthenticationDefaults.AuthenticationScheme)

                if not (isNull cookies.SessionStore) then
                    match
                        context.Properties,
                        context.TokenEndpointResponse,
                        PrincipalIdentity.browserAuthentication principal
                    with
                    | NonNull properties, NonNull response, Some proof ->
                        BrowserTicket.recordProof properties proof

                        match response.IdToken with
                        | NonNull token when not (String.IsNullOrWhiteSpace token) ->
                            properties.Items[idTokenProperty] <- token
                        | _ ->
                            context.Fail(
                                AuthenticationFailure.token AuthenticationFailure.PrincipalInvalid
                            )
                    | _ ->
                        context.Fail(
                            AuthenticationFailure.token AuthenticationFailure.PrincipalInvalid
                        )
        | None -> context.Fail(AuthenticationFailure.token AuthenticationFailure.SubjectInvalid)

        Task.CompletedTask

    let private cookie absolute (options: CookieAuthenticationOptions) =
        options.Cookie.Name <- "__Host-ClaimCoreSession"
        options.Cookie.HttpOnly <- true
        options.Cookie.IsEssential <- true
        options.Cookie.SecurePolicy <- Microsoft.AspNetCore.Http.CookieSecurePolicy.Always
        options.Cookie.SameSite <- Microsoft.AspNetCore.Http.SameSiteMode.Lax
        options.Cookie.Path <- "/"
        options.SlidingExpiration <- false
        options.ExpireTimeSpan <- absolute

        // A completed OIDC challenge creates a new ticket, even if this browser
        // still holds a previous session cookie. Correlation and nonce cookies are unchanged.
        let cookies = Microsoft.AspNetCore.Authentication.Cookies.ChunkingCookieManager()

        options.CookieManager <-
            { new Microsoft.AspNetCore.Authentication.Cookies.ICookieManager with
                member _.GetRequestCookie(context, key) =
                    if context.Request.Path = callbackPath then
                        null
                    else
                        cookies.GetRequestCookie(context, key)

                member _.AppendResponseCookie(context, key, value, settings) =
                    cookies.AppendResponseCookie(context, key, value, settings)

                member _.DeleteCookie(context, key, settings) =
                    cookies.DeleteCookie(context, key, settings)
            }

        options.Events.OnRedirectToLogin <-
            Func<RedirectContext<CookieAuthenticationOptions>, Task>(fun context ->
                context.Response.StatusCode <- 401
                Task.CompletedTask)

    let private oidc
        (syntheticRoot: X509Certificate2 option)
        idTokenSink
        (metadata: IssuerMetadata)
        (configuration: OidcConfiguration)
        (options: OpenIdConnectOptions)
        =
        options.CallbackPath <- callbackPath
        options.SignedOutCallbackPath <- signedOutCallbackPath
        options.RemoteSignOutPath <- remoteSignOutPath
        options.Authority <- configuration.Issuer.AbsoluteUri
        options.RequireHttpsMetadata <- true
        options.ClientId <- configuration.ClientId
        options.ClientSecret <- configuration.ClientSecret
        options.ResponseType <- "code"
        options.UsePkce <- true

        if syntheticRoot.IsSome then
            options.PushedAuthorizationBehavior <- PushedAuthorizationBehavior.Disable

        options.Backchannel <- metadata.Client
        options.ConfigurationManager <- metadata

        options.SaveTokens <- false
        options.MapInboundClaims <- false
        options.GetClaimsFromUserInfoEndpoint <- false
        options.Scope.Clear()
        options.Scope.Add("openid")
        options.TokenValidationParameters.ValidateIssuer <- true
        options.TokenValidationParameters.ValidIssuer <- configuration.Issuer.AbsoluteUri
        options.TokenValidationParameters.ValidateAudience <- true
        options.TokenValidationParameters.ValidAudience <- configuration.ClientId
        options.TokenValidationParameters.ValidateIssuerSigningKey <- true
        options.TokenValidationParameters.ValidateLifetime <- true
        options.TokenValidationParameters.ValidAlgorithms <- acceptedAlgorithms
        options.TokenValidationParameters.ValidTypes <- [| "JWT" |]
        options.TokenValidationParameters.ClockSkew <- TimeSpan.FromMinutes(1.)

        options.Events.OnTokenValidated <-
            Func<TokenValidatedContext, Task>(requireHuman configuration)

        idTokenSink
        |> Option.iter (fun sink ->
            options.Events.OnTokenResponseReceived <-
                Func<TokenResponseReceivedContext, Task>(fun context ->
                    context.TokenEndpointResponse.IdToken |> Option.ofObj |> Option.iter sink

                    Task.CompletedTask))

        options.Events.OnRedirectToIdentityProvider <-
            Func<RedirectContext, Task>(fun context ->
                task {
                    match context.Properties.Items.TryGetValue "ui_locales" with
                    | true, language -> context.ProtocolMessage.UiLocales <- language
                    | _ -> ()

                    context.ProtocolMessage.Prompt <- "login"
                    context.ProtocolMessage.MaxAge <- "0"

                    let cookies =
                        context.HttpContext.RequestServices
                            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                            .Get(CookieAuthenticationDefaults.AuthenticationScheme)

                    match cookies.SessionStore with
                    | :? OidcTicketStore as store ->
                        let! flow = store.StartFlow context.HttpContext.RequestAborted
                        context.Properties.Items[BrowserTicket.flowProperty] <- flow.ToString("D")
                    | _ -> ()
                })

        options.Events.OnRemoteFailure <-
            Func<RemoteFailureContext, Task>(fun context ->
                context.HandleResponse()

                context.Response.StatusCode <-
                    if OidcDependencyException.tryFind context.Failure |> Option.isSome then
                        StatusCodes.Status503ServiceUnavailable
                    else
                        StatusCodes.Status401Unauthorized

                context.Response.ContentType <- "text/html; charset=utf-8"
                context.Response.Headers["Referrer-Policy"] <- "no-referrer"
                HttpHeaders.noStore context.HttpContext

                let inventory =
                    context.HttpContext.RequestServices.GetRequiredService<PublicAssets.Inventory>()

                let recovery = inventory.Recovery
                context.Response.ContentLength <- Nullable(int64 recovery.Length)

                context.Response.Body
                    .WriteAsync(recovery, context.HttpContext.RequestAborted)
                    .AsTask())

    let private bearer
        (syntheticRoot: X509Certificate2 option)
        (metadata: IssuerMetadata)
        (configuration: OidcConfiguration)
        (options: JwtBearerOptions)
        =
        options.Authority <- configuration.Issuer.AbsoluteUri
        options.RequireHttpsMetadata <- true

        options.Backchannel <- metadata.Client
        options.ConfigurationManager <- metadata

        options.Audience <- configuration.ApiAudience
        options.MapInboundClaims <- false
        options.SaveToken <- false
        options.TokenValidationParameters.ValidateIssuer <- true
        options.TokenValidationParameters.ValidIssuer <- configuration.Issuer.AbsoluteUri
        options.TokenValidationParameters.ValidateAudience <- true
        options.TokenValidationParameters.ValidAudience <- configuration.ApiAudience
        options.TokenValidationParameters.ValidateIssuerSigningKey <- true
        options.TokenValidationParameters.ValidateLifetime <- true
        options.TokenValidationParameters.ValidAlgorithms <- acceptedAlgorithms
        options.TokenValidationParameters.ValidTypes <- [| "JWT"; "at+jwt" |]

        options.TokenValidationParameters.ClockSkew <-
            if syntheticRoot.IsNone then
                TimeSpan.FromMinutes(1.)
            else
                TimeSpan.Zero

        options.Events.OnAuthenticationFailed <-
            Func<Microsoft.AspNetCore.Authentication.JwtBearer.AuthenticationFailedContext, Task>
                (fun context ->
                    if OidcDependencyException.tryFind context.Exception |> Option.isSome then
                        context.Fail context.Exception

                    Task.CompletedTask)

        options.RefreshOnIssuerKeyNotFound <- true

    let private configureWith
        (syntheticRoot: X509Certificate2 option)
        idTokenSink
        (configuration: OidcConfiguration)
        absolute
        (services: IServiceCollection)
        =
        services.AddSingleton<IssuerMetadata>(fun _ ->
            let handler =
                syntheticRoot
                |> Option.orElse configuration.TrustRoot
                |> Option.map syntheticTrustHandler
                |> Option.defaultWith productionTrustHandler

            new IssuerMetadata(
                configuration,
                new HttpClient(handler, Timeout = TimeSpan.FromSeconds 10.),
                TimeProvider.System
            ))
        |> ignore

        services
            .AddAuthentication(fun options ->
                options.DefaultScheme <- CookieAuthenticationDefaults.AuthenticationScheme
                options.DefaultChallengeScheme <- OpenIdConnectDefaults.AuthenticationScheme)
            .AddCookie(cookie absolute)
            .AddOpenIdConnect()
            .AddJwtBearer(BearerScheme)
        |> ignore

        services
            .AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IssuerMetadata>(fun options metadata ->
                oidc syntheticRoot idTokenSink metadata configuration options)
        |> ignore

        services
            .AddOptions<JwtBearerOptions>(BearerScheme)
            .Configure<IssuerMetadata>(fun options metadata ->
                bearer syntheticRoot metadata configuration options)
        |> ignore

    let configure (configuration: OidcConfiguration) absolute (services: IServiceCollection) =
        if configuration.Issuer.Scheme <> Uri.UriSchemeHttps then
            invalidArg (nameof configuration) "Production OIDC requires HTTPS."

        configureWith None None configuration absolute services

    /// Internal-only synthetic fixture seam. Production configuration never calls this path.
    let internal configureSyntheticLoopback
        (configuration: OidcConfiguration)
        (root: X509Certificate2)
        idTokenSink
        absolute
        (services: IServiceCollection)
        =
        if
            configuration.Issuer.Scheme <> Uri.UriSchemeHttps
            || not configuration.Issuer.IsLoopback
        then
            invalidArg (nameof configuration) "Synthetic OIDC requires loopback HTTPS."

        configureWith (Some root) (Some idTokenSink) configuration absolute services
