module ClaimCore.Tests.RepresentationIngressTests

open System
open System.IO
open System.Net
open System.Security.Claims
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Web
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testCaseAsync
        "actor ingress refuses invalid negotiation before dispatching an otherwise valid mutation"
        (async {
            let directory = PrivateTestPaths.newPrivateDirectory "claimcore-representation-"

            try
                use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)

                let certificateRequest =
                    CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256)

                use certificate =
                    certificateRequest.CreateSelfSigned(
                        DateTimeOffset.UtcNow.AddHours(-1.),
                        DateTimeOffset.UtcNow.AddHours 1.
                    )

                let origin = Uri "https://localhost:5443"

                let configuration =
                    {
                        WebConfiguration.KeyRingDirectory = DirectoryInfo directory
                        Binding = WebBindings.create origin "127.0.0.1" origin.Port
                        ConnectionString = "Host=localhost;Database=synthetic;Username=synthetic"
                        Certificate = certificate
                        SessionIdle = TimeSpan.FromMinutes 10.
                        SessionAbsolute = TimeSpan.FromHours 1.
                        RequestGate =
                            {
                                MaximumJsonBytes = Limits.MaximumRequestBytes
                                CorePermitLimit = 1
                                CoreQueueLimit = 0
                                LoginPermitLimit = 1
                            }
                        Oidc =
                            Some
                                {
                                    Issuer = Uri "https://issuer.example/"
                                    TrustRoot = None
                                    ClientId = "web"
                                    ClientSecret = "synthetic"
                                    ApiAudience = "api"
                                    CliClientId = "cli"
                                }
                    }

                let principal =
                    ClaimsPrincipal(
                        ClaimsIdentity(
                            [ Claim("azp", "synthetic-service"); Claim("sub", "synthetic") ],
                            "synthetic"
                        )
                    )

                let authentication =
                    { new IAuthenticationService with
                        member _.AuthenticateAsync(_, _) =
                            Task.FromResult(
                                AuthenticateResult.Success(
                                    AuthenticationTicket(principal, "synthetic")
                                )
                            )

                        member _.ChallengeAsync(_, _, _) = Task.CompletedTask
                        member _.ForbidAsync(_, _, _) = Task.CompletedTask
                        member _.SignInAsync(_, _, _, _) = Task.CompletedTask
                        member _.SignOutAsync(_, _, _) = Task.CompletedTask
                    }

                let antiforgery =
                    { new IAntiforgery with
                        member _.GetAndStoreTokens _ =
                            invalidOp "Bearer authentication does not use antiforgery"

                        member _.GetTokens _ =
                            invalidOp "Bearer authentication does not use antiforgery"

                        member _.IsRequestValidAsync _ = Task.FromResult true
                        member _.ValidateRequestAsync _ = Task.CompletedTask
                        member _.SetCookieTokenAndHeader _ = ()
                    }

                let dependencies = ServiceCollection()
                dependencies.AddSingleton<IAuthenticationService>(authentication) |> ignore
                dependencies.AddSingleton<IAntiforgery>(antiforgery) |> ignore
                dependencies.AddSingleton<AuthenticationAdmission>() |> ignore

                dependencies.AddSingleton<PrincipalAdmission>(fun _ -> new PrincipalAdmission(1, 0))
                |> ignore

                dependencies.AddScoped<RequestCapacity>() |> ignore
                use provider = dependencies.BuildServiceProvider()
                let mutable dispatched = 0

                let handler =
                    ClaimCore.Web.Route.bind Api.operationExecute (fun _ input _ ->
                        dispatched <- dispatched + 1

                        Task.FromResult(
                            RepresentationReply.Data
                                {
                                    ExecuteReply.OperationId = OperationInput.operationId input
                                    Outcome = ExecuteResult.OutcomeUnknown
                                }
                        ))

                let body =
                    Encoding.UTF8.GetBytes
                        """{"kind":"CASE","operationId":"019aa6a0-0000-7000-8000-000000000001","target":{"kind":"REFERENCE","caseReference":"SYNTHETIC"},"expectedRevision":1,"act":{"kind":"CLOSE"}}"""

                let withActor principal work =
                    task {
                        let services =
                            ClaimCore.Tests.DecisionFixtures.services
                                ClaimCore.Tests.DecisionFixtures.installation
                                ClaimCore.Tests.DecisionFixtures.calendar
                                principal

                        let! result = work services
                        return Some result
                    }

                let send (values: string list) =
                    task {
                        use scope = provider.CreateScope()
                        use input = new MemoryStream(body)
                        use output = new MemoryStream()
                        let context = DefaultHttpContext()
                        context.RequestServices <- scope.ServiceProvider
                        context.Request.Scheme <- "https"
                        context.Request.Method <- "POST"
                        context.Request.Path <- "/api/operation.execute"
                        context.Request.Headers.Host <- origin.Authority
                        context.Request.Headers.Authorization <- "Bearer synthetic"
                        context.Request.Headers["ClaimCore-Contract"] <- ContractIdentity.value

                        context.Request.Headers["ClaimCore-Installation"] <-
                            ClaimCore.Tests.DecisionFixtures.installation.ToString("D")

                        context.Request.Headers[Representation.HeaderName] <-
                            StringValues(values |> List.toArray)

                        context.Request.ContentType <- "application/json"
                        context.Request.ContentLength <- Nullable(int64 body.Length)
                        context.Request.Body <- input
                        context.Response.Body <- output
                        context.Connection.RemoteIpAddress <- IPAddress.Loopback

                        let! result = ApiRoutes.handle handler configuration withActor context
                        do! result.ExecuteAsync context
                        return context.Response.StatusCode, output.ToArray()
                    }

                for values in [ [ "UNKNOWN" ]; [ "HUMAN"; "DATA" ]; [ "" ] ] do
                    let! status, response = send values |> Async.AwaitTask
                    Expect.equal status 400 "Sanitized pre-dispatch transport refusal"

                    Expect.isOk
                        (HostFailureCodecs.decode response)
                        "Host failure remains independently renderable"

                    Expect.equal dispatched 0 "No use case starts for invalid selection"

                let! status, _ = send [ "DATA" ] |> Async.AwaitTask

                Expect.equal
                    status
                    200
                    "The same authenticated request passes with valid negotiation"

                Expect.equal
                    dispatched
                    1
                    "Positive control proves the request was otherwise executable at ingress"
            finally
                Directory.Delete(directory, true)
        })
