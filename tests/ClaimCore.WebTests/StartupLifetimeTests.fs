module ClaimCore.WebTests.StartupLifetimeTests

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Threading.Tasks
open Expecto
open ClaimCore.Web
open ClaimCore.WebTests.TestServerServices

type private RefusedMetadata() =
    inherit HttpMessageHandler()

    override _.SendAsync(_, _) =
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway))

let private certificatesCloseWhenMetadataFails () =
    use key = RSA.Create(2048)

    let request =
        CertificateRequest(
            "CN=synthetic-issuer",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        )

    use root =
        request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1.),
            DateTimeOffset.UtcNow.AddDays(1.)
        )

    let configuration =
        { testConfiguration "synthetic-not-opened" 5 with
            Oidc =
                Some
                    {
                        Issuer = Uri("https://localhost:5443")
                        TrustRoot = Some root
                        ClientId = "synthetic-web"
                        ClientSecret = "synthetic-secret"
                        ApiAudience = "synthetic-api"
                        ServiceClientId = "synthetic-service"
                        CliClientId = "synthetic-cli"
                    }
        }

    use client = new HttpClient(new RefusedMetadata())

    Expect.throws
        (fun () ->
            use certificates = Configuration.ownCertificates configuration
            configuration.Certificate.GetCertHash() |> ignore
            root.GetCertHash() |> ignore
            OidcStartup.verify client configuration.Oidc.Value)
        "Refused metadata unwinds startup ownership"

    for certificate in [ configuration.Certificate; root ] do
        Expect.throwsT<CryptographicException>
            (fun () -> certificate.GetCertHash() |> ignore)
            "Native certificate resources are closed after startup refusal"

let tests =
    testList
        "startup resource lifetimes"
        [
            testCase
                "[CC-WEB-001] failed OIDC verification closes both startup certificates"
                certificatesCloseWhenMetadataFails
        ]
