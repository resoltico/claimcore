module internal ClaimCore.WebTests.ProbeHttpsFixture

open System
open System.Net
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open ClaimCore.Web

let certificate () =
    use key = RSA.Create(2048)

    let request =
        CertificateRequest(
            "CN=probe.localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        )

    request.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))

    request.CertificateExtensions.Add(
        X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign ||| X509KeyUsageFlags.DigitalSignature,
            true
        )
    )

    let names = SubjectAlternativeNameBuilder()
    names.AddDnsName("probe.localhost")
    request.CertificateExtensions.Add(names.Build())
    let usages = OidCollection()
    usages.Add(Oid("1.3.6.1.5.5.7.3.1")) |> ignore
    request.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(usages, false))

    use issued =
        request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1.),
            DateTimeOffset.UtcNow.AddDays(1.)
        )

    X509CertificateLoader.LoadPkcs12(
        issued.Export(X509ContentType.Pfx),
        "",
        X509KeyStorageFlags.DefaultKeySet
    )

let withServer (work: X509Certificate2 -> WebBinding -> Task<unit>) =
    task {
        use certificate = certificate ()
        let builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders() |> ignore

        builder.WebHost.ConfigureKestrel(fun options ->
            options.Listen(
                IPAddress.Loopback,
                0,
                fun listen -> listen.UseHttps(certificate) |> ignore
            ))
        |> ignore

        use application = builder.Build()

        application.MapGet(
            "/health/live",
            Func<HttpContext, Task>(fun context ->
                context.Response.StatusCode <- 200
                context.Response.Headers["Observed-Host"] <- context.Request.Host.Value
                Task.CompletedTask)
        )
        |> ignore

        application.MapGet(
            "/health/ready",
            Func<HttpContext, Task>(fun context ->
                context.Response.StatusCode <- 503
                Task.CompletedTask)
        )
        |> ignore

        do! application.StartAsync()

        try
            let bound = application.Urls |> Seq.exactlyOne |> Uri

            let binding =
                WebBindings.create (Uri("https://probe.localhost:9443")) "0.0.0.0" bound.Port

            do! work certificate binding
        finally
            application.StopAsync().GetAwaiter().GetResult()
    }
