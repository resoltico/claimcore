namespace ClaimCore.Web

open System
open System.Security.Claims
open System.Net.Http
open System.Net.Security
open System.Security.Cryptography.X509Certificates
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Authentication.OpenIdConnect
open Microsoft.Extensions.DependencyInjection
open Microsoft.IdentityModel.Tokens
open ClaimCore.HostSecurity

module AuthMiddleware =
    [<Literal>]
    let BearerScheme = "ClaimCore.Bearer"

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

    let private requireHuman (configuration: OidcConfiguration) (context: TokenValidatedContext) =
        match context.Principal |> Option.ofObj with
        | Some principal ->
            match PrincipalIdentity.fromBrowserSession configuration.Issuer principal with
            | Error _ -> context.Fail("OIDC_SUBJECT_INVALID")
            | Ok _ -> ()
        | None -> context.Fail("OIDC_SUBJECT_INVALID")

        Task.CompletedTask

    let private cookie idle absolute (options: CookieAuthenticationOptions) =
        options.Cookie.Name <- "__Host-ClaimCoreSession"
        options.Cookie.HttpOnly <- true
        options.Cookie.IsEssential <- true
        options.Cookie.SecurePolicy <- Microsoft.AspNetCore.Http.CookieSecurePolicy.Always
        options.Cookie.SameSite <- Microsoft.AspNetCore.Http.SameSiteMode.Lax
        options.Cookie.Path <- "/"
        options.SlidingExpiration <- false
        options.ExpireTimeSpan <- absolute
        options.SessionStore <- OidcTicketStore(idle, absolute)

        options.Events.OnRedirectToLogin <-
            Func<RedirectContext<CookieAuthenticationOptions>, Task>(fun context ->
                context.Response.StatusCode <- 401
                Task.CompletedTask)

    let private oidc
        (syntheticRoot: X509Certificate2 option)
        idTokenSink
        (configuration: OidcConfiguration)
        (options: OpenIdConnectOptions)
        =
        options.Authority <- configuration.Issuer.AbsoluteUri
        options.RequireHttpsMetadata <- true
        options.ClientId <- configuration.ClientId
        options.ClientSecret <- configuration.ClientSecret
        options.ResponseType <- "code"
        options.UsePkce <- true

        if syntheticRoot.IsSome then
            options.PushedAuthorizationBehavior <- PushedAuthorizationBehavior.Disable

        options.BackchannelHttpHandler <-
            (syntheticRoot |> Option.orElse configuration.TrustRoot)
            |> Option.map syntheticTrustHandler
            |> Option.defaultWith productionTrustHandler

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

        options.Events.OnRemoteFailure <-
            Func<RemoteFailureContext, Task>(fun context ->
                context.HandleResponse()
                context.Response.StatusCode <- 401
                Task.CompletedTask)

    let private bearer
        (syntheticRoot: X509Certificate2 option)
        (configuration: OidcConfiguration)
        (options: JwtBearerOptions)
        =
        options.Authority <- configuration.Issuer.AbsoluteUri
        options.RequireHttpsMetadata <- true

        options.BackchannelHttpHandler <-
            (syntheticRoot |> Option.orElse configuration.TrustRoot)
            |> Option.map syntheticTrustHandler
            |> Option.defaultWith productionTrustHandler

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
        options.TokenValidationParameters.ValidTypes <- [| "JWT"; "at+jwt"; "Bearer" |]

        options.TokenValidationParameters.ClockSkew <-
            if syntheticRoot.IsNone then
                TimeSpan.FromMinutes(1.)
            else
                TimeSpan.Zero

        options.RefreshOnIssuerKeyNotFound <- true

        if syntheticRoot.IsSome then
            options.RefreshInterval <- TimeSpan.FromSeconds(1.)

    let private configureWith
        (syntheticRoot: X509Certificate2 option)
        idTokenSink
        (configuration: OidcConfiguration)
        idle
        absolute
        (services: IServiceCollection)
        =
        services
            .AddAuthentication(fun options ->
                options.DefaultScheme <- CookieAuthenticationDefaults.AuthenticationScheme
                options.DefaultChallengeScheme <- OpenIdConnectDefaults.AuthenticationScheme)
            .AddCookie(cookie idle absolute)
            .AddOpenIdConnect(oidc syntheticRoot idTokenSink configuration)
            .AddJwtBearer(BearerScheme, bearer syntheticRoot configuration)
        |> ignore

    let configure (configuration: OidcConfiguration) idle absolute (services: IServiceCollection) =
        if configuration.Issuer.Scheme <> Uri.UriSchemeHttps then
            invalidArg (nameof configuration) "Production OIDC requires HTTPS."

        configureWith None None configuration idle absolute services

    /// Internal-only synthetic fixture seam. Production configuration never calls this path.
    let internal configureSyntheticLoopback
        (configuration: OidcConfiguration)
        (root: X509Certificate2)
        idTokenSink
        idle
        absolute
        (services: IServiceCollection)
        =
        if
            configuration.Issuer.Scheme <> Uri.UriSchemeHttps
            || not configuration.Issuer.IsLoopback
        then
            invalidArg (nameof configuration) "Synthetic OIDC requires loopback HTTPS."

        configureWith (Some root) (Some idTokenSink) configuration idle absolute services
