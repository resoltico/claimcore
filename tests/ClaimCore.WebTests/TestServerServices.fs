module ClaimCore.WebTests.TestServerServices

open ClaimCore.Contracts
open System
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Threading
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Web

let origin = Uri("https://localhost:5443")
let credential = "synthetic-testserver-credential"
let digest = String.replicate 64 "a"

let private certificate () =
    use key = RSA.Create(2048)

    let request =
        CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

    let names = SubjectAlternativeNameBuilder()
    names.AddDnsName("localhost")
    request.CertificateExtensions.Add(names.Build())
    let usages = OidCollection()
    usages.Add(Oid("1.3.6.1.5.5.7.3.1")) |> ignore
    request.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(usages, false))

    request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1.), DateTimeOffset.UtcNow.AddDays(1.))

let configureServices loginPermits (services: IServiceCollection) =
    services.AddDataProtection().UseEphemeralDataProtectionProvider() |> ignore

    services.AddAntiforgery(fun options ->
        options.HeaderName <- "X-ClaimCore-Antiforgery"
        options.Cookie.Name <- "__Host-ClaimCoreAntiforgery"
        options.Cookie.SecurePolicy <- CookieSecurePolicy.Always)
    |> ignore

    services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(fun options ->
            options.Cookie.Name <- "__Host-ClaimCoreSession"
            options.Cookie.SecurePolicy <- CookieSecurePolicy.Always)
    |> ignore

    services.AddAuthorization() |> ignore

    services.AddRateLimiter(fun options ->
        options.RejectionStatusCode <- StatusCodes.Status429TooManyRequests

        options.OnRejected <-
            Func<OnRejectedContext, CancellationToken, ValueTask>(fun rejected _ ->
                HttpHeaders.noStore rejected.HttpContext

                ValueTask(
                    (WebWire.hostFailure WebHostFailure.Busy).ExecuteAsync(rejected.HttpContext)
                ))

        options.AddConcurrencyLimiter(
            "core",
            fun limiter ->
                limiter.PermitLimit <- 4
                limiter.QueueProcessingOrder <- QueueProcessingOrder.OldestFirst
                limiter.QueueLimit <- 16
        )
        |> ignore

        options.AddFixedWindowLimiter(
            "login",
            fun limiter ->
                limiter.PermitLimit <- loginPermits
                limiter.Window <- TimeSpan.FromMinutes(1.)
                limiter.QueueLimit <- 0
        )
        |> ignore)
    |> ignore

let testConfiguration assets loginPermits =
    {
        Origin = origin
        ConnectionString = "synthetic-not-opened"
        StateDirectory = assets
        Certificate = certificate ()
        SessionIdle = TimeSpan.FromMinutes(30.)
        SessionAbsolute = TimeSpan.FromHours(8.)
        Admission =
            {
                MaximumJsonBytes = 65536
                CorePermitLimit = 4
                CoreQueueLimit = 16
                LoginPermitLimit = loginPermits
            }
    }
