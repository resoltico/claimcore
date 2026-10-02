module ClaimCore.WebTests.TestServerServices

open System
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
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

let private admissionLimits loginPermits =
    {
        MaximumJsonBytes = 65536
        CorePermitLimit = 4
        CoreQueueLimit = 16
        LoginPermitLimit = loginPermits
    }

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

    RateLimits.configure (admissionLimits loginPermits) services

let testConfiguration assets loginPermits =
    {
        Origin = origin
        ConnectionString = "synthetic-not-opened"
        WitnessConnectionString = "synthetic-not-opened"
        WitnessKeyRingPath = "synthetic-not-opened"
        SuppressionKeyPath = "synthetic-not-opened"
        RecoveryArtifactKeyPath = "synthetic-not-opened"
        StateDirectory = assets
        Certificate = certificate ()
        SessionIdle = TimeSpan.FromMinutes(30.)
        SessionAbsolute = TimeSpan.FromHours(8.)
        Oidc = None
        Admission = admissionLimits loginPermits
    }
